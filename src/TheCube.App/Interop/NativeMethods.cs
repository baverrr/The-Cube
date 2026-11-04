using System.Runtime.InteropServices;

namespace TheCube.App.Interop;

internal static class NativeMethods
{
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_NOREPEAT = 0x4000;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80;
    public const long WS_EX_APPWINDOW = 0x40000;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const int DWMWA_CLOAKED = 14;
    public const uint SPI_GETDESKWALLPAPER = 0x0073;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct OSVERSIONINFOEXW
    {
        public int dwOSVersionInfoSize;
        public int dwMajorVersion;
        public int dwMinorVersion;
        public int dwBuildNumber;
        public int dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;
    }

    /// <summary>Real OS version (not affected by compatibility manifests).</summary>
    [DllImport("ntdll.dll")]
    public static extern int RtlGetVersion(ref OSVERSIONINFOEXW info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(nint hWnd, int id);

    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    public static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, char[] pvParam, uint fWinIni);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    // HSTRING helpers: .NET 5+ no longer marshals UnmanagedType.HString, so HSTRINGs come back as handles.
    [DllImport("combase.dll")]
    public static extern nint WindowsGetStringRawBuffer(nint hstring, out uint length);

    [DllImport("combase.dll")]
    public static extern int WindowsDeleteString(nint hstring);

    /// <summary>Copy an HSTRING to a .NET string and free it.</summary>
    public static string? TakeHString(nint hstring)
    {
        if (hstring == 0)
        {
            return null;
        }

        try
        {
            var buffer = WindowsGetStringRawBuffer(hstring, out var length);
            return buffer == 0 ? null : Marshal.PtrToStringUni(buffer, (int)length);
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    public static string? GetSystemWallpaper()
    {
        var buffer = new char[260];
        if (!SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)buffer.Length, buffer, 0))
        {
            return null;
        }

        var path = new string(buffer).TrimEnd('\0');
        return path.Length == 0 ? null : path;
    }

    public static bool IsCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    public static (int Build, int Revision) GetWindowsBuild()
    {
        var info = new OSVERSIONINFOEXW { dwOSVersionInfoSize = Marshal.SizeOf<OSVERSIONINFOEXW>() };
        RtlGetVersion(ref info);
        var ubr = Microsoft.Win32.Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", 0) as int? ?? 0;
        return (info.dwBuildNumber, ubr);
    }
}
