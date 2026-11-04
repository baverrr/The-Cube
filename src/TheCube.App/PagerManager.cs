using System.Runtime.InteropServices;
using System.Windows.Threading;
using TheCube.App.Interop;
using TheCube.Core;

namespace TheCube.App;

/// <summary>
/// Keeps a pager on every taskbar, just left of the Start button. The taskbar layout (which moves as apps
/// open and close on a centred taskbar) is read on a background thread every second; the desktops and
/// windows are read on the UI thread twice a second, and right away whenever another window comes to the
/// front (which also puts the pager back on top of the taskbar).
/// </summary>
internal sealed class PagerManager : IDisposable
{
    private readonly App _app;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<nint, PagerWindow> _pagers = [];
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
        while (!cancel.IsCancellationRequested)
        {
            List<TaskbarInfo> layout;
            try
            {
                layout = await Task.Run(() => taskbars.Find(Monitors.All()), cancel);
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
                await Task.Delay(1000, cancel);
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
        }

        foreach (var taskbar in _layout)
        {
            var monitor = taskbar.Monitor;
            var aspect = monitor.Bounds.Height > 0 ? (double)monitor.Bounds.Width / monitor.Bounds.Height : 16.0 / 9;
            var rect = taskbar.Shown && !EdgeWatcher.FullScreenAppInFront(monitor)
                ? Pager.Place(taskbar.Bounds, taskbar.Start, taskbar.Tray, list.Count, aspect)
                : null;

            if (!_pagers.TryGetValue(taskbar.Handle, out var pager))
            {
                if (rect is null)
                {
                    continue;
                }

                pager = new PagerWindow(monitor, SwitchTo);
                _pagers[taskbar.Handle] = pager;
            }

            if (rect is not { } r)
            {
                pager.HideNow();
                continue;
            }

            pager.Monitor = monitor;
            var perDesktop = Enumerable.Range(0, list.Count)
                .Select(d => (IReadOnlyList<PixelRect>)SceneBuilder.WindowsFor(windows, d, monitor.Bounds).Select(w => w.Frame).ToList())
                .ToList();
            pager.Update(new PagerState(current, perDesktop));
            if (!pager.IsVisible)
            {
                pager.Show();
            }

            pager.Place(r);
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

    private static string Describe(Exception ex) => $"{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}";

    public void Dispose() => Stop();

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc proc, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);
}
