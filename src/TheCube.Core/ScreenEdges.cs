namespace TheCube.Core;

/// <summary>Which screen edge or corner the pointer is pushing against, if any.</summary>
public static class ScreenEdges
{
    /// <summary>How far along an edge from a corner still counts as that corner, in pixels.</summary>
    public const int CornerSize = 12;

    /// <summary>
    /// Classify the pointer position on <paramref name="monitor"/>. Only outer edges count: an edge
    /// shared with a neighbouring monitor isn't one the pointer can push against.
    /// </summary>
    /// <param name="onAnyMonitor">True if a point lies on some monitor.</param>
    public static ScreenEdge? Classify(int x, int y, PixelRect monitor, Func<int, int, bool> onAnyMonitor)
    {
        var left = x <= monitor.X && !onAnyMonitor(monitor.X - 1, y);
        var right = x >= monitor.Right - 1 && !onAnyMonitor(monitor.Right, y);
        var top = y <= monitor.Y && !onAnyMonitor(x, monitor.Y - 1);
        var bottom = y >= monitor.Bottom - 1 && !onAnyMonitor(x, monitor.Bottom);

        var nearTop = y < monitor.Y + CornerSize;
        var nearBottom = y >= monitor.Bottom - CornerSize;
        var nearLeft = x < monitor.X + CornerSize;
        var nearRight = x >= monitor.Right - CornerSize;

        if ((top && nearLeft) || (left && nearTop)) return ScreenEdge.TopLeft;
        if ((top && nearRight) || (right && nearTop)) return ScreenEdge.TopRight;
        if ((bottom && nearLeft) || (left && nearBottom)) return ScreenEdge.BottomLeft;
        if ((bottom && nearRight) || (right && nearBottom)) return ScreenEdge.BottomRight;
        if (top) return ScreenEdge.Top;
        if (bottom) return ScreenEdge.Bottom;
        if (left) return ScreenEdge.Left;
        if (right) return ScreenEdge.Right;
        return null;
    }
}

/// <summary>
/// Fires once the pointer has rested on an edge for <see cref="DwellMs"/> (KWin's default activation
/// delay is 150 ms), and not again until it leaves that edge.
/// </summary>
public sealed class EdgeDwellTracker(double dwellMs = 150)
{
    private ScreenEdge? _edge;
    private double _since;
    private bool _fired;

    public double DwellMs { get; } = dwellMs;

    /// <summary>Feed the current edge (or null). Returns the edge when it should trigger.</summary>
    public ScreenEdge? Update(ScreenEdge? edge, double now)
    {
        if (edge != _edge)
        {
            _edge = edge;
            _since = now;
            _fired = false;
        }

        if (_edge is { } e && !_fired && now - _since >= DwellMs)
        {
            _fired = true;
            return e;
        }

        return null;
    }
}
