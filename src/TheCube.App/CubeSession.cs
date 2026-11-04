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
    private bool _opening;
    private double _openedAt = double.NegativeInfinity;
    private double _closingSince = double.NaN;
    private CubeOverlay? _focused;

    public CubeSession(App app)
    {
        _app = app;

        // KWin's cube never closes because focus moved, and Windows can steal focus for a moment just
        // after the cube opens (its own Win+C handling, the Start menu, foreground rules). So: log it,
        // and if it happens while the cube is still opening, take focus back.
        Application.Current.Deactivated += (_, _) => OnAppDeactivated();
        Application.Current.Activated += (_, _) => EventLog.Write("app activated");
    }

    public bool IsOpen => _overlays.Count > 0;

    private double Now() => _clock.Elapsed.TotalMilliseconds;

    private double Duration => _app.Settings.AnimationDurationMs;

    public void Toggle()
    {
        EventLog.Write($"toggle: open={IsOpen} opening={_opening} activated={_overlays.Count(o => o.Controller.Activated)}/{_overlays.Count} foreground={EventLog.ForegroundWindow()}");
        if (_opening)
        {
            return;
        }

        if (IsOpen && !_overlays.Any(o => o.Controller.Activated))
        {
            // Closing. Normally that ends within the animation; if it hasn't (something went wrong),
            // clean up so the cube can't get stuck "open" and ignore the hotkey for good.
            if (!double.IsNaN(_closingSince) && Now() - _closingSince > Duration + 1500)
            {
                EventLog.Write("recovering: the cube was stuck closing");
                CloseOverlays();
                _ = OpenAsync();
            }

            return;
        }

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

    private void OnAppDeactivated()
    {
        EventLog.Write($"app deactivated: foreground={EventLog.ForegroundWindow()} open={IsOpen}");
        if (IsOpen && _focused is { } overlay && overlay.Controller.Activated && Now() - _openedAt < Duration + 500)
        {
            EventLog.Write("taking focus back (lost while opening)");
            FocusOverlay(overlay);
        }
    }

    private static void FocusOverlay(CubeOverlay overlay)
    {
        ForceForeground(new System.Windows.Interop.WindowInteropHelper(overlay).Handle);
        overlay.Activate();
        overlay.Focus();
    }

    /// <param name="previousForeground">Window to give focus back to on Esc; by default the current foreground window.</param>
    private async Task OpenAsync(nint? previousForeground = null)
    {
        _opening = true;
        try
        {
            await OpenCoreAsync(previousForeground);
        }
        catch (Exception ex)
        {
            EventLog.Write("open failed: " + ex);
            CloseOverlays();
            throw;
        }
        finally
        {
            _opening = false;
        }
    }

    private async Task OpenCoreAsync(nint? previousForeground)
    {
        if (_app.Desktops is not { } desktops || _app.Images is not { } images)
        {
            EventLog.Write("open: no virtual desktops");
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
            EventLog.Write("open: reading desktops failed: " + ex.Message);
            _app.ReportStatus("The cube can't open: " + ex.Message);
            return;
        }

        var generation = ++_generation;
        _closingSince = double.NaN;
        _previousForeground = previousForeground ?? GetForegroundWindow();
        _switchTarget = null;
        _hiddenCount = 0;

        // Grab the screens before any overlay covers them; the current face starts as this exact image,
        // so the zoom-out begins from what was on screen.
        var monitors = Monitors.All();
        var screens = DesktopImageProvider.CaptureScreens(monitors);
        EventLog.Write($"open: desktops={count} current={current + 1} monitors={monitors.Count} foreground={EventLog.ForegroundWindow()}");

        // Create and start every monitor's cube before showing any, so a focus change while windows
        // appear can't close some cubes while others are still being made.
        var created = new List<CubeOverlay>();
        foreach (var monitor in monitors)
        {
            var overlay = new CubeOverlay(monitor, _app.Settings, Now);
            overlay.Controller.Closing += desktop => FollowClose(overlay, desktop);
            overlay.Controller.SwitchRequested += desktop => _switchTarget = desktop;
            overlay.Controller.Hidden += OnOverlayHidden;
            overlay.AddDesktopRequested += AddDesktopAndReopen;
            created.Add(overlay);
        }

        _overlays.AddRange(created);
        _openedAt = Now();
        foreach (var overlay in created)
        {
            Brush face = screens.GetValueOrDefault(overlay.Monitor) is { } shot
                ? new ImageBrush(shot) { Stretch = Stretch.Fill }
                : new SolidColorBrush(Colors.Black);
            face.Freeze();
            overlay.Start(current, count, face);
        }

        foreach (var overlay in created)
        {
            overlay.Show();
        }

        // Keyboard goes to the monitor under the pointer, as in Plasma.
        GetCursorPos(out var cursor);
        _focused = _overlays.FirstOrDefault(o => Contains(o.Monitor.Bounds, cursor.X, cursor.Y)) ?? _overlays.FirstOrDefault();
        if (_focused is not null)
        {
            FocusOverlay(_focused);
        }

        EventLog.Write($"open: shown, foreground={EventLog.ForegroundWindow()}");

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
            EventLog.Write("capture failed: " + ex.Message);
            _app.ReportStatus("Couldn't capture the other desktops: " + ex.Message);
            return;
        }

        EventLog.Write($"faces captured in {images.LastBuildTime.TotalMilliseconds:0} ms");

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
        if (double.IsNaN(_closingSince))
        {
            _closingSince = now;
            EventLog.Write($"closing onto desktop {desktop + 1}");
        }

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
        try
        {
            if (target is int desktop && _app.Desktops is { } desktops)
            {
                // While the overlays still show the target face, so there's no flash of the old desktop.
                desktops.SwitchTo(desktop, animate: false);
                EventLog.Write($"switched to desktop {desktop + 1}");
            }
            else if (_previousForeground != 0)
            {
                SetForegroundWindow(_previousForeground);
                EventLog.Write("closed without switching");
            }
        }
        catch (Exception ex)
        {
            EventLog.Write("switch failed: " + ex);
            _app.ReportStatus("Couldn't switch desktop: " + ex.Message);
        }
        finally
        {
            CloseOverlays(); // always, so the cube can't stay stuck open
        }
    }

    private void CloseOverlays()
    {
        foreach (var overlay in _overlays)
        {
            overlay.Stop();
            overlay.Close();
        }

        _overlays.Clear();
        _focused = null;
        _closingSince = double.NaN;
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

        // Like Plasma, the cube appears once there are enough desktops.
        EventLog.Write("added a desktop from the placeholder");
        var previous = _previousForeground;
        CloseOverlays();
        _ = OpenAsync(previous);
    }

    /// <summary>
    /// Bring our window to the front even when the triggering input went to another app (keyboard-hook
    /// hotkeys and screen edges), which Windows' foreground rules would otherwise block.
    /// </summary>
    private static void ForceForeground(nint hwnd)
    {
        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var ourThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != ourThread && AttachThreadInput(ourThread, foregroundThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(ourThread, foregroundThread, false);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hwnd);

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
