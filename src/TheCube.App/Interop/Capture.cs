using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TheCube.Core;

namespace TheCube.App.Interop;

/// <summary>Screen and window capture with GDI. Results are frozen Bgr32 bitmaps, usable from any thread.</summary>
internal static class Capture
{
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;
    private const uint PW_RENDERFULLCONTENT = 0x2;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;

        public readonly PixelRect ToPixelRect() => PixelRect.FromEdges(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hDC);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER bmi, uint usage, out nint bits, nint section, uint offset);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(nint hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attr, out RECT value, int size);

    /// <summary>The window's visible frame (without the invisible resize border), in physical pixels.</summary>
    public static PixelRect GetFrame(nint hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var rect, Marshal.SizeOf<RECT>()) == 0)
        {
            return rect.ToPixelRect();
        }

        return GetWindowRect(hwnd, out rect) ? rect.ToPixelRect() : default;
    }

    /// <summary>Grab part of the screen as it is now (physical pixels).</summary>
    public static BitmapSource? Screen(PixelRect area) =>
        WithDib(area.Width, area.Height, (memDc, _) =>
        {
            var screenDc = GetDC(0);
            try
            {
                return BitBlt(memDc, 0, 0, area.Width, area.Height, screenDc, area.X, area.Y, SRCCOPY | CAPTUREBLT);
            }
            finally
            {
                ReleaseDC(0, screenDc);
            }
        }, crop: new PixelRect(0, 0, area.Width, area.Height), rejectBlank: false);

    /// <summary>
    /// Ask Windows to render a window into a bitmap, even when it's covered or on another desktop.
    /// Returns null if it fails or comes back blank (common for some GPU-rendered apps).
    /// </summary>
    public static BitmapSource? Window(nint hwnd, PixelRect frame)
    {
        if (IsHungAppWindow(hwnd) || !GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        var outer = rect.ToPixelRect();
        var crop = frame.Intersect(outer).RelativeTo(outer);
        if (crop.IsEmpty)
        {
            return null;
        }

        return WithDib(outer.Width, outer.Height, (memDc, _) => PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT), crop, rejectBlank: true);
    }

    private static BitmapSource? WithDib(int width, int height, Func<nint, nint, bool> draw, PixelRect crop, bool rejectBlank)
    {
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
        {
            return null;
        }

        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };

        var memDc = CreateCompatibleDC(0);
        var dib = CreateDIBSection(memDc, ref header, 0, out var bits, 0, 0);
        if (dib == 0)
        {
            DeleteDC(memDc);
            return null;
        }

        var old = SelectObject(memDc, dib);
        try
        {
            if (!draw(memDc, bits))
            {
                return null;
            }

            var srcStride = width * 4;
            var dstStride = crop.Width * 4;
            var pixels = new byte[dstStride * crop.Height];
            for (var y = 0; y < crop.Height; y++)
            {
                Marshal.Copy(bits + (crop.Y + y) * srcStride + crop.X * 4, pixels, y * dstStride, dstStride);
            }

            if (rejectBlank && PixelChecks.LooksBlank(pixels, crop.Width, crop.Height, dstStride, ignoreAlpha: true))
            {
                return null;
            }

            var bitmap = BitmapSource.Create(crop.Width, crop.Height, 96, 96, PixelFormats.Bgr32, null, pixels, dstStride);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(dib);
            DeleteDC(memDc);
        }
    }
}
