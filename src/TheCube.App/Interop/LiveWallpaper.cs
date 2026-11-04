using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;
using TheCube.Core;

namespace TheCube.App.Interop;

/// <summary>One live-wallpaper window's picture, in screen pixels.</summary>
internal sealed record LiveWallpaperImage(PixelRect Frame, BitmapSource Image);

/// <summary>What <see cref="LiveWallpaper.Find"/> saw, for the log.</summary>
internal sealed record LiveWallpaperResult(IReadOnlyList<LiveWallpaperImage> Images, IReadOnlyList<string> Windows)
{
    public bool Found => Windows.Count > 0;
}

/// <summary>
/// Animated-wallpaper apps (Lively Wallpaper, Wallpaper Engine and the like) draw in a window placed behind
/// the desktop icons, inside the shell's desktop windows. Windows' wallpaper setting still names the old
/// static picture, so this grabs a frame of those windows instead. The desktop windows are shared by every
/// virtual desktop, so the same frame belongs on every face.
/// </summary>
internal static class LiveWallpaper
{
    private const uint GW_HWNDNEXT = 2;
    private const uint GW_CHILD = 5;

    /// <summary>Find wallpaper windows and capture them. Safe to call from any thread.</summary>
    public static LiveWallpaperResult Find()
    {
        var progman = FindWindow("Progman", null);
        if (progman == 0)
        {
            return new([], []);
        }

        // Where the shell hosts wallpaper windows: a WorkerW inside Progman (Windows 11 24H2 and later), or a
        // top-level WorkerW that doesn't hold the desktop icons (earlier builds). Some apps parent to Progman itself.
        var hosts = new List<(nint Host, nint CaptureFallback)>();
        foreach (var child in Children(progman))
        {
            if (ClassName(child) == "WorkerW")
            {
                hosts.Add((child, progman));
            }
        }

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (ClassName(hwnd) == "WorkerW" && FindWindowEx(hwnd, 0, "SHELLDLL_DefView", null) == 0)
            {
                hosts.Add((hwnd, hwnd));
            }

            return true;
        }, 0);

        var windows = new List<(nint Hwnd, nint Host, nint CaptureFallback)>();
        foreach (var (host, fallback) in hosts)
        {
            windows.AddRange(Children(host).Select(w => (w, host, fallback)));
        }

        windows.AddRange(Children(progman)
            .Where(w => ClassName(w) is not ("SHELLDLL_DefView" or "WorkerW"))
            .Select(w => (w, progman, progman)));

        var images = new List<LiveWallpaperImage>();
        var names = new List<string>();
        foreach (var (hwnd, host, fallback) in windows.Distinct())
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || !Capture.GetWindowRect(hwnd, out var rect))
            {
                continue;
            }

            var frame = rect.ToPixelRect();
            if (frame.IsEmpty)
            {
                continue;
            }

            names.Add(Describe(hwnd));

            // The app's own window first (just the wallpaper), then the windows around it.
            var image = Capture.Window(hwnd, frame) ?? Capture.Window(host, frame) ?? Capture.Window(fallback, frame);
            if (image is not null)
            {
                images.Add(new LiveWallpaperImage(frame, image));
            }
        }

        return new(images, names);
    }

    private static IEnumerable<nint> Children(nint parent)
    {
        var list = new List<nint>();
        for (var child = NativeMethods.GetWindow(parent, GW_CHILD); child != 0 && list.Count < 256; child = NativeMethods.GetWindow(child, GW_HWNDNEXT))
        {
            list.Add(child);
        }

        return list;
    }

    /// <summary>"ClassName (process)" for the log; never the window title.</summary>
    private static string Describe(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return $"{ClassName(hwnd)} ({p.ProcessName})";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"{ClassName(hwnd)} (?)";
        }
    }

    private static string ClassName(nint hwnd)
    {
        var sb = new StringBuilder(64);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint childAfter, string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}
