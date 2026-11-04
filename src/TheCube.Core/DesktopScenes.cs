namespace TheCube.Core;

/// <summary>An integer rectangle in physical pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect FromEdges(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);

    public PixelRect Intersect(PixelRect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right > left && bottom > top ? FromEdges(left, top, right, bottom) : default;
    }

    public bool IntersectsWith(PixelRect other) => !Intersect(other).IsEmpty;

    /// <summary>The same rectangle relative to <paramref name="origin"/>'s top-left corner.</summary>
    public PixelRect RelativeTo(PixelRect origin) => this with { X = X - origin.X, Y = Y - origin.Y };
}

/// <summary>How a wallpaper is laid out (same values as Windows' DESKTOP_WALLPAPER_POSITION).</summary>
public enum WallpaperPosition
{
    Center = 0,
    Tile = 1,
    Stretch = 2,
    Fit = 3,
    Fill = 4,
    Span = 5,
}

public static class WallpaperLayout
{
    /// <summary>
    /// Where to draw an <paramref name="imageWidth"/>×<paramref name="imageHeight"/> wallpaper for one
    /// monitor, in coordinates relative to that monitor's top-left corner. Draw each rectangle,
    /// clipped to the monitor. <paramref name="virtualScreen"/> is the bounding box of all monitors (for Span).
    /// </summary>
    public static IReadOnlyList<PixelRect> Place(
        WallpaperPosition position, int imageWidth, int imageHeight, PixelRect monitor, PixelRect virtualScreen)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || monitor.IsEmpty)
        {
            return [];
        }

        var local = new PixelRect(0, 0, monitor.Width, monitor.Height);
        switch (position)
        {
            case WallpaperPosition.Stretch:
                return [local];

            case WallpaperPosition.Center:
                return [new PixelRect((monitor.Width - imageWidth) / 2, (monitor.Height - imageHeight) / 2, imageWidth, imageHeight)];

            case WallpaperPosition.Fit:
                return [Scale(imageWidth, imageHeight, local, Math.Min)];

            case WallpaperPosition.Fill:
                return [Scale(imageWidth, imageHeight, local, Math.Max)];

            case WallpaperPosition.Span:
                // Fill the whole virtual screen, then show this monitor's part of it.
                return [Scale(imageWidth, imageHeight, virtualScreen, Math.Max).RelativeTo(monitor)];

            case WallpaperPosition.Tile:
            {
                // A tiny pattern would mean tens of thousands of drawings per face; draw it stretched instead.
                const long MaxTiles = 1024;
                var across = (monitor.Width + imageWidth - 1) / imageWidth;
                var down = (monitor.Height + imageHeight - 1) / imageHeight;
                if ((long)across * down > MaxTiles)
                {
                    return [new PixelRect(0, 0, monitor.Width, monitor.Height)];
                }

                var tiles = new List<PixelRect>();
                for (var y = 0; y < monitor.Height; y += imageHeight)
                {
                    for (var x = 0; x < monitor.Width; x += imageWidth)
                    {
                        tiles.Add(new PixelRect(x, y, imageWidth, imageHeight));
                    }
                }

                return tiles;
            }

            default:
                return [local];
        }
    }

    private static PixelRect Scale(int w, int h, PixelRect area, Func<double, double, double> pick)
    {
        var scale = pick((double)area.Width / w, (double)area.Height / h);
        var sw = (int)Math.Round(w * scale);
        var sh = (int)Math.Round(h * scale);
        return new PixelRect(area.X + (area.Width - sw) / 2, area.Y + (area.Height - sh) / 2, sw, sh);
    }
}

/// <summary>A top-level window as seen when building desktop images.</summary>
/// <param name="Handle">HWND.</param>
/// <param name="Frame">Visible frame in physical screen pixels (without the invisible resize border).</param>
/// <param name="Desktop">0-based desktop index, or null if Windows doesn't track it.</param>
/// <param name="AllDesktops">Shown on every desktop (taskbar, pinned windows).</param>
/// <param name="Minimized">Minimized windows aren't drawn.</param>
public sealed record WindowEntry(nint Handle, PixelRect Frame, int? Desktop, bool AllDesktops, bool Minimized);

public static class SceneBuilder
{
    /// <summary>
    /// Windows to draw for <paramref name="desktop"/> on <paramref name="monitor"/>, bottom to top.
    /// <paramref name="topToBottom"/> is in z-order as EnumWindows returns it.
    /// </summary>
    public static IReadOnlyList<WindowEntry> WindowsFor(IReadOnlyList<WindowEntry> topToBottom, int desktop, PixelRect monitor)
    {
        var result = new List<WindowEntry>();
        for (var i = topToBottom.Count - 1; i >= 0; i--)
        {
            var w = topToBottom[i];
            if (w.Minimized || w.Frame.IsEmpty || !w.Frame.IntersectsWith(monitor))
            {
                continue;
            }

            if (w.AllDesktops || w.Desktop == desktop)
            {
                result.Add(w);
            }
        }

        return result;
    }
}

public static class PixelChecks
{
    /// <summary>
    /// True if a BGRA32 image looks empty (every sampled pixel black or fully transparent), which is
    /// what a failed window capture usually produces. Samples a grid instead of every pixel.
    /// </summary>
    /// <param name="ignoreAlpha">For Bgr32 images, where the fourth byte is padding.</param>
    public static bool LooksBlank(ReadOnlySpan<byte> bgra, int width, int height, int stride, int samplesPerAxis = 16, bool ignoreAlpha = false)
    {
        if (width <= 0 || height <= 0)
        {
            return true;
        }

        for (var sy = 0; sy < samplesPerAxis; sy++)
        {
            var y = (int)((sy + 0.5) * height / samplesPerAxis);
            for (var sx = 0; sx < samplesPerAxis; sx++)
            {
                var x = (int)((sx + 0.5) * width / samplesPerAxis);
                var i = y * stride + x * 4;
                if (i + 3 >= bgra.Length)
                {
                    continue;
                }

                var alpha = ignoreAlpha ? (byte)255 : bgra[i + 3];
                if (alpha != 0 && (bgra[i] | bgra[i + 1] | bgra[i + 2]) != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }
}

/// <summary>
/// Last good image of each window, used when a window can't be captured now (for example a GPU app
/// on another desktop). An entry only matches while the window keeps the same size.
/// </summary>
public sealed class WindowImageCache<TImage> where TImage : class
{
    private readonly Dictionary<nint, (int Width, int Height, TImage Image)> _entries = [];

    public int Count => _entries.Count;

    public void Put(nint hwnd, int width, int height, TImage image) => _entries[hwnd] = (width, height, image);

    public TImage? Get(nint hwnd, int width, int height) =>
        _entries.TryGetValue(hwnd, out var e) && e.Width == width && e.Height == height ? e.Image : null;

    /// <summary>Forget windows that no longer exist.</summary>
    public void Prune(IEnumerable<nint> alive)
    {
        var keep = alive.ToHashSet();
        foreach (var hwnd in _entries.Keys.Where(h => !keep.Contains(h)).ToList())
        {
            _entries.Remove(hwnd);
        }
    }
}
