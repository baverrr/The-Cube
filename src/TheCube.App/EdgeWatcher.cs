using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using TheCube.App.Interop;
using TheCube.Core;

namespace TheCube.App;

/// <summary>
/// Opens the cube when the pointer rests on one of the chosen screen edges or corners (KWin's
/// "screen edges"). Polls the cursor only while at least one edge is enabled, and ignores edges while
/// a full-screen app (a game or video) is in front, as KWin does.
/// </summary>
internal sealed class EdgeWatcher : IDisposable
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly EdgeDwellTracker _tracker = new();
    private readonly Action _activate;
    private HashSet<ScreenEdge> _edges = [];
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private double _monitorsReadAt = double.NegativeInfinity;

    public EdgeWatcher(Action activate)
    {
        _activate = activate;
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>Set which edges are active; with none, polling stops.</summary>
    public void Configure(IEnumerable<ScreenEdge> edges)
    {
        _edges = edges.ToHashSet();
        if (_edges.Count > 0)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void Poll()
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        if (now - _monitorsReadAt > 2000)
        {
            _monitors = Monitors.All(); // monitors rarely change; re-read every couple of seconds
            _monitorsReadAt = now;
        }

        if (!GetCursorPos(out var p))
        {
            return;
        }

        ScreenEdge? edge = null;
        var monitor = _monitors.FirstOrDefault(m => Contains(m.Bounds, p.X, p.Y));
        if (monitor is not null)
        {
            edge = ScreenEdges.Classify(p.X, p.Y, monitor.Bounds, (x, y) => _monitors.Any(m => Contains(m.Bounds, x, y)));
            if (edge is { } e && !_edges.Contains(e))
            {
                edge = null;
            }
        }

        if (_tracker.Update(edge, now) is not null && monitor is not null && !FullScreenAppInFront(monitor))
        {
            _activate();
        }
    }

    /// <summary>A full-screen app (game, video) covers this monitor; the taskbar is hidden then too.</summary>
    internal static bool FullScreenAppInFront(MonitorInfo monitor)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0)
        {
            return false;
        }

        var sb = new System.Text.StringBuilder(32);
        GetClassName(hwnd, sb, sb.Capacity);
        if (sb.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            return false; // the desktop itself
        }

        return Capture.GetWindowRect(hwnd, out var rect) && rect.ToPixelRect() == monitor.Bounds;
    }

    private static bool Contains(PixelRect r, int x, int y) => x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom;

    public void Dispose() => _timer.Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, System.Text.StringBuilder name, int max);
}
