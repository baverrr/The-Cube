using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using TheCube.App.Interop;
using TheCube.Core;

namespace TheCube.App;

/// <summary>
/// Opens the cube on every monitor and closes them together. Picking a desktop on one monitor makes
/// every monitor zoom into it; the actual desktop switch happens once all of them have finished
/// (the overlays live on the old desktop and would vanish if Windows switched mid-animation).
/// </summary>
internal sealed class CubeSession
{
    private readonly App _app;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<CubeOverlay> _overlays = [];
    private int? _switchTarget;
    private int _hiddenCount;
    private nint _previousForeground;
    private int _generation;

    public CubeSession(App app)
    {
        _app = app;
        Application.Current.Deactivated += (_, _) => CloseIfOpen(); // e.g. the Start menu opened
    }

    public bool IsOpen => _overlays.Count > 0;

    private double Now() => _clock.Elapsed.TotalMilliseconds;

    public void Toggle()
    {
        if (IsOpen)
        {
            CloseIfOpen();
        }
        else
        {
            _ = OpenAsync();
        }
    }

    private void CloseIfOpen()
    {
        // Closing one cascades to the others through CubeController.Closing.
        _overlays.FirstOrDefault(o => o.Controller.Activated)?.Controller.Deactivate(Now());
    }

    /// <param name="previousForeground">Window to give focus back to on Esc; by default the current foreground window.</param>
    private async Task OpenAsync(nint? previousForeground = null)
    {
        if (_app.Desktops is not { } desktops || _app.Images is not { } images)
        {
            _app.ReportStatus("The cube can't open: virtual desktops aren't available on this Windows build.");
            return;
        }

        int count, current;
        try
        {
            count = desktops.GetDesktops().Count;
            current = desktops.GetCurrentIndex();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            _app.ReportStatus("The cube can't open: " + ex.Message);
            return;
        }

        var generation = ++_generation;
        _previousForeground = previousForeground ?? GetForegroundWindow();
        _switchTarget = null;
        _hiddenCount = 0;

        // Grab the screens before any overlay covers them; the current face starts as this exact image,
        // so the zoom-out begins from what was on screen.
        var monitors = Monitors.All();
        var screens = DesktopImageProvider.CaptureScreens(monitors);

        foreach (var monitor in monitors)
        {
            var overlay = new CubeOverlay(monitor, _app.Settings, Now);
            overlay.Controller.Closing += desktop => FollowClose(overlay, desktop);
            overlay.Controller.SwitchRequested += desktop => _switchTarget = desktop;
            overlay.Controller.Hidden += OnOverlayHidden;
            overlay.AddDesktopRequested += AddDesktopAndReopen;
            _overlays.Add(overlay);

            Brush face = screens.GetValueOrDefault(monitor) is { } shot
                ? new ImageBrush(shot) { Stretch = Stretch.Fill }
                : new SolidColorBrush(Colors.Black);
            face.Freeze();
            overlay.Start(current, count, face);
            overlay.Show();
        }

        // Keyboard goes to the monitor under the pointer, as in Plasma.
        GetCursorPos(out var cursor);
        var focused = _overlays.FirstOrDefault(o => Contains(o.Monitor.Bounds, cursor.X, cursor.Y)) ?? _overlays.FirstOrDefault();
        focused?.Activate();
        focused?.Focus();

        if (count < CubeGeometry.MinimumFaces)
        {
            return;
        }

        // Fill in the other desktops' faces as soon as they're ready (usually during the zoom-out).
        IReadOnlyList<DesktopScene> scenes;
        try
        {
            scenes = await images.BuildAsync(monitors, screens);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            _app.ReportStatus("Couldn't capture the other desktops: " + ex.Message);
            return;
        }

        if (generation != _generation || !IsOpen)
        {
            return; // closed (or reopened) while capturing
        }

        foreach (var scene in scenes)
        {
            if (scene.IsCurrent)
            {
                continue;
            }

            _overlays.FirstOrDefault(o => o.Monitor == scene.Monitor)?.SetFace(scene.Desktop, FaceRenderer.ToBrush(scene));
        }
    }

    private void FollowClose(CubeOverlay source, int desktop)
    {
        var now = Now();
        foreach (var overlay in _overlays)
        {
            if (overlay != source)
            {
                overlay.Controller.CloseTo(desktop, now);
            }
        }
    }

    private void OnOverlayHidden()
    {
        if (++_hiddenCount < _overlays.Count)
        {
            return;
        }

        var target = _switchTarget;
        _switchTarget = null;
        if (target is int desktop && _app.Desktops is { } desktops)
        {
            try
            {
                // While the overlays still show the target face, so there's no flash of the old desktop.
                desktops.SwitchTo(desktop, animate: false);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                _app.ReportStatus("Couldn't switch desktop: " + ex.Message);
            }
        }
        else if (_previousForeground != 0)
        {
            SetForegroundWindow(_previousForeground);
        }

        CloseOverlays();
    }

    private void CloseOverlays()
    {
        foreach (var overlay in _overlays)
        {
            overlay.Stop();
            overlay.Close();
        }

        _overlays.Clear();
        _generation++;
    }

    private void AddDesktopAndReopen()
    {
        try
        {
            _app.Desktops?.CreateDesktop();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            _app.ReportStatus("Couldn't add a desktop: " + ex.Message);
            return;
        }

        // Like Plasma, the cube appears once there are enough desktops. Focus stays with The Cube, so
        // the app-deactivated handler doesn't close the cube that's about to open.
        var previous = _previousForeground;
        CloseOverlays();
        _ = OpenAsync(previous);
    }

    private static bool Contains(PixelRect r, int x, int y) => x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);
}
