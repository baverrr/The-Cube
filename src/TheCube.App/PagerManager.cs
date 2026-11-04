using System.Runtime.InteropServices;
using System.Windows.Threading;
using TheCube.App.Interop;
using TheCube.Core;

namespace TheCube.App;

/// <summary>
/// Keeps a pager on every taskbar, just left of the Start button. The taskbar layout (which moves as apps
/// open and close on a centred taskbar) is read on a background thread every second; the desktops and
/// windows are read on the UI thread twice a second, and right away whenever another window comes to the
/// front (which also puts the pager back on top of the taskbar). An auto-hide taskbar slides: Windows
/// reports each move of a taskbar window, and the pager is moved with it, so it slides away and back with
/// the taskbar instead of lagging behind. Nothing polls while the taskbar stays put.
/// </summary>
internal sealed class PagerManager : IDisposable
{
    private readonly App _app;
    private readonly DispatcherTimer _timer;
    private readonly WinEventProc _onLocationChange; // kept alive while the hook exists
    private readonly SemaphoreSlim _wakeLayout = new(0, 1);
    private nint _locationHook;
    private uint _hookedProcess;
    private bool _followPending;
    private int _desktopCount;
    private readonly Dictionary<nint, PagerWindow> _pagers = [];

    // Per taskbar: where the pager was placed and where the taskbar was then (fully shown), and the
    // rectangles last used, so the per-frame follower only moves windows when something changed.
    private readonly Dictionary<nint, (PixelRect Pager, PixelRect Taskbar, int Count)> _anchors = [];
    private readonly Dictionary<nint, (PixelRect Taskbar, PixelRect? Pager)> _followed = [];
    private readonly HashSet<nint> _suppressed = []; // full-screen app in front, or no room
    private readonly WinEventProc _onForeground; // kept alive while the hook exists
    private CancellationTokenSource? _layoutLoop;
    private List<TaskbarInfo> _layout = [];
    private nint _hook;
    private bool _enabled;
    private string _lastProblem = "";

    public PagerManager(App app)
    {
        _app = app;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => SafeRefresh();
        _onForeground = (_, _, _, _, _, _, _) => _app.Dispatcher.BeginInvoke(SafeRefresh, DispatcherPriority.Background);
        _onLocationChange = (_, _, hwnd, idObject, idChild, _, _) =>
        {
            // Only the taskbar windows themselves (not their buttons, the caret or the cursor).
            if (idObject != OBJID_WINDOW || idChild != 0 || !_pagers.ContainsKey(hwnd) || _followPending)
            {
                return;
            }

            _followPending = true;
            _app.Dispatcher.BeginInvoke(() =>
            {
                _followPending = false;
                Follow();
            }, DispatcherPriority.Render);
        };
    }

    /// <summary>Turn the pager on or off (the "Show the desktop pager" setting).</summary>
    public void Configure(bool enabled)
    {
        enabled &= _app.Desktops is WindowsVirtualDesktops;
        if (enabled == _enabled)
        {
            return;
        }

        _enabled = enabled;
        if (enabled)
        {
            _layoutLoop = new CancellationTokenSource();
            _ = ReadLayoutLoop(_layoutLoop.Token);
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, _onForeground, 0, 0, WINEVENT_OUTOFCONTEXT);
            if (_hook == 0)
            {
                Problem("pager: SetWinEventHook failed; the pager will come back on top within half a second instead");
            }
            _timer.Start();
        }
        else
        {
            Stop();
        }

        EventLog.Write($"pager: {(enabled ? "on" : "off")}");
    }

    private void Stop()
    {
        _timer.Stop();
        UnhookLocation();
        _anchors.Clear();
        _followed.Clear();
        _suppressed.Clear();
        _layoutLoop?.Cancel();
        _layoutLoop = null;
        if (_hook != 0)
        {
            UnhookWinEvent(_hook);
            _hook = 0;
        }

        foreach (var pager in _pagers.Values)
        {
            pager.Close();
        }

        _pagers.Clear();
        _layout = [];
    }

    private async Task ReadLayoutLoop(CancellationToken cancel)
    {
        // Each loop has its own lookup cache: a cancelled loop's last Find may still be running.
        var taskbars = new Taskbars();
        var first = true;
        while (!cancel.IsCancellationRequested)
        {
            List<TaskbarInfo> layout;
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                layout = await Task.Run(() => taskbars.Find(Monitors.All()), cancel);
                if (first)
                {
                    first = false;
                    EventLog.Write($"pager: first taskbar read took {clock.ElapsedMilliseconds} ms ({layout.Count} taskbar(s))");
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Problem("pager: reading the taskbars failed: " + Describe(ex));
                layout = [];
            }

            if (cancel.IsCancellationRequested)
            {
                return;
            }

            _layout = layout;
            SafeRefresh();
            try
            {
                // Every second, or straight away when a taskbar has just moved (it may have slid back in
                // with Start somewhere else).
                await _wakeLayout.WaitAsync(1000, cancel);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Refresh, logging (once) instead of throwing: this runs from timers and hooks every half second.</summary>
    private void SafeRefresh()
    {
        try
        {
            Refresh();
        }
        catch (Exception ex)
        {
            Problem("pager: refresh failed: " + ex);
        }
    }

    private void Refresh()
    {
        if (!_enabled || _app.Desktops is not WindowsVirtualDesktops desktops)
        {
            return;
        }

        // The cube covers everything while it's open, as Plasma's does its panels.
        if (_app.Cube?.IsOpen == true)
        {
            foreach (var pager in _pagers.Values)
            {
                pager.HideNow();
            }

            return;
        }

        IReadOnlyList<DesktopInfo> list;
        int current;
        List<WindowEntry> windows;
        try
        {
            list = desktops.GetDesktops();
            current = desktops.GetCurrentIndex();
            windows = DesktopImageProvider.ListWindows(desktops, list, includeTaskbars: false);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Problem("pager: reading desktops failed: " + Describe(ex));
            return;
        }

        _lastProblem = ""; // log the next failure even if it's the same as the last one

        foreach (var gone in _pagers.Keys.Except(_layout.Select(t => t.Handle)).ToList())
        {
            _pagers[gone].Close();
            _pagers.Remove(gone);
            _anchors.Remove(gone);
            _followed.Remove(gone);
            _suppressed.Remove(gone);
        }

        _desktopCount = list.Count;
        HookLocationChanges();
        foreach (var taskbar in _layout)
        {
            var monitor = taskbar.Monitor;
            if (EdgeWatcher.FullScreenAppInFront(monitor))
            {
                _suppressed.Add(taskbar.Handle);
            }
            else
            {
                _suppressed.Remove(taskbar.Handle);
            }

            // Re-anchor only while the taskbar is fully shown (that's when the Start button can be read);
            // while it's sliding or hidden, the follower moves the pager from the last anchor.
            if (taskbar.Shown)
            {
                var aspect = monitor.Bounds.Height > 0 ? (double)monitor.Bounds.Width / monitor.Bounds.Height : 16.0 / 9;
                var placedAt = Pager.Place(taskbar.Bounds, taskbar.Start, taskbar.Tray, list.Count, aspect, taskbar.Obstacles);
                LogPlacement(taskbar, placedAt);
                if (placedAt is { } placed)
                {
                    _anchors[taskbar.Handle] = (placed, taskbar.Bounds, list.Count);
                }
                else
                {
                    _anchors.Remove(taskbar.Handle);
                }
            }

            if (!_anchors.ContainsKey(taskbar.Handle))
            {
                if (_pagers.TryGetValue(taskbar.Handle, out var unplaced))
                {
                    unplaced.HideNow();
                }

                continue;
            }

            if (!_pagers.TryGetValue(taskbar.Handle, out var pager))
            {
                pager = new PagerWindow(monitor, SwitchTo);
                _pagers[taskbar.Handle] = pager;
            }

            pager.Monitor = monitor;
            var perDesktop = Enumerable.Range(0, list.Count)
                .Select(d => (IReadOnlyList<PixelRect>)SceneBuilder.WindowsFor(windows, d, monitor.Bounds).Select(w => w.Frame).ToList())
                .ToList();
            pager.Update(new PagerState(current, perDesktop));
            _followed.Remove(taskbar.Handle); // place it (and put it back on top) on the next frame
        }

        Follow();
    }

    /// <summary>Whenever a taskbar moves (and on each refresh): keep each pager on its taskbar, sliding with an auto-hide taskbar.</summary>
    private void Follow()
    {
        if (!_enabled || _app.Cube?.IsOpen == true)
        {
            return;
        }

        foreach (var (handle, pager) in _pagers)
        {
            if (!_anchors.TryGetValue(handle, out var anchor) || anchor.Count != _desktopCount || _suppressed.Contains(handle)
                || !Capture.GetWindowRect(handle, out var rect))
            {
                pager.HideNow();
                _followed.Remove(handle);
                continue;
            }

            var taskbar = rect.ToPixelRect();
            var target = NativeMethods.IsWindowVisible(handle) ? Pager.Follow(anchor.Pager, anchor.Taskbar, taskbar, pager.Monitor.Bounds) : null;
            if (_followed.TryGetValue(handle, out var last) && last.Taskbar == taskbar && last.Pager == target)
            {
                continue; // nothing moved
            }

            if (last.Taskbar != taskbar && _followed.ContainsKey(handle))
            {
                WakeLayout(); // the taskbar moved: read Start's position again soon
            }

            _followed[handle] = (taskbar, target);
            if (target is not { } r)
            {
                pager.HideNow();
                continue;
            }

            if (!pager.IsVisible)
            {
                pager.Show();
            }

            pager.Place(r); // also puts it back above the taskbar, which comes to the front as it slides in
        }
    }

    private void SwitchTo(int index)
    {
        if (_app.Desktops is not { } desktops || _app.Cube?.IsOpen == true)
        {
            return;
        }

        try
        {
            if (DesktopActions.SwitchTo(desktops, index, animate: true))
            {
                EventLog.Write($"pager: switched to desktop {index + 1}");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            _app.ReportFailure("Couldn't switch desktop: " + ex.Message);
        }

        SafeRefresh();
    }

    private void Problem(string message)
    {
        if (message != _lastProblem)
        {
            _lastProblem = message;
            EventLog.Write(message);
        }
    }

    /// <summary>Listen for the taskbars moving; re-done if Explorer restarts (a new process).</summary>
    private void HookLocationChanges()
    {
        var taskbar = _layout.FirstOrDefault();
        if (taskbar is null)
        {
            return;
        }

        GetWindowThreadProcessId(taskbar.Handle, out var process);
        if (process == 0 || process == _hookedProcess)
        {
            return;
        }

        UnhookLocation();
        _locationHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, 0, _onLocationChange, process, 0, WINEVENT_OUTOFCONTEXT);
        if (_locationHook == 0)
        {
            Problem("pager: couldn't watch the taskbar move; it will follow an auto-hide taskbar every half second instead");
            return;
        }

        _hookedProcess = process;
    }

    private void UnhookLocation()
    {
        if (_locationHook != 0)
        {
            UnhookWinEvent(_locationHook);
            _locationHook = 0;
        }

        _hookedProcess = 0;
    }

    private void WakeLayout()
    {
        if (_wakeLayout.CurrentCount == 0)
        {
            try
            {
                _wakeLayout.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already woken.
            }
        }
    }

    private readonly Dictionary<nint, string> _placementLog = [];

    /// <summary>Log where the pager went and what it was fitting around (positions only), when that changes.</summary>
    private void LogPlacement(TaskbarInfo taskbar, PixelRect? placed)
    {
        static string R(PixelRect? r) => r is { } v ? $"{v.X}..{v.Right}" : "-";
        var where = placed is not { } p ? "nowhere (no room)"
            : taskbar.Start is { } s && p.Right <= s.X ? "left of Start"
            : taskbar.Tray is { } t && p.Right <= t.X ? "left of the tray"
            : "elsewhere";
        var line = $"pager: {where} at {R(placed)} h={placed?.Height}; taskbar {R(taskbar.Bounds)} h={taskbar.Bounds.Height}, " +
                   $"Start {R(taskbar.Start)}, tray {R(taskbar.Tray)}, keep clear of [{string.Join(", ", taskbar.Obstacles.Select(o => R(o)))}]";
        foreach (var gone in _placementLog.Keys.Except(_layout.Select(t => t.Handle)).ToList())
        {
            _placementLog.Remove(gone);
        }

        if (!_placementLog.TryGetValue(taskbar.Handle, out var last) || last != line)
        {
            _placementLog[taskbar.Handle] = line;
            EventLog.Write(line);
        }
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}";

    public void Dispose() => Stop();

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const int OBJID_WINDOW = 0;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc proc, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}
