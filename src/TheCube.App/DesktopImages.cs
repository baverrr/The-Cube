using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TheCube.App.Interop;
using TheCube.Core;

namespace TheCube.App;

internal enum LayerSource
{
    /// <summary>Captured just now.</summary>
    Live,

    /// <summary>Couldn't capture now; this is the window's last good image.</summary>
    Cached,
}

/// <summary>A window image placed on a face, relative to the monitor's top-left corner.</summary>
internal sealed record WindowLayer(nint Handle, BitmapSource Image, PixelRect Frame, LayerSource Source);

/// <summary>Everything needed to draw one desktop on one monitor.</summary>
internal sealed record DesktopScene(
    int Desktop,
    MonitorInfo Monitor,
    Color Background,
    BitmapSource? Wallpaper,
    IReadOnlyList<PixelRect> WallpaperRects,
    IReadOnlyList<LiveWallpaperImage> LiveWallpaper,
    IReadOnlyList<WindowLayer> Windows,
    int MissingWindows,
    BitmapSource? Screenshot)
{
    public bool IsCurrent => Screenshot is not null;

    /// <summary>An animated-wallpaper app's frame replaces the static wallpaper.</summary>
    public bool HasLiveWallpaper => LiveWallpaper.Count > 0;
}

/// <summary>
/// Builds a picture of every desktop on every monitor, like KWin's per-desktop views:
/// the current desktop is a screen grab; other desktops are their wallpaper plus each window,
/// rendered by Windows (PrintWindow) and stacked in z-order. Windows that can't be captured fall back
/// to their last good image, or are left out.
/// </summary>
internal sealed class DesktopImageProvider(WindowsVirtualDesktops desktops)
{
    private const int DWM_CLOAKED_APP = 0x1;
    private const int DWM_CLOAKED_INHERITED = 0x4;

    private readonly WindowImageCache<BitmapSource> _windowCache = new();
    private readonly Dictionary<string, (DateTime Written, BitmapSource Image)> _wallpaperCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private IReadOnlyList<LiveWallpaperImage> _lastLiveWallpaper = [];
    private string _lastLiveWallpaperLog = "";
    private readonly HashSet<string> _loggedWallpaperErrors = [];

    public TimeSpan LastBuildTime { get; private set; }

    /// <summary>
    /// Grab every monitor as it looks now, in parallel (whether that's faster shows in the open log's "screens"
    /// time). Call before showing any overlay; blocks the calling thread until every monitor is copied. A monitor
    /// that can't be copied gets null. If one copy throws, its own exception comes out (not an AggregateException);
    /// if several do, an AggregateException. Either way the other monitors' copies are dropped.
    /// </summary>
    public static Dictionary<MonitorInfo, BitmapSource?> CaptureScreens(IReadOnlyList<MonitorInfo> monitors)
    {
        var shots = new BitmapSource?[monitors.Count];
        try
        {
            Parallel.For(0, monitors.Count, i => shots[i] = Capture.Screen(monitors[i].Bounds));
        }
        catch (AggregateException ae) when (ae.InnerExceptions.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerExceptions[0]).Throw();
        }

        return monitors.Select((m, i) => (m, shots[i])).ToDictionary(p => p.m, p => p.Item2);
    }

    /// <summary>
    /// Build scenes for every desktop × monitor. Call on the UI thread (the desktop interfaces are
    /// single-threaded); slow window captures run on worker threads.
    /// </summary>
    public async Task<IReadOnlyList<DesktopScene>> BuildAsync(
        IReadOnlyList<MonitorInfo> monitors, Dictionary<MonitorInfo, BitmapSource?> screens)
    {
        var clock = Stopwatch.StartNew();
        var desktopList = desktops.GetDesktops();
        var current = desktops.GetCurrentIndex();
        var wallpapers = WallpaperSettings.Read();
        var windows = EnumerateWindows(desktopList);
        var virtualScreen = Monitors.VirtualScreen(monitors);

        var images = await Task.Run(() => CaptureWindows(windows));
        var live = await Task.Run(CaptureLiveWallpaper);

        var scenes = new List<DesktopScene>();
        foreach (var monitor in monitors)
        {
            foreach (var desktop in desktopList)
            {
                if (desktop.Index == current && screens.GetValueOrDefault(monitor) is { } screen)
                {
                    scenes.Add(new DesktopScene(desktop.Index, monitor, wallpapers.Background, null, [], [], [], 0, screen));
                    continue;
                }

                var liveHere = live
                    .Where(l => l.Frame.IntersectsWith(monitor.Bounds))
                    .Select(l => l with { Frame = l.Frame.RelativeTo(monitor.Bounds) })
                    .ToList();
                var path = liveHere.Count > 0 ? null : desktop.WallpaperPath ?? wallpapers.PathFor(monitor);
                var wallpaper = path is null ? null : await Task.Run(() => LoadWallpaper(path, monitors));
                var rects = wallpaper is null
                    ? []
                    : WallpaperLayout.Place(wallpapers.Position, wallpaper.PixelWidth, wallpaper.PixelHeight, monitor.Bounds, virtualScreen);

                var layers = new List<WindowLayer>();
                var missing = 0;
                foreach (var w in SceneBuilder.WindowsFor(windows, desktop.Index, monitor.Bounds))
                {
                    if (images.TryGetValue(w.Handle, out var layer) && layer is not null)
                    {
                        layers.Add(layer with { Frame = w.Frame.RelativeTo(monitor.Bounds) });
                    }
                    else
                    {
                        missing++;
                    }
                }

                scenes.Add(new DesktopScene(desktop.Index, monitor, wallpapers.Background, wallpaper, rects, liveHere, layers, missing, null));
            }
        }

        LastBuildTime = clock.Elapsed;
        return scenes;
    }

    private List<WindowEntry> EnumerateWindows(IReadOnlyList<DesktopInfo> desktopList)
    {
        var result = ListWindows(desktops, desktopList);
        lock (_lock)
        {
            _windowCache.Prune(result.Select(w => w.Handle));
        }

        return result;
    }

    /// <summary>
    /// App windows on every desktop (plus the taskbars, unless told not to), top to bottom. Call on the UI thread (the desktop
    /// interfaces are single-threaded).
    /// </summary>
    internal static List<WindowEntry> ListWindows(WindowsVirtualDesktops desktops, IReadOnlyList<DesktopInfo> desktopList, bool includeTaskbars = true)
    {
        var candidates = new List<(nint Handle, bool Taskbar)>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var taskbar = IsTaskbar(hwnd);
            if (taskbar && !includeTaskbars)
            {
                return true;
            }

            if (taskbar ? NativeMethods.IsWindowVisible(hwnd) : IsCandidate(hwnd))
            {
                candidates.Add((hwnd, taskbar));
            }

            return true;
        }, 0);

        var onDesktop = desktops.GetWindowDesktops(candidates.Where(c => !c.Taskbar).Select(c => c.Handle), desktopList);
        var result = new List<WindowEntry>();
        foreach (var (hwnd, taskbar) in candidates)
        {
            int? desktop = taskbar ? null : onDesktop.GetValueOrDefault(hwnd);
            var allDesktops = taskbar || desktop == -1;
            if (!allDesktops && desktop is null)
            {
                continue; // not a window Windows puts on a desktop
            }

            result.Add(new WindowEntry(hwnd, Capture.GetFrame(hwnd), allDesktops ? null : desktop, allDesktops, NativeMethods.IsIconic(hwnd)));
        }

        return result;
    }

    /// <summary>Capture each window once (it may appear on several monitors), falling back to the cache.</summary>
    private Dictionary<nint, WindowLayer?> CaptureWindows(IReadOnlyList<WindowEntry> windows)
    {
        var result = new Dictionary<nint, WindowLayer?>();
        var todo = windows.Where(w => !w.Minimized && !w.Frame.IsEmpty).ToList();
        var captured = new BitmapSource?[todo.Count];
        Parallel.For(0, todo.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            captured[i] = Capture.Window(todo[i].Handle, todo[i].Frame));

        lock (_lock)
        {
            for (var i = 0; i < todo.Count; i++)
            {
                var w = todo[i];
                if (captured[i] is { } image)
                {
                    _windowCache.Put(w.Handle, w.Frame.Width, w.Frame.Height, image);
                    result[w.Handle] = new WindowLayer(w.Handle, image, w.Frame, LayerSource.Live);
                }
                else if (_windowCache.Get(w.Handle, w.Frame.Width, w.Frame.Height) is { } cached)
                {
                    result[w.Handle] = new WindowLayer(w.Handle, cached, w.Frame, LayerSource.Cached);
                }
                else
                {
                    result[w.Handle] = null;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// A frame of an animated-wallpaper app, if one is running. If it's running but this capture failed,
    /// the last good frame is used rather than dropping back to the old static picture.
    /// </summary>
    private IReadOnlyList<LiveWallpaperImage> CaptureLiveWallpaper()
    {
        var result = LiveWallpaper.Find();
        var log = result.Found
            ? $"live wallpaper: windows=[{string.Join(", ", result.Windows)}] captured={result.Images.Count}"
            : "live wallpaper: none";
        lock (_lock)
        {
            if (log != _lastLiveWallpaperLog)
            {
                _lastLiveWallpaperLog = log;
                EventLog.Write(log); // only when it changes, to keep the log short
            }

            if (!result.Found)
            {
                _lastLiveWallpaper = [];
            }
            else if (result.Images.Count > 0)
            {
                _lastLiveWallpaper = result.Images;
            }

            return _lastLiveWallpaper;
        }
    }

    private BitmapSource? LoadWallpaper(string path, IReadOnlyList<MonitorInfo> monitors)
    {
        try
        {
            var written = File.GetLastWriteTimeUtc(path);
            lock (_lock)
            {
                if (_wallpaperCache.TryGetValue(path, out var hit) && hit.Written == written)
                {
                    return hit.Image;
                }
            }

            // Decode at no more than the virtual screen's width (enough for any position, Span included) to save memory.
            var maxWidth = Math.Max(monitors.Max(m => m.Bounds.Width), Monitors.VirtualScreen(monitors).Width);
            using var stream = File.OpenRead(path);
            var probe = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            if (probe.PixelWidth > maxWidth)
            {
                image.DecodePixelWidth = maxWidth;
            }

            image.EndInit();
            image.Freeze();
            lock (_lock)
            {
                _wallpaperCache[path] = (written, image);
            }

            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException or COMException)
        {
            // Missing, slideshow mid-change, or a format WIC can't read: draw the background colour.
            // Logged once per file, by name only (the folder can contain the user name).
            var message = $"wallpaper: couldn't load {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}";
            lock (_lock)
            {
                if (_loggedWallpaperErrors.Add(message))
                {
                    EventLog.Write(message);
                }
            }

            return null;
        }
    }

    private static bool IsTaskbar(nint hwnd)
    {
        var name = ClassName(hwnd);
        return name is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary>An app window, possibly on another desktop (cloaked by the shell, but not by the app).</summary>
    private static bool IsCandidate(nint hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd))
        {
            return false;
        }

        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
            && (cloaked & (DWM_CLOAKED_APP | DWM_CLOAKED_INHERITED)) != 0)
        {
            return false;
        }

        var exStyle = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_APPWINDOW) != 0)
        {
            return true;
        }

        const uint GW_OWNER = 4;
        return (exStyle & (NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE)) == 0
            && NativeMethods.GetWindow(hwnd, GW_OWNER) == 0
            && NativeMethods.GetWindowTextLength(hwnd) > 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder name, int max);

    private static string ClassName(nint hwnd)
    {
        var sb = new StringBuilder(64);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }
}

/// <summary>Turns a <see cref="DesktopScene"/> into something drawable: a brush for a cube face, or a bitmap.</summary>
internal static class FaceRenderer
{
    /// <summary>The scene as a drawing in monitor pixel coordinates (0,0 to width,height).</summary>
    public static Drawing ToDrawing(DesktopScene scene)
    {
        var full = new Rect(0, 0, scene.Monitor.Bounds.Width, scene.Monitor.Bounds.Height);
        var group = new DrawingGroup { ClipGeometry = new RectangleGeometry(full) };
        if (scene.Screenshot is { } shot)
        {
            group.Children.Add(new ImageDrawing(shot, full));
        }
        else
        {
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(scene.Background), null, new RectangleGeometry(full)));
            foreach (var live in scene.LiveWallpaper)
            {
                group.Children.Add(new ImageDrawing(live.Image, ToRect(live.Frame)));
            }

            if (!scene.HasLiveWallpaper && scene.Wallpaper is { } wallpaper)
            {
                foreach (var r in scene.WallpaperRects)
                {
                    group.Children.Add(new ImageDrawing(wallpaper, ToRect(r)));
                }
            }

            foreach (var layer in scene.Windows)
            {
                group.Children.Add(new ImageDrawing(layer.Image, ToRect(layer.Frame)));
            }
        }

        group.Freeze();
        return group;
    }

    /// <summary>
    /// A brush for a cube face. Composed faces are cached as a bitmap by WPF, so the many layers aren't
    /// redrawn every frame of the animation.
    /// </summary>
    public static Brush ToBrush(DesktopScene scene)
    {
        Brush brush;
        if (scene.Screenshot is { } shot)
        {
            brush = new ImageBrush(shot) { Stretch = Stretch.Fill };
        }
        else
        {
            var full = new Rect(0, 0, scene.Monitor.Bounds.Width, scene.Monitor.Bounds.Height);
            brush = new DrawingBrush(ToDrawing(scene))
            {
                Stretch = Stretch.Fill,
                Viewbox = full,
                ViewboxUnits = BrushMappingMode.Absolute,
            };
            RenderOptions.SetCachingHint(brush, CachingHint.Cache);
            RenderOptions.SetCacheInvalidationThresholdMinimum(brush, 0.5);
            RenderOptions.SetCacheInvalidationThresholdMaximum(brush, 2.0);
        }

        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        brush.Freeze();
        return brush;
    }

    /// <summary>Draw the scene into a bitmap. <paramref name="scale"/> 1 = full monitor resolution.</summary>
    public static BitmapSource Render(DesktopScene scene, double scale = 1)
    {
        var width = Math.Max(1, (int)Math.Round(scene.Monitor.Bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Round(scene.Monitor.Bounds.Height * scale));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawDrawing(ToDrawing(scene));
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Rect ToRect(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);
}
