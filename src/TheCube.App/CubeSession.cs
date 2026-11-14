using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
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
    private bool _reopenAfterClose; // open again once the close ends: Toggle was called during the closing animation
    private CubeOverlay? _focused;
    private readonly FrameStats _frames = new();
    private bool _countingFrames;
    private double _openStartedAt;
    private bool _revealLogged;
    private bool _warmingUp;
    private bool _openedDuringWarmUp;

    // Cube windows kept hidden between opens, one per monitor (SparePool has the rules). _layout counts display
    // changes, so windows made for an older layout of screens aren't kept or reused.
    private readonly SparePool<MonitorInfo, CubeOverlay> _kept = new(o => o.Monitor);
    private int _layout;

    /// <summary>
    /// Keep <paramref name="overlay"/> (already hidden) for the next open on its monitor, or close it if it's stale.
    /// Returns whether it was kept.
    /// </summary>
    private bool Keep(CubeOverlay overlay)
    {
        if (_keepingDisabled)
        {
            CloseQuietly([overlay], "keeping windows is off", log: false);
            return false;
        }

        var dispose = _kept.Add(overlay, overlay.Settings, overlay.DisplayLayout, _app.Settings, _layout);
        CloseQuietly(dispose, "replaced or made for other settings or screens");
        return !dispose.Contains(overlay);
    }

    /// <summary>
    /// The kept window for <paramref name="monitor"/> if it's usable here (same settings and screens, not closed, and
    /// on the current virtual desktop), else a new one. <paramref name="note"/> says why one wasn't reused, for the log.
    /// </summary>
    private CubeOverlay TakeOverlay(MonitorInfo monitor, out bool reused, out string? note)
    {
        reused = false;
        note = null;
        if (_keepingDisabled)
        {
            CloseQuietly(_kept.TakeAll(), "keeping windows is off"); // e.g. one a warm-up kept just before
            return new CubeOverlay(monitor, _app.Settings, Now, _layout);
        }

        if (_kept.TryTake(monitor, _app.Settings, _layout, out var kept))
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(kept).Handle;
            if (kept.IsClosed || hwnd == 0)
            {
                note = "kept window was closed";
                CloseQuietly([kept], note, log: false); // out of the pool now; closing again does nothing
            }
            else if (_app.Desktops is not { } desktops)
            {
                note = "no virtual desktops";
                CloseQuietly([kept], note);
            }
            else
            {
                try
                {
                    // A kept window stays on the desktop it was last shown on, and the cube may have switched since.
                    if (desktops.MoveToCurrentDesktop(hwnd))
                    {
                        reused = true;
                        return kept;
                    }

                    note = "kept window couldn't be put on this desktop";
                }
                catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException or UnauthorizedAccessException)
                {
                    note = $"moving the kept window failed ({ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message})";
                }
                catch
                {
                    CloseQuietly([kept], "unexpected error while moving it"); // it's out of the pool: don't leak it
                    throw;
                }

                CloseQuietly([kept], note);
            }
        }
        else if (_kept.Count > 0)
        {
            note = "kept windows are for other settings or screens";
        }

        return new CubeOverlay(monitor, _app.Settings, Now, _layout);
    }

    /// <summary>Close windows one by one, logging (not throwing) any that fail, so one bad window can't stop the rest.</summary>
    /// <param name="log">Log the closing itself (failures are always logged).</param>
    private static void CloseQuietly(IReadOnlyCollection<CubeOverlay> overlays, string why, bool log = true)
    {
        if (overlays.Count == 0)
        {
            return;
        }

        if (log)
        {
            EventLog.Write($"cube windows: closing {overlays.Count} ({why})");
        }
        foreach (var overlay in overlays)
        {
            try
            {
                overlay.Close();
            }
            catch (Exception ex)
            {
                EventLog.Write($"cube windows: closing the one on {overlay.Monitor.DeviceName} failed: {ex}");
            }
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>

        // SystemEvents calls this through the subscribing thread's SynchronizationContext, normally the UI thread;
        // BeginInvoke keeps it safe if that ever isn't so.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _layout++; // windows open now, or still being prepared, won't be kept either
            EventLog.Write("display settings changed");
            CloseQuietly(_kept.TakeAll(), "display settings changed");
            ScheduleWarmUp();
        });

    /// <summary>The settings changed: drop the kept windows made with the old ones and prepare new ones.</summary>
    public void SettingsChanged()
    {
        CloseQuietly(_kept.TakeAll(), "settings changed");
        ScheduleWarmUp();
    }

    /// <summary>
    /// Prepare kept windows again once the app is idle. If the cube is open or a warm-up is running, it's done when
    /// that ends instead (see <see cref="RunWantedWarmUp"/>).
    /// </summary>
    private void ScheduleWarmUp()
    {
        _warmUpWanted = true;
        RunWantedWarmUp();
    }

    /// <summary>Start the warm-up asked for earlier, at idle, if nothing is in the way now.</summary>
    private void RunWantedWarmUp() =>
        Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (_warmUpWanted && !IsOpen && !_opening && !_reopenPending && !_warmingUp && !_shutDown && !_keepingDisabled)
                {
                    await WarmUpAsync();
                }
            }
            catch (Exception ex)
            {
                EventLog.Write("warm-up failed: " + ex); // the task is discarded, so nothing else would see this
            }
        }, DispatcherPriority.ApplicationIdle);

    private bool _shutDown;
    private bool _warmUpWanted;

    /// <summary>Stop listening for display changes and close the kept windows. For app exit.</summary>
    public void Shutdown()
    {
        _shutDown = true;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        // WPF may already have closed them while shutting down; closing again does nothing.
        CloseQuietly(_kept.ShutDown(), "exiting");
    }

    public CubeSession(App app)
    {
        _app = app;

        // KWin's cube never closes because focus moved, and Windows can steal focus for a moment just
        // after the cube opens (its own Win+C handling, the Start menu, foreground rules). So: log it,
        // and if it happens while the cube is still opening, take focus back.
        Application.Current.Deactivated += (_, _) => OnAppDeactivated();

        // Kept windows are sized for the screens as they were: a change of screens or resolution makes them stale.
        // (A change of scaling alone may not raise this; a window then re-places itself through DpiChanged.)
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Application.Current.Activated += (_, _) => EventLog.Write("app activated");
    }

    public bool IsOpen => _overlays.Count > 0;

    private double Now() => _clock.Elapsed.TotalMilliseconds;

    /// <summary>
    /// Create each monitor's cube window ahead of time, drawn a few times while invisible and then hidden, so the
    /// next open can reuse them and skip most one-time setup (code loading, WPF's 3D pipeline, the graphics driver's
    /// caches). Never throws: it's only a head start, so a failure just leaves the next open slower, and is logged.
    /// Safe to overlap with a real open: these windows are only kept once ready, and only if still current.
    /// </summary>
    public async Task WarmUpAsync()
    {
        if (_warmingUp || _shutDown || _keepingDisabled)
        {
            return; // one at a time; the running one's end checks whether another is wanted
        }

        _warmingUp = true;
        _warmUpWanted = false;
        _openedDuringWarmUp = false;
        try
        {
            var clock = Stopwatch.StartNew();
            var tier = RenderCapability.Tier >> 16; // high word: 2 = most drawing on the GPU, 1 = some, 0 = software
            var monitors = Monitors.All();
            var screens = DesktopImageProvider.CaptureScreens(monitors);
            var captured = clock.ElapsedMilliseconds;
            var missing = screens.Values.Count(s => s is null);
            if (_keepingDisabled || _shutDown)
            {
                return; // turned off while the screens were captured: nothing would be kept
            }

            var settings = _app.Settings;
            var layout = _layout;
            var results = await Task.WhenAll(monitors.Select(async monitor =>
            {
                try
                {
                    Brush face = screens.GetValueOrDefault(monitor) is { } shot ? new ImageBrush(shot) { Stretch = Stretch.Fill } : new SolidColorBrush(Colors.Black);
                    face.Freeze();
                    var (kept, status) = await CubeOverlay.PrepareAsync(monitor, settings, Now, layout, face);
                    return kept is null || Keep(kept) ? status : status + ", not kept (settings or screens changed meanwhile)";
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
            RunWantedWarmUp(); // a change during this warm-up asked for another
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
            // Closing. Normally that ends within the animation; if it hasn't (something went wrong), or the
            // windows were deactivated without a close starting (which raises ClosingOnto at once, so shouldn't
            // happen), clean up so the cube can't get stuck "open" and ignore the hotkey for good.
            if (double.IsNaN(_closingSince) || Now() - _closingSince > Duration + 1500)
            {
                RecoverStuckClose(double.IsNaN(_closingSince) ? "the cube was deactivated without closing" : "the cube was stuck closing");
                return;
            }

            // A press during the closing animation opens the cube again once the close ends. Further presses
            // don't cancel it: the tray's "Open cube" calls this too, and that must never mean "don't open".
            if (!_reopenAfterClose)
            {
                _reopenAfterClose = true;
                EventLog.Write("reopening after the close");
                _ = ReopenIfCloseHangsAsync(_generation);
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

    /// <summary>
    /// Open again after a close, once the frame that ended it is over: not from inside that frame's Rendering
    /// callback, and not before DWM and the shell have caught up with the hidden windows and any desktop switch
    /// (or the screen capture would show the closing cube).
    /// </summary>
    /// <param name="previousForeground">The window to give focus back to: the old one when no desktop switch happened.</param>
    private void ReopenSoon(nint? previousForeground)
    {
        if (_shutDown)
        {
            EventLog.Write("not reopening after the close: The Cube is exiting");
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            EventLog.Write("not reopening after the close: the app is shutting down");
            return;
        }

        _reopenPending = true; // keeps the warm-up from starting in the gap; any open clears it
        var token = ++_reopenToken; // a newer ReopenSoon (with its own foreground window) supersedes this one
        _ = dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(ReopenDelayMs); // a few frames, for DWM to stop showing the hidden windows
                if (_shutDown || IsOpen || _opening || !_reopenPending || token != _reopenToken)
                {
                    EventLog.Write("not reopening after the close: " + (_shutDown ? "The Cube is exiting" : "another open was started or requested"));
                    return;
                }

                EventLog.Write("reopening after the close now");
                _ = OpenAsync(previousForeground);
            }
            catch (Exception ex)
            {
                EventLog.Write("reopening after the close failed: " + ex);
            }
            finally
            {
                if (token == _reopenToken)
                {
                    _reopenPending = false; // else a newer request is still waiting
                }

                RunWantedWarmUp(); // in case one was held back by the gap and the reopen didn't happen
            }
        }, DispatcherPriority.Background);
    }

    private const int ReopenDelayMs = 50;
    private bool _reopenPending; // ReopenSoon is waiting to open
    private int _reopenToken;

    private void RecoverStuckClose(string why)
    {
        EventLog.Write($"recovering: {why}");
        _reopenAfterClose = false; // a waiting press is answered by the reopen below
        CloseOverlays();
        ReopenSoon(null);
    }

    /// <summary>
    /// A press is waiting for the close to end. If the close never ends (a window never reports it's hidden),
    /// recover instead of leaving the press waiting for another one.
    /// </summary>
    private async Task ReopenIfCloseHangsAsync(int generation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Duration + 1500));
            if (generation == _generation && _reopenAfterClose && !_shutDown && IsOpen)
            {
                RecoverStuckClose("the close never ended, so the press waiting for it reopens the cube now");
            }
        }
        catch (Exception ex)
        {
            EventLog.Write("recovering a stuck close failed: " + ex);
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
        if (_shutDown)
        {
            return; // exiting
        }

        _opening = true;
        _reopenPending = false; // any open answers a waiting reopen
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
                RunWantedWarmUp(); // one asked for while this open was starting
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

        // Take (or create) and start every monitor's cube before showing any, so all of them are in _overlays
        // before focus can move and OnAppDeactivated runs. Each goes into _overlays at once, so a failure part-way
        // still closes it. Kept windows for monitors that are gone are dropped.
        var created = new List<CubeOverlay>();
        var reusedSet = new HashSet<CubeOverlay>();
        var notes = new List<string>();
        foreach (var monitor in monitors)
        {
            var overlay = TakeOverlay(monitor, out var reused, out var note);
            _overlays.Add(overlay);
            created.Add(overlay);
            if (reused)
            {
                reusedSet.Add(overlay);
            }

            if (note is not null)
            {
                notes.Add(note);
            }
        }

        CloseQuietly(_kept.TakeAll(), "left over: screens no longer connected, or other settings or screens");
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
            overlay.Start(current, count, face, reused: reusedSet.Contains(overlay));

            // The overlay passes on its controller's events and drops every handler when it's kept for later.
            overlay.ClosingOnto += desktop => FollowClose(overlay, desktop);
            overlay.SwitchRequested += desktop => _switchTarget = desktop;
            overlay.Hidden += OnOverlayHidden;
            overlay.AddDesktopRequested += AddDesktopAndReopen;
            overlay.Revealed += info => OnRevealed(generation, overlay, info);
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

        // Windows are shown (new or kept) and queued for drawing here, not yet visible: OnRevealed logs when they are.
        var queuedMs = phase.ElapsedMilliseconds;
        EventLog.Write($"open: desktops={count} current={current + 1} monitors={monitors.Count} reused={reusedSet.Count}{(notes.Count > 0 ? $" ({string.Join("; ", notes.Distinct())})" : "")}" +
                       $"{(screens.Values.Any(s => s is null) ? " (a screen wasn't captured)" : "")}, queued in {queuedMs} ms " +
                       $"(desktops {desktopsMs}, screens {captureMs - desktopsMs}, take+start {startMs - captureMs}, show {showMs - startMs}, focus {queuedMs - showMs}), " +
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

    private void StopCountingFrames(int monitorCount = 0)
    {
        if (_countingFrames)
        {
            CompositionTarget.Rendering -= CountFrame;
            _countingFrames = false;

            // UI-thread ticks for all monitors together, from open to close (animations and idle time alike).
            var where = monitorCount > 0 ? $"{monitorCount} monitors" : "open failed";
            EventLog.Write($"frames while open ({where}): {_frames.Summary()}");
        }
    }

    /// <summary>
    /// Log when the cube first became visible (uncloaked; on screen about one frame later), which is what the user
    /// waits for after the hotkey, and any monitor that was only shown by the fallback or isn't really visible.
    /// A kept window that Windows still hides (left on another virtual desktop) is moved here, or the cube is
    /// reopened with new windows and keeping windows is turned off until The Cube restarts.
    /// </summary>
    private void OnRevealed(int generation, CubeOverlay overlay, RevealInfo info)
    {
        if (generation != _generation)
        {
            return;
        }

        var problem = info.UncloakResult != 0 ? $"uncloaking failed 0x{info.UncloakResult:X8}"
            : info.StillCloaked == -1 ? "couldn't check whether Windows shows it"
            : info.StillCloaked != 0 ? $"Windows still hides it (cloaked 0x{info.StillCloaked:X})"
            : info.ByFallback ? "no frame drawn within 300 ms; shown anyway"
            : null;
        if (!_revealLogged || problem is not null)
        {
            _revealLogged = true;
            EventLog.Write($"open: uncloaked {Now() - _openStartedAt:0} ms after the open started ({(info.Reused ? "kept" : "new")} window on {overlay.Monitor.DeviceName}){(problem is null ? "" : $": {problem}")}");
        }

        // -1 means the check itself failed: nothing is known to be wrong, so leave the cube alone.
        if (info.UncloakResult != 0 || info.StillCloaked is not (0 or -1))
        {
            overlay.DoNotKeep = true; // even if the user closes it before the recovery below decides
            _ = RecoverHiddenAsync(generation, overlay, info);
        }
    }

    /// <summary>
    /// A window that's "revealed" but still hidden by Windows would leave an invisible cube holding the keyboard.
    /// A kept window is most likely on another virtual desktop: move it here and give Windows a moment. If it's
    /// still hidden (or it's a new window, which moving won't help), close the cube; a kept window's cube is
    /// reopened with new windows, and keeping windows is turned off until The Cube restarts.
    /// </summary>
    /// <remarks>Started fire-and-forget, so it must never throw: everything is caught and logged.</remarks>
    private async Task RecoverHiddenAsync(int generation, CubeOverlay overlay, RevealInfo info)
    {
        try
        {
            if (info.Reused && info.UncloakResult == 0)
            {
                // Turned off at once, so a close (or another monitor) during the wait can't keep a bad window.
                _keepingDisabled = true;
                var moved = false;
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
                    moved = _app.Desktops?.MoveToCurrentDesktop(hwnd) == true;
                }
                catch (Exception ex)
                {
                    EventLog.Write($"open: moving the shown window failed ({ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message})");
                }

                await Task.Delay(150); // the shell may lift its cloak only after it has handled the move
                if (generation != _generation || _shutDown)
                {
                    return; // closed (and not kept: keeping is off) or exiting meanwhile
                }

                var cloaked = overlay.CloakedFlags();
                if (cloaked is 0 or -1)
                {
                    EventLog.Write($"open: the kept window is visible after the move (moved={moved}{(cloaked == -1 ? ", couldn't check" : "")}); keeping windows stays off until The Cube restarts");
                    overlay.MarkVisible();
                    return;
                }

                EventLog.Write($"open: the kept window is still hidden after moving it (moved={moved}, cloaked 0x{cloaked:X})");
            }

            if (generation != _generation || _shutDown)
            {
                return;
            }

            if (!double.IsNaN(_closingSince))
            {
                return; // the user is already closing it; that close switches desktop and won't keep the window
            }

            var previous = _previousForeground;
            CloseOverlays();
            if (info.Reused)
            {
                EventLog.Write("open: reopening with new windows; keeping windows is off until The Cube restarts");
                _ = OpenAsync(previous);
            }
            else
            {
                _app.ReportFailure("The cube couldn't be shown on this screen, so it was closed.");
            }
        }
        catch (Exception ex)
        {
            EventLog.Write("open: recovering a hidden cube window failed: " + ex);
            try
            {
                CloseOverlays(); // never leave an invisible cube holding the keyboard
            }
            catch (Exception closeEx)
            {
                EventLog.Write("open: closing after that failed too: " + closeEx);
            }
        }
    }

    private bool _keepingDisabled;

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
        if (_overlays.Count == 0)
        {
            return; // a stray report after the cube already closed
        }

        if (++_hiddenCount < _overlays.Count)
        {
            return;
        }

        var target = _switchTarget;
        _switchTarget = null;
        var previous = _previousForeground;
        var generation = _generation;
        var switched = false;
        var reopen = false;
        try
        {
            if (target is int desktop && _app.Desktops is { } desktops)
            {
                // While the overlays still show the target face, so there's no flash of the old desktop.
                desktops.SwitchTo(desktop, animate: false);
                switched = true;
                EventLog.Write($"switched to desktop {desktop + 1}");
            }
            else if (previous != 0)
            {
                SetForegroundWindow(previous);
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
            if (generation != _generation)
            {
                // SwitchTo is a COM call that can pump messages: a stuck-close recovery ran during it, closed
                // these windows and scheduled a reopen (which may already have started); this must not close that.
                EventLog.Write("the cube was closed and reopened during the desktop switch");
            }
            else
            {
                // Taken after the switch (a press can arrive during that COM call) and before CloseOverlays, which
                // would otherwise report it dropped, so a failure there can't leave it to reopen after a later close.
                reopen = _reopenAfterClose;
                _reopenAfterClose = false;
                try
                {
                    CloseOverlays(keepWindows: true); // always, so the cube can't stay stuck open
                }
                finally
                {
                    // Even after a failed switch or cleanup: the user asked for it to open.
                    if (reopen)
                    {
                        // After a switch the old window is on another desktop; the open reads the new foreground.
                        ReopenSoon(switched ? null : previous);
                    }
                }
            }
        }
    }

    /// <param name="keepWindows">
    /// Hide the windows for the next open instead of closing them: when the cube closed through its own animation
    /// (even if the desktop switch then failed) or reopens after adding a desktop. Not when recovering from a stuck
    /// or failed open, where a window may be in a bad state.
    /// </param>
    private void CloseOverlays(bool keepWindows = false)
    {
        StopCountingFrames(_overlays.Count);

        // Clear the session's state first, so a failure below can't leave the cube "open" with windows half gone.
        var closing = _overlays.ToList();
        _overlays.Clear();
        _focused = null; // so hiding a focused window can't make OnAppDeactivated focus it again
        _closingSince = double.NaN;
        if (_reopenAfterClose)
        {
            // OnOverlayHidden and RecoverStuckClose take the press first. The other callers run while opening
            // (Toggle can't set this then), reopen themselves (AddDesktopAndReopen), or return early while
            // closing (RecoverHiddenAsync, though its error path doesn't); so this is logged, not expected.
            EventLog.Write("a press waiting for the close was dropped: the cube closed another way");
            _reopenAfterClose = false;
        }

        _generation++;

        var allKept = keepWindows && closing.Count > 0;
        foreach (var overlay in closing)
        {
            try
            {
                overlay.Stop();
                if (keepWindows && !_keepingDisabled && !overlay.IsClosed && !overlay.DoNotKeep)
                {
                    overlay.Recycle();
                    if (Keep(overlay))
                    {
                        continue;
                    }

                    allKept = false;
                    continue; // Keep closed it (stale)
                }
            }
            catch (Exception ex)
            {
                EventLog.Write($"close: couldn't keep the window on {overlay.Monitor.DeviceName} for the next open ({ex.GetType().Name}: {ex.Message})");
            }

            allKept = false;
            CloseQuietly([overlay], "cube closed", log: false);
        }

        if (allKept)
        {
            _warmUpWanted = false; // this open's windows were made for the current settings and screens, and are kept
        }

        RunWantedWarmUp(); // a settings or display change while the cube was open asked for new kept windows
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
        CloseOverlays(keepWindows: true);
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
