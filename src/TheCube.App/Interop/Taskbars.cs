using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using TheCube.Core;

namespace TheCube.App.Interop;

/// <summary>A taskbar and the parts the pager lines up with, in physical pixels.</summary>
internal sealed record TaskbarInfo(nint Handle, PixelRect Bounds, PixelRect? Start, PixelRect? Tray, MonitorInfo Monitor, bool Shown);

/// <summary>
/// Finds the taskbars (one per monitor) and where their Start buttons are. On Windows 11 the Start button
/// isn't a window of its own, so it's found with UI Automation, which is slow-ish: call <see cref="Find"/> from
/// a background thread.
/// </summary>
internal sealed class Taskbars
{
    // Found Start buttons are cached per taskbar (reading a cached element's position is cheap). A failed
    // lookup searches the whole taskbar tree, so it's retried only every 30 seconds.
    private static readonly TimeSpan RetryMissAfter = TimeSpan.FromSeconds(30);
    private readonly Dictionary<nint, AutomationElement?> _startButtons = [];
    private readonly Dictionary<nint, DateTime> _misses = [];
    private readonly Dictionary<nint, string> _anchors = [];

    public List<TaskbarInfo> Find(IReadOnlyList<MonitorInfo> monitors)
    {
        var handles = new List<nint>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (ClassName(hwnd) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                handles.Add(hwnd);
            }

            return true;
        }, 0);

        foreach (var gone in _startButtons.Keys.Concat(_misses.Keys).Concat(_anchors.Keys).Except(handles).ToList())
        {
            _startButtons.Remove(gone);
            _misses.Remove(gone);
            _anchors.Remove(gone);
        }

        var result = new List<TaskbarInfo>();
        foreach (var hwnd in handles)
        {
            if (!Capture.GetWindowRect(hwnd, out var rect))
            {
                continue;
            }

            var bounds = rect.ToPixelRect();
            var monitor = monitors
                .Select(m => (Monitor: m, Overlap: Area(m.Bounds.Intersect(bounds))))
                .OrderByDescending(x => x.Overlap)
                .FirstOrDefault();
            if (monitor.Monitor is null || monitor.Overlap == 0)
            {
                continue;
            }

            // Auto-hidden taskbars slide almost entirely off the screen.
            var onScreen = bounds.Intersect(monitor.Monitor.Bounds);
            var shown = NativeMethods.IsWindowVisible(hwnd) && onScreen.Height >= bounds.Height - 2 && onScreen.Width >= bounds.Width - 2;

            var start = shown ? StartButton(hwnd) : null;
            var tray = Child(hwnd, "TrayNotifyWnd");
            if (shown)
            {
                LogAnchor(hwnd, start is not null ? "Start button" : tray is not null ? "no Start button found; next to the tray" : "no Start button or tray found; right end");
            }

            result.Add(new TaskbarInfo(hwnd, bounds, start, tray, monitor.Monitor, shown));
        }

        return result;
    }

    private PixelRect? StartButton(nint taskbar)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!_startButtons.TryGetValue(taskbar, out var element) || element is null)
            {
                if (_misses.TryGetValue(taskbar, out var missedAt) && DateTime.UtcNow - missedAt < RetryMissAfter)
                {
                    break;
                }

                element = FindStartElement(taskbar);
                _startButtons[taskbar] = element;
                if (element is null)
                {
                    _misses[taskbar] = DateTime.UtcNow;
                }
                else
                {
                    _misses.Remove(taskbar);
                }
            }

            if (element is null)
            {
                break;
            }

            try
            {
                var r = element.Current.BoundingRectangle;
                if (!r.IsEmpty && r.Width > 0)
                {
                    return new PixelRect((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height));
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                // Explorer restarted or the taskbar was rebuilt; look it up again.
            }

            _startButtons[taskbar] = null;
        }

        // Windows 10: the Start button is a child window.
        return Child(taskbar, "Start");
    }

    private static AutomationElement? FindStartElement(nint taskbar)
    {
        try
        {
            return AutomationElement.FromHandle(taskbar)
                .FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"));
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException)
        {
            EventLog.Write($"pager: UI Automation lookup of the Start button failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
            return null;
        }
    }

    /// <summary>Log where each taskbar's pager is anchored, when that changes.</summary>
    private void LogAnchor(nint taskbar, string anchor)
    {
        if (!_anchors.TryGetValue(taskbar, out var last) || last != anchor)
        {
            _anchors[taskbar] = anchor;
            EventLog.Write($"pager: taskbar {ClassName(taskbar)}: {anchor}");
        }
    }

    private static PixelRect? Child(nint parent, string className)
    {
        var child = FindWindowEx(parent, 0, className, null);
        return child != 0 && NativeMethods.IsWindowVisible(child) && Capture.GetWindowRect(child, out var rect) && !rect.ToPixelRect().IsEmpty
            ? rect.ToPixelRect()
            : null;
    }

    private static long Area(PixelRect r) => r.IsEmpty ? 0 : (long)r.Width * r.Height;

    private static string ClassName(nint hwnd)
    {
        var sb = new StringBuilder(64);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint childAfter, string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder name, int max);
}
