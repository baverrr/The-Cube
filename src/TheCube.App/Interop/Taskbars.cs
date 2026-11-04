using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using TheCube.Core;

namespace TheCube.App.Interop;

/// <summary>A taskbar and the parts the pager lines up with, in physical pixels.</summary>
/// <param name="Obstacles">Things on the taskbar the pager mustn't cover, such as the Widgets (weather) button.</param>
internal sealed record TaskbarInfo(nint Handle, PixelRect Bounds, PixelRect? Start, PixelRect? Tray, IReadOnlyList<PixelRect> Obstacles, MonitorInfo Monitor, bool Shown);

/// <summary>
/// Finds the taskbars (one per monitor) and where their Start buttons are. On Windows 11 the Start button
/// isn't a window of its own, so it's found with UI Automation, which is slow-ish: call <see cref="Find"/> from
/// a background thread.
/// </summary>
internal sealed class Taskbars
{
    private const string StartButtonId = "StartButton";
    private const string WidgetsButtonId = "WidgetsButton"; // Windows 11's weather/Widgets button

    // Found elements are cached per taskbar (reading a cached element's position is cheap). A failed
    // lookup searches the whole taskbar tree, so it's retried only every 30 seconds.
    private static readonly TimeSpan RetryMissAfter = TimeSpan.FromSeconds(30);
    private readonly Dictionary<(nint Taskbar, string Id), AutomationElement?> _elements = [];
    private readonly Dictionary<(nint Taskbar, string Id), DateTime> _misses = [];
    private readonly Dictionary<nint, string> _anchors = [];
    private readonly Dictionary<nint, string> _searchLog = [];

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

        foreach (var key in _elements.Keys.Concat(_misses.Keys).Where(k => !handles.Contains(k.Taskbar)).Distinct().ToList())
        {
            _elements.Remove(key);
            _misses.Remove(key);
        }

        foreach (var gone in _anchors.Keys.Concat(_searchLog.Keys).Except(handles).Distinct().ToList())
        {
            _anchors.Remove(gone);
            _searchLog.Remove(gone);
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

            // Windows 10's Start button is a child window instead.
            if (shown)
            {
                Prefetch(hwnd, StartButtonId, WidgetsButtonId); // one walk of the taskbar for both
            }

            var xamlStart = shown ? Element(hwnd, StartButtonId) : null;
            var start = xamlStart ?? (shown ? Child(hwnd, "Start") : null);
            var tray = Child(hwnd, "TrayNotifyWnd");
            var obstacles = new List<PixelRect>();
            if (shown && Element(hwnd, WidgetsButtonId) is { } widgets)
            {
                obstacles.Add(widgets);
            }

            // Only Windows 11's own (XAML) taskbar can be centred; a classic Start window from a taskbar
            // replacement is at the far left whatever TaskbarAl says. Ignore an implausible group too.
            if (xamlStart is { } s && IsCentered() && Pager.CenteredAppGroup(bounds, s) is { } apps
                && s.X - bounds.X > bounds.Height && (tray is not { } tr || apps.Right <= tr.X))
            {
                obstacles.Add(apps); // never on top of the pinned and running apps
            }

            if (shown)
            {
                LogAnchor(hwnd, (start is not null ? "Start button" : tray is not null ? "no Start button found; next to the tray" : "no Start button or tray found; right end") + (obstacles.Count > 0 ? $", keeping clear of {obstacles.Count} area(s)" : ""));
            }

            result.Add(new TaskbarInfo(hwnd, bounds, start, tray, obstacles, monitor.Monitor, shown));
        }

        return result;
    }

    /// <summary>Where a taskbar element with this UI Automation ID is, if there is one (and it's shown).</summary>
    private PixelRect? Element(nint taskbar, string automationId)
    {
        var key = (taskbar, automationId);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!_elements.TryGetValue(key, out var element) || element is null)
            {
                if (_misses.TryGetValue(key, out var missedAt) && DateTime.UtcNow - missedAt < RetryMissAfter)
                {
                    break;
                }

                element = FindElement(taskbar, automationId);
                _elements[key] = element;
                if (element is null)
                {
                    _misses[key] = DateTime.UtcNow;
                }
                else
                {
                    _misses.Remove(key);
                }
            }

            if (element is null)
            {
                break;
            }

            try
            {
                var current = element.Current;
                var r = current.BoundingRectangle;
                if (current.IsOffscreen)
                {
                    return null; // e.g. Widgets turned off but still in the tree
                }

                if (!r.IsEmpty && r.Width > 0)
                {
                    return new PixelRect((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height));
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                // Explorer restarted or the taskbar was rebuilt; look it up again.
            }

            _elements[key] = null;
        }

        return null;
    }

    /// <summary>
    /// Look up every ID that isn't cached (and didn't just fail) in a single search of the taskbar's
    /// UI Automation tree; searching is the slow part, so one walk instead of one per ID.
    /// </summary>
    private void Prefetch(nint taskbar, params string[] automationIds)
    {
        var needed = automationIds.Where(id =>
            !(_elements.TryGetValue((taskbar, id), out var e) && e is not null)
            && !(_misses.TryGetValue((taskbar, id), out var missedAt) && DateTime.UtcNow - missedAt < RetryMissAfter)).ToList();
        if (needed.Count == 0)
        {
            return;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var found = new Dictionary<string, AutomationElement>();
        try
        {
            Condition condition = needed.Count == 1
                ? new PropertyCondition(AutomationElement.AutomationIdProperty, needed[0])
                : new OrCondition(needed.Select(id => (Condition)new PropertyCondition(AutomationElement.AutomationIdProperty, id)).ToArray());

            // Fetch each match's ID with the search itself, so reading it can't fail if an element has
            // gone by the time we look (the taskbar is often still being built just after sign-in).
            var request = new CacheRequest();
            request.Add(AutomationElement.AutomationIdProperty);
            using (request.Activate())
            {
                foreach (AutomationElement element in AutomationElement.FromHandle(taskbar).FindAll(TreeScope.Descendants, condition))
                {
                    found.TryAdd(element.Cached.AutomationId, element);
                }
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException)
        {
            // Don't record misses: Element() falls back to its own lookup.
            EventLog.Write($"pager: UI Automation search of the taskbar failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
            return;
        }

        foreach (var id in needed)
        {
            if (found.TryGetValue(id, out var element))
            {
                _elements[(taskbar, id)] = element;
                _misses.Remove((taskbar, id));
            }
            else
            {
                _elements[(taskbar, id)] = null;
                _misses[(taskbar, id)] = DateTime.UtcNow;
            }
        }

        // Log the first search of each taskbar, slow ones, and changes in what was found; not every retry.
        var summary = $"for {string.Join(", ", needed)}, found {string.Join(", ", found.Keys)}";
        if (!_searchLog.TryGetValue(taskbar, out var last) || last != summary || clock.ElapsedMilliseconds > 200)
        {
            _searchLog[taskbar] = summary;
            EventLog.Write($"pager: searched the taskbar {summary} in {clock.ElapsedMilliseconds} ms");
        }
    }

    private static AutomationElement? FindElement(nint taskbar, string automationId)
    {
        try
        {
            return AutomationElement.FromHandle(taskbar)
                .FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException)
        {
            EventLog.Write($"pager: UI Automation lookup of {automationId} failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
            return null;
        }
    }

    /// <summary>Windows 11 centres the taskbar unless "Taskbar alignment" is set to Left.</summary>
    private static bool IsCentered()
    {
        if (Environment.OSVersion.Version.Build < 22000)
        {
            return false; // Windows 10 can't centre it
        }

        var value = Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAl", 1);
        return value is not int alignment || alignment != 0;
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
