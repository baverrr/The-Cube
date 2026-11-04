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

    /// <summary>
    /// Where the pager goes on a taskbar, in physical pixels: just left of the Start button. If there's no
    /// room there (a left-aligned taskbar puts Start at the far left), just left of the notification area,
    /// else at the right end. Null for a vertical or missing taskbar, or if it doesn't fit anywhere.
    /// </summary>
    public static PixelRect? Place(PixelRect taskbar, PixelRect? start, PixelRect? tray, int count, double screenAspect)
    {
        if (taskbar.IsEmpty || taskbar.Width <= taskbar.Height || count <= 0)
        {
            return null;
        }

        var padding = (int)Math.Round(taskbar.Height * VerticalPadding);
        var height = taskbar.Height - 2 * padding;
        var width = (int)Math.Ceiling(Width(count, height, screenAspect));
        var gap = (int)Math.Round(taskbar.Height * StartGap);
        var y = taskbar.Y + padding;

        // Never on top of Start or the notification area.
        bool Clear(PixelRect r) =>
            r.X >= taskbar.X && r.Right <= taskbar.Right
            && !(start is { IsEmpty: false } st && r.IntersectsWith(st))
            && !(tray is { IsEmpty: false } tr && r.IntersectsWith(tr));

        if (start is { IsEmpty: false } s)
        {
            var left = new PixelRect(s.X - gap - width, y, width, height);
            if (left.X >= taskbar.X + gap && Clear(left))
            {
                return left;
            }
        }

        var right = tray is { IsEmpty: false } t && t.X > taskbar.X ? t.X : taskbar.Right;
        var fallback = new PixelRect(right - gap - width, y, width, height);
        return Clear(fallback) ? fallback : null;
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
