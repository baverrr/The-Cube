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
    private int _openSequence;
    private bool _opening;
    private double _openedAt = double.NegativeInfinity;
    private double _closingSince = double.NaN;
    private CubeOverlay? _focused;
    private readonly FrameStats _frames = new();
    private bool _countingFrames;
    private double _openStartedAt;
    private bool _revealLogged;
    private bool _warmingUp;
    private bool _openedDuringWarmUp;

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

    /// <summary>
    /// Do most of the slow one-time work of a first open (code loading, WPF's 3D pipeline, the graphics driver's
    /// caches) with invisible windows, so the first real open is closer to the speed of later ones.
    /// Never throws: it's only a head start, so a failure just leaves the first open slower, and is logged.
    /// Safe to overlap with a real open: these windows aren't in the session's list.
    /// </summary>
    public async Task WarmUpAsync()
    {
        _warmingUp = true;
        _openedDuringWarmUp = false;
        try
        {
            var clock = Stopwatch.StartNew();
            var tier = RenderCapability.Tier >> 16; // high word: 2 = most drawing on the GPU, 1 = some, 0 = software
            var monitors = Monitors.All();
            var screens = DesktopImageProvider.CaptureScreens(monitors);
            var captured = clock.ElapsedMilliseconds;
            var missing = screens.Values.Count(s => s is null);
            var results = await Task.WhenAll(monitors.Select(async monitor =>
            {
                try
                {
                    Brush face = screens.GetValueOrDefault(monitor) is { } shot ? new ImageBrush(shot) { Stretch = Stretch.Fill } : new SolidColorBrush(Colors.Black);
                    face.Freeze();
                    return await CubeOverlay.WarmUpAsync(monitor, _app.Settings, Now, face);
                }
                catch (Exception ex)
                {
                    return $"{monitor.Bounds.Width}x{monitor.Bounds.Height} failed: {ex}";
                }
            }));
            EventLog.Write($"warm-up in {clock.ElapsedMilliseconds} ms (screens {captured} ms{(missing > 0 ? $", {missing} not captured" : "")}), render tier {tier}{(_openedDuringWarmUp ? ", overlapped a real open" : "")}: {string.Join("; ", results)}");
        }
        catch (Exception ex)
        {
            EventLog.Write("warm-up failed: " + ex);
        }
        finally
        {
            _warmingUp = false;
        }
    }

    /// <summary>
    /// Count one UI frame tick, by WPF's own frame time: Rendering can be raised more than once per frame, and
    /// those repeats carry the same time, so <see cref="FrameStats"/> skips them.
    /// </summary>
    private void CountFrame(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs rendering)
        {
            _frames.RecordFrame(rendering.RenderingTime.TotalMilliseconds);
        }
    }

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

    /// <param name="previousForeground">Window to give focus back to when the cube closes without switching; by default the current foreground window.</param>
    /// <remarks>Started fire-and-forget, so it must never throw: failures are logged and reported here.</remarks>
    private async Task OpenAsync(nint? previousForeground = null)
    {
        _opening = true;
        var token = ++_openSequence;
        var startGeneration = _generation;
        try
        {
            await OpenCoreAsync(previousForeground);
        }
        catch (Exception ex)
        {
            EventLog.Write("open failed: " + ex);
            _app.ReportFailure("The cube couldn't open: " + ex.Message);

            // Only this open's cube: OpenCoreAsync bumps the generation once, CloseOverlays bumps it again.
            if (token == _openSequence && _generation <= startGeneration + 1)
            {
                CloseOverlays();
            }
        }
        finally
        {
            if (token == _openSequence)
            {
                _opening = false; // a newer open owns the flag otherwise
            }

            if (!IsOpen)
            {
                StopCountingFrames(); // a failed open with no cube left: don't keep WPF drawing every frame
            }
        }
    }

    private async Task OpenCoreAsync(nint? previousForeground)
    {
        var phase = Stopwatch.StartNew();
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
        _openedDuringWarmUp |= _warmingUp;
        _closingSince = double.NaN;
        _previousForeground = previousForeground ?? GetForegroundWindow();
        _switchTarget = null;
        _hiddenCount = 0;

        // Grab the screens before any overlay covers them; the current face starts as this exact image,
        // so the zoom-out begins from what was on screen.
        var desktopsMs = phase.ElapsedMilliseconds;
        var monitors = Monitors.All();
        var screens = DesktopImageProvider.CaptureScreens(monitors);
        var captureMs = phase.ElapsedMilliseconds;

        // Create and start every monitor's cube before showing any, so all of them are in _overlays
        // before focus can move and OnAppDeactivated runs.
        var created = new List<CubeOverlay>();
        foreach (var monitor in monitors)
        {
            var overlay = new CubeOverlay(monitor, _app.Settings, Now);
            overlay.Controller.Closing += desktop => FollowClose(overlay, desktop);
            overlay.Controller.SwitchRequested += desktop => _switchTarget = desktop;
            overlay.Controller.Hidden += OnOverlayHidden;
            overlay.AddDesktopRequested += AddDesktopAndReopen;
            overlay.Revealed += byFallback => OnRevealed(generation, byFallback);
            created.Add(overlay);
        }

        _overlays.AddRange(created);
        _openedAt = Now();
        _openStartedAt = _openedAt - phase.Elapsed.TotalMilliseconds;
        _revealLogged = false;

        // Count frames from before the first one is drawn, so a slow first frame shows up too.
        _frames.Reset();
        if (!_countingFrames)
        {
            CompositionTarget.Rendering += CountFrame; // removed on close: any Rendering handler keeps WPF drawing every frame
            _countingFrames = true;
        }
        foreach (var overlay in created)
        {
            Brush face = screens.GetValueOrDefault(overlay.Monitor) is { } shot
                ? new ImageBrush(shot) { Stretch = Stretch.Fill }
                : new SolidColorBrush(Colors.Black);
            face.Freeze();
            overlay.Start(current, count, face);
        }

        var startMs = phase.ElapsedMilliseconds;
        foreach (var overlay in created)
        {
            overlay.Show();
        }

        var showMs = phase.ElapsedMilliseconds;

        // Keyboard goes to the monitor under the pointer, as in Plasma.
        GetCursorPos(out var cursor);
        _focused = _overlays.FirstOrDefault(o => Contains(o.Monitor.Bounds, cursor.X, cursor.Y)) ?? _overlays.FirstOrDefault();
        if (_focused is not null)
        {
            FocusOverlay(_focused);
        }

        // Windows are created and queued for drawing here, not yet visible: OnRevealed logs when they are.
        var queuedMs = phase.ElapsedMilliseconds;
        EventLog.Write($"open: desktops={count} current={current + 1} monitors={monitors.Count}{(screens.Values.Any(s => s is null) ? " (a screen wasn't captured)" : "")}, queued in {queuedMs} ms " +
                       $"(desktops {desktopsMs}, screens {captureMs - desktopsMs}, create+start {startMs - captureMs}, show {showMs - startMs}, focus {queuedMs - showMs}), " +
                       $"previous foreground 0x{_previousForeground:X}, now {EventLog.ForegroundWindow()}");

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
        catch (Exception ex)
        {
            // The cube still works with placeholder faces; don't close it over a capture problem.
            EventLog.Write("capture failed: " + ex);
            _app.ReportStatus("Couldn't capture the other desktops: " + ex.Message);
            return;
        }

        EventLog.Write($"faces captured in {images.LastBuildTime.TotalMilliseconds:0} ms");
        if (generation != _generation || !IsOpen)
        {
            EventLog.Write("faces discarded: the cube closed or reopened while they were captured");
            return;
        }

        var applyStart = Now();
        foreach (var scene in scenes)
        {
            if (scene.IsCurrent)
            {
                continue;
            }

            _overlays.FirstOrDefault(o => o.Monitor == scene.Monitor)?.SetFace(scene.Desktop, FaceRenderer.ToBrush(scene));
        }

        var applied = Now();
        EventLog.Write($"faces applied in {applied - applyStart:0} ms, {applied - _openStartedAt:0} ms after the open started");
    }

    private void StopCountingFrames()
    {
        if (_countingFrames)
        {
            CompositionTarget.Rendering -= CountFrame;
            _countingFrames = false;

            // UI-thread ticks for all monitors together, from open to close (animations and idle time alike).
            var where = _overlays.Count > 0 ? $"{_overlays.Count} monitors" : "open failed";
            EventLog.Write($"frames while open ({where}): {_frames.Summary()}");
        }
    }

    /// <summary>
    /// Log when the cube first became visible (uncloaked; on screen about one frame later), which is what the user
    /// waits for after the hotkey, and any monitor that was only shown by the fallback.
    /// </summary>
    private void OnRevealed(int generation, bool byFallback)
    {
        if (generation != _generation || (_revealLogged && !byFallback))
        {
            return;
        }

        _revealLogged = true;
        EventLog.Write($"open: uncloaked {Now() - _openStartedAt:0} ms after the open started{(byFallback ? " (no frame drawn within 300 ms; shown anyway)" : "")}");
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
        StopCountingFrames();

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
            _app.ReportFailure("Couldn't add a desktop: " + ex.Message);
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
