using System.Runtime.InteropServices;
using TheCube.Core;

namespace TheCube.App.Interop;

/// <summary>A display, in physical pixels (the app is per-monitor DPI aware).</summary>
internal sealed record MonitorInfo(string DeviceName, PixelRect Bounds, bool IsPrimary);

internal static class Monitors
{
    private const uint MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public Capture.RECT rcMonitor;
        public Capture.RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref Capture.RECT rect, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX info);

    /// <summary>All monitors, primary first, then left to right.</summary>
    public static IReadOnlyList<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(0, 0, (nint handle, nint _, ref Capture.RECT _, nint _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(handle, ref info))
            {
                list.Add(new MonitorInfo(info.szDevice, info.rcMonitor.ToPixelRect(), (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }

            return true;
        }, 0);

        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ToList();
    }

    /// <summary>Bounding box of all monitors.</summary>
    public static PixelRect VirtualScreen(IReadOnlyList<MonitorInfo> monitors) =>
        monitors.Count == 0
            ? default
            : PixelRect.FromEdges(
                monitors.Min(m => m.Bounds.X), monitors.Min(m => m.Bounds.Y),
                monitors.Max(m => m.Bounds.Right), monitors.Max(m => m.Bounds.Bottom));
}

/// <summary>Documented IDesktopWallpaper (shobjidl_core.h): per-monitor wallpaper, position and colour.</summary>
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
internal interface IDesktopWallpaper
{
    void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetMonitorDevicePathAt(uint monitorIndex);

    uint GetMonitorDevicePathCount();

    Capture.RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);

    void SetBackgroundColor(uint color);

    uint GetBackgroundColor();

    void SetPosition(WallpaperPosition position);

    WallpaperPosition GetPosition();
}

/// <summary>Wallpaper settings for the current desktop, per monitor. Must be read on the UI thread.</summary>
internal sealed record WallpaperSettings(
    WallpaperPosition Position, System.Windows.Media.Color Background, IReadOnlyDictionary<PixelRect, string> ByMonitor, string? Fallback)
{
    private static readonly Guid CLSID_DesktopWallpaper = new("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD");

    public string? PathFor(MonitorInfo monitor) => ByMonitor.TryGetValue(monitor.Bounds, out var p) ? p : Fallback;

    public static WallpaperSettings Read()
    {
        var fallback = NativeMethods.GetSystemWallpaper();
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_DesktopWallpaper, throwOnError: true)!;
            var wallpaper = (IDesktopWallpaper)Activator.CreateInstance(type)!;
            try
            {
                var byMonitor = new Dictionary<PixelRect, string>();
                var count = wallpaper.GetMonitorDevicePathCount();
                for (uint i = 0; i < count; i++)
                {
                    try
                    {
                        var id = wallpaper.GetMonitorDevicePathAt(i);
                        var path = wallpaper.GetWallpaper(id);
                        if (!string.IsNullOrEmpty(path))
                        {
                            byMonitor[wallpaper.GetMonitorRECT(id).ToPixelRect()] = path;
                        }
                    }
                    catch (COMException)
                    {
                        // Detached monitors are listed but fail; skip them.
                    }
                }

                var bgr = wallpaper.GetBackgroundColor(); // COLORREF 0x00BBGGRR
                var color = System.Windows.Media.Color.FromRgb((byte)bgr, (byte)(bgr >> 8), (byte)(bgr >> 16));
                return new WallpaperSettings(wallpaper.GetPosition(), color, byMonitor, fallback);
            }
            finally
            {
                Marshal.ReleaseComObject(wallpaper);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            EventLog.Write($"wallpaper: couldn't read the wallpaper settings: {ex.GetType().Name} 0x{ex.HResult:X8}");
            return new WallpaperSettings(WallpaperPosition.Fill, System.Windows.Media.Colors.Black, new Dictionary<PixelRect, string>(), fallback);
        }
    }
}
