namespace TheCube.Core;

/// <summary>A rectangle in any units (used for the pager's drawing, in device-independent pixels).</summary>
public readonly record struct BoxRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>
/// The desktop pager on the taskbar, modelled on Plasma's Pager widget: one small box per desktop, shaped
/// like the screen, the current one highlighted, with an outline of each window; click a box to go to that
/// desktop, scroll over it to step through them.
/// </summary>
public static class Pager
{
    /// <summary>Taskbar height left free above and below the boxes, as a fraction of the height.</summary>
    public const double VerticalPadding = 0.16;

    /// <summary>Gap between the pager and the Start button, as a fraction of the taskbar height.</summary>
    public const double StartGap = 0.25;

    /// <summary>Gap between boxes, as a fraction of a box's height.</summary>
    public const double BoxGap = 0.08;

    /// <summary>Width of the whole pager for <paramref name="count"/> boxes of the given height.</summary>
    public static double Width(int count, double boxHeight, double screenAspect)
    {
        if (count <= 0)
        {
            return 0;
        }

        var boxWidth = boxHeight * screenAspect;
        return count * boxWidth + (count - 1) * Math.Max(1, boxHeight * BoxGap);
    }

    /// <summary>Smallest the boxes may shrink to fit a gap, as a fraction of their full height.</summary>
    public const double MinimumScale = 0.5;

    /// <summary>
    /// Where the pager goes on a taskbar, in physical pixels: just left of the Start button, keeping clear
    /// of anything else on the taskbar that it mustn't cover (<paramref name="obstacles"/>, such as the
    /// Widgets/weather button), and shrinking a little to fit if the gap is tight. If it can't fit there
    /// (a left-aligned taskbar puts Start at the far left), it goes just left of the notification area,
    /// then into the widest free gap. Null for a vertical or missing taskbar, or if it fits nowhere.
    /// </summary>
    public static PixelRect? Place(PixelRect taskbar, PixelRect? start, PixelRect? tray, int count, double screenAspect,
        IReadOnlyList<PixelRect>? obstacles = null)
    {
        if (taskbar.IsEmpty || taskbar.Width <= taskbar.Height || count <= 0 || screenAspect <= 0)
        {
            return null;
        }

        var padding = (int)Math.Round(taskbar.Height * VerticalPadding);
        var fullHeight = taskbar.Height - 2 * padding;
        var gap = (int)Math.Round(taskbar.Height * StartGap);

        // Free stretches of the taskbar between the things not to cover, each kept a gap away.
        var blockers = new List<PixelRect>();
        if (start is { IsEmpty: false } s) blockers.Add(s);
        if (tray is { IsEmpty: false } t) blockers.Add(t);
        blockers.AddRange((obstacles ?? []).Where(o => !o.IsEmpty));
        var free = new List<(int Left, int Right)>();
        var x = taskbar.X + gap;
        foreach (var b in blockers.Where(b => b.Right > taskbar.X && b.X < taskbar.Right).OrderBy(b => b.X))
        {
            if (b.X - gap > x)
            {
                free.Add((x, b.X - gap));
            }

            x = Math.Max(x, b.Right + gap);
        }

        if (taskbar.Right - gap > x)
        {
            free.Add((x, taskbar.Right - gap));
        }

        PixelRect? FitAgainstRight((int Left, int Right) gapRange)
        {
            var available = gapRange.Right - gapRange.Left;
            var height = fullHeight;
            if (Width(count, height, screenAspect) > available)
            {
                // Width grows in proportion to height (gaps included), so scale the height to fit.
                height = (int)Math.Floor(available / (count * screenAspect + (count - 1) * BoxGap));
                while (height > 0 && Width(count, height, screenAspect) > available)
                {
                    height--;
                }

                if (height < fullHeight * MinimumScale)
                {
                    return null;
                }
            }

            var width = (int)Math.Ceiling(Width(count, height, screenAspect));
            return new PixelRect(gapRange.Right - width, taskbar.Y + (taskbar.Height - height) / 2, width, height);
        }

        // 1. The gap that ends just left of Start; 2. the one just left of the tray; 3. the widest.
        var preferred = new List<(int Left, int Right)>();
        if (start is { IsEmpty: false } st)
        {
            preferred.AddRange(free.Where(f => f.Right == st.X - gap));
        }

        if (tray is { IsEmpty: false } tr)
        {
            preferred.AddRange(free.Where(f => f.Right == tr.X - gap));
        }

        if (start is null && tray is null)
        {
            preferred.AddRange(free.Where(f => f.Right == taskbar.Right - gap));
        }

        preferred.AddRange(free.OrderByDescending(f => f.Right - f.Left));
        foreach (var range in preferred)
        {
            if (FitAgainstRight(range) is { } placed)
            {
                return placed;
            }
        }

        return null;
    }

    /// <summary>
    /// On a centred taskbar (Windows 11's default) the Start button and app buttons form one group centred
    /// on the taskbar, so its right end mirrors Start's left edge. The pager must not cover any of it.
    /// Null if Start isn't left of the middle (then the taskbar isn't centred after all).
    /// </summary>
    public static PixelRect? CenteredAppGroup(PixelRect taskbar, PixelRect start)
    {
        var middle = taskbar.X + taskbar.Width / 2;
        if (start.IsEmpty || start.X >= middle)
        {
            return null;
        }

        return new PixelRect(start.X, taskbar.Y, 2 * (middle - start.X), taskbar.Height);
    }

    /// <summary>
    /// Keep the pager stuck to a taskbar that's sliding (auto-hide): move it by as much as the taskbar has
    /// moved since the pager was placed. Null once it would be off its monitor (the taskbar is hidden).
    /// </summary>
    public static PixelRect? Follow(PixelRect placed, PixelRect taskbarWhenPlaced, PixelRect taskbarNow, PixelRect monitor)
    {
        var moved = placed with
        {
            X = placed.X + taskbarNow.X - taskbarWhenPlaced.X,
            Y = placed.Y + taskbarNow.Y - taskbarWhenPlaced.Y,
        };

        // Wholly on its monitor, or not at all: part-way it would show on a neighbouring monitor.
        return moved.Intersect(monitor) == moved ? moved : null;
    }

    /// <summary>The boxes inside a pager of the given size, left to right.</summary>
    public static IReadOnlyList<BoxRect> Boxes(int count, double width, double height)
    {
        if (count <= 0 || width <= 0 || height <= 0)
        {
            return [];
        }

        var gap = Math.Max(1, height * BoxGap);
        var boxWidth = Math.Max(1, (width - (count - 1) * gap) / count);
        return Enumerable.Range(0, count).Select(i => new BoxRect(i * (boxWidth + gap), 0, boxWidth, height)).ToList();
    }

    /// <summary>Which box a point is in, or null for a gap or outside.</summary>
    public static int? BoxAt(IReadOnlyList<BoxRect> boxes, double x, double y)
    {
        for (var i = 0; i < boxes.Count; i++)
        {
            if (boxes[i].Contains(x, y))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>A window's outline inside a box, scaled from the monitor; null if it's not on this monitor.</summary>
    public static BoxRect? MiniWindow(PixelRect window, PixelRect monitor, BoxRect box)
    {
        var visible = window.Intersect(monitor);
        if (visible.IsEmpty || monitor.IsEmpty)
        {
            return null;
        }

        var sx = box.Width / monitor.Width;
        var sy = box.Height / monitor.Height;
        return new BoxRect(
            box.X + (visible.X - monitor.X) * sx,
            box.Y + (visible.Y - monitor.Y) * sy,
            Math.Max(1, visible.Width * sx),
            Math.Max(1, visible.Height * sy));
    }

    /// <summary>
    /// The desktop the mouse wheel moves to: scrolling down (negative delta) goes to the next desktop, as in
    /// Plasma, wrapping round at the ends.
    /// </summary>
    public static int Wheel(int current, int count, int delta)
    {
        if (count <= 0 || delta == 0)
        {
            return current;
        }

        var step = delta < 0 ? 1 : -1;
        return ((current + step) % count + count) % count;
    }
}
