using System.IO;
using System.Windows;
using System.Windows.Threading;
using TheCube.App.Interop;
using TheCube.Core;

namespace TheCube.App;

/// <summary>
/// Tray app: owns the settings, the virtual desktop connection, hotkeys, screen edges, the taskbar pager and the cube.
/// Runs until "Quit The Cube" in the tray menu.
/// </summary>
public partial class App : Application
{
    public static string SettingsDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "The Cube");

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    private const string MutexName = @"Local\TheCube.SingleInstance";
    private const string ShowEventName = @"Local\TheCube.Show";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private HotkeyManager? _hotkeys;
    private KeyboardHook? _keyboardHook;
    private EdgeWatcher? _edges;
    private PagerManager? _pager;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private DiagnosticsWindow? _diagnosticsWindow;
    private bool _hotkeysSuspended;

    internal CubeSettings Settings { get; private set; } = new();
    internal IVirtualDesktopService? Desktops { get; private set; }
    internal DesktopImageProvider? Images { get; private set; }
    internal CubeSession? Cube { get; private set; }

    /// <summary>Hotkeys that couldn't be set up at all.</summary>
    internal List<string> HotkeyProblems { get; } = [];

    /// <summary>Hotkeys handled by the keyboard-hook fallback because Windows wouldn't register them.</summary>
    internal List<string> HookedHotkeys { get; } = [];

    internal string? StartupError { get; private set; }
    internal int DesktopsAdded { get; private set; }

    /// <summary>Raised on the UI thread for anything worth showing in the status line.</summary>
    internal event Action<string>? Status;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteErrorLog("unobserved task error", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteErrorLog("fatal error", e.ExceptionObject as Exception);

        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out var first);
        if (!first)
        {
            // Already running: ask that copy to show its settings, then leave.
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The other copy is still starting up.
            }

            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(ShowSettings), null, Timeout.Infinite, executeOnlyOnce: false);

        var firstRun = !File.Exists(SettingsPath);
        try
        {
            Settings = CubeSettings.Load(SettingsPath);
            if (firstRun)
            {
                Settings.Save(SettingsPath); // so there's a file to edit
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            StartupError = $"Couldn't read {SettingsPath}: {ex.Message}. Using the defaults.";
            Settings = new CubeSettings();
        }

        try
        {
            var windowsDesktops = WindowsVirtualDesktops.Create();
            Desktops = windowsDesktops;
            Images = new DesktopImageProvider(windowsDesktops);
        }
        catch (VirtualDesktopsUnsupportedException ex)
        {
            StartupError = ex.Message;
        }

        if (Desktops is not null)
        {
            try
            {
                DesktopsAdded = DesktopActions.EnsureCount(Desktops, Settings.EnsureDesktopCount);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
            {
                // Not fatal: the cube still works with the desktops there are.
                StartupError = "Couldn't add virtual desktops: " + ex.Message;
            }
        }

        var (build, revision) = NativeMethods.GetWindowsBuild();
        EventLog.Write($"started: The Cube {typeof(App).Assembly.GetName().Version} on Windows build {build}.{revision}, " +
                       $"interfaces={Desktops?.BackendName ?? "none"}, error={StartupError ?? "none"}");

        Cube = new CubeSession(this);
        _edges = new EdgeWatcher(() =>
        {
            if (Cube?.IsOpen == false)
            {
                Cube.Toggle();
            }
        });
        _edges.Configure(Settings.BorderActivate);
        _pager = new PagerManager(this);
        _pager.Configure(Settings.ShowPager);
        RegisterHotkeys();
        TryApplyStartWithWindows();

        _tray = new TrayIcon(this);
        EventLog.Write($"ready {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds:0} ms after launch");

        var background = e.Args.Contains(StartWithWindows.BackgroundArgument);
        if (StartupError is not null || HotkeyProblems.Count > 0)
        {
            ShowDiagnostics(); // something needs the user's attention
        }
        else if (!background)
        {
            var hotkey = string.IsNullOrWhiteSpace(Settings.ToggleCubeHotkey) ? "the tray icon" : Settings.ToggleCubeHotkey;
            _tray.ShowBalloon("The Cube is running", $"Press {hotkey} to open the cube. Right-click the tray icon for settings.");
        }
    }

    /// <summary>Apply edited settings: save, re-register hotkeys, update edges, the pager and start-up, and add desktops if needed. Returns an error to show, or null.</summary>
    internal string? ApplySettings(CubeSettings settings)
    {
        try
        {
            settings.Save(SettingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't save {SettingsPath}: {ex.Message}";
        }

        Settings = settings;
        RegisterHotkeys();
        _edges?.Configure(Settings.BorderActivate);
        _pager?.Configure(Settings.ShowPager);
        TryApplyStartWithWindows();
        if (Desktops is not null)
        {
            try
            {
                DesktopActions.EnsureCount(Desktops, Settings.EnsureDesktopCount);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
            {
                ReportStatus("Couldn't add desktops: " + ex.Message);
            }
        }

        _tray?.Refresh();
        return HotkeyProblems.Count > 0 ? "Some shortcuts couldn't be set up:\n\n" + string.Join("\n", HotkeyProblems) : null;
    }

    internal void SetStartWithWindows(bool enabled)
    {
        var updated = Settings.Clone();
        updated.StartWithWindows = enabled;
        ApplySettings(updated);
    }

    private void TryApplyStartWithWindows()
    {
        try
        {
            StartWithWindows.Apply(Settings.StartWithWindows);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            ReportStatus("Couldn't change Start with Windows: " + ex.Message);
        }
    }

    /// <summary>Turn all shortcuts off while the user records a new one in the settings window.</summary>
    internal void SuspendHotkeys(bool suspend)
    {
        if (suspend == _hotkeysSuspended)
        {
            return;
        }

        _hotkeysSuspended = suspend;
        if (suspend)
        {
            UnregisterHotkeys();
        }
        else
        {
            RegisterHotkeys();
        }
    }

    private void UnregisterHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
        _keyboardHook?.Dispose();
        _keyboardHook = null;
    }

    private void RegisterHotkeys()
    {
        UnregisterHotkeys();
        HotkeyProblems.Clear();
        HookedHotkeys.Clear();
        if (_hotkeysSuspended)
        {
            return;
        }

        _hotkeys = new HotkeyManager();
        _keyboardHook = new KeyboardHook(Dispatcher);

        void Add(string text, Action action)
        {
            if (string.IsNullOrWhiteSpace(text) || _hotkeys.Register(text, action) is not { } problem)
            {
                return;
            }

            // Windows (or another app) owns it: take it with the keyboard hook instead.
            if (Hotkey.TryParse(text, out var hotkey) && _keyboardHook.Add(hotkey, action))
            {
                HookedHotkeys.Add(hotkey.ToString());
            }
            else
            {
                HotkeyProblems.Add(problem);
            }
        }

        Add(Settings.ToggleCubeHotkey, () =>
        {
            EventLog.Write($"hotkey {Settings.ToggleCubeHotkey}");
            Cube?.Toggle();
        });
        for (var i = 0; i < Settings.SwitchToDesktopHotkeys.Count; i++)
        {
            var index = i;
            Add(Settings.SwitchToDesktopHotkeys[i], () => SwitchFromHotkey(index));
        }

        EventLog.Write($"hotkeys: via keyboard hook=[{string.Join(", ", HookedHotkeys)}] problems=[{string.Join(" | ", HotkeyProblems)}]");
    }

    /// <summary>Log a status message and show it in the Diagnostics window if that's open.</summary>
    internal void ReportStatus(string message)
    {
        EventLog.Write("status: " + message);
        Status?.Invoke(message);
    }

    /// <summary>Report a failure the user caused directly (a hotkey or pager click): log it and show a tray balloon.</summary>
    internal void ReportFailure(string message)
    {
        ReportStatus(message);
        _tray?.ShowBalloon("The Cube", message);
    }

    private void SwitchFromHotkey(int index)
    {
        if (Desktops is null || Cube?.IsOpen == true)
        {
            return;
        }

        try
        {
            if (DesktopActions.SwitchTo(Desktops, index, animate: true))
            {
                EventLog.Write($"hotkey: switched to desktop {index + 1}");
                ReportStatus($"Switched to desktop {index + 1}");
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            ReportFailure("Couldn't switch desktop: " + ex.Message);
        }
    }

    internal void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) =>
        {
            SuspendHotkeys(false);
            _settingsWindow = null;
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    internal void ShowDiagnostics()
    {
        if (_diagnosticsWindow is { IsLoaded: true })
        {
            _diagnosticsWindow.Activate();
            return;
        }

        _diagnosticsWindow = new DiagnosticsWindow(this);
        _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow.Show();
        _diagnosticsWindow.Activate();
    }

    private bool _showingError;
    private DateTime _lastErrorBox = DateTime.MinValue;

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        WriteErrorLog("unhandled error", e.Exception);

        // A failing timer would otherwise stack up a new box every tick (the box runs a nested message loop).
        if (_showingError || DateTime.UtcNow - _lastErrorBox < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _showingError = true;
        try
        {
            MessageBox.Show(e.Exception.Message + "\n\nDetails were written to error.log in " + SettingsDirectory,
                "The Cube — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _showingError = false;
            _lastErrorBox = DateTime.UtcNow;
        }
    }

    private static void WriteErrorLog(string what, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.AppendAllText(Path.Combine(SettingsDirectory, "error.log"), $"{DateTime.Now:u} {what}: {ex}\n\n");
        }
        catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException)
        {
            // Nowhere to log.
        }

        EventLog.Write($"{what}: {ex?.GetType().Name}: {ex?.Message}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _edges?.Dispose();
        _pager?.Dispose();
        UnregisterHotkeys();
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
