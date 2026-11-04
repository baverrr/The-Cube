using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TheCube.App;

/// <summary>
/// Small rolling log of what The Cube did (hotkeys, opening/closing, focus changes, switches) in
/// %APPDATA%\The Cube\cube.log, for diagnosing problems on someone else's PC. Records window classes
/// and process names, never window titles.
/// </summary>
internal static class EventLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Lock = new();

    public static string PathName => Path.Combine(App.SettingsDirectory, "cube.log");

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(App.SettingsDirectory);
                var info = new FileInfo(PathName);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(PathName, PathName + ".old", overwrite: true);
                }

                File.AppendAllText(PathName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}\r\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never break the app.
        }
    }

    /// <summary>"ClassName (process[, us])" for the current foreground window.</summary>
    public static string ForegroundWindow()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0)
        {
            return "none";
        }

        var cls = new StringBuilder(128);
        GetClassName(hwnd, cls, cls.Capacity);
        GetWindowThreadProcessId(hwnd, out var pid);
        string process;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            process = p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            process = "?";
        }

        return $"{cls} ({process}{(pid == Environment.ProcessId ? ", us" : "")})";
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}
