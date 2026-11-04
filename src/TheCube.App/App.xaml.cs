using System.IO;
using System.Windows;
using System.Windows.Threading;
using TheCube.App.Interop;
using TheCube.Core;

namespace TheCube.App;

public partial class App : Application
{
    public static string SettingsDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "The Cube");

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    private Mutex? _singleInstance;
    private HotkeyManager? _hotkeys;

    internal CubeSettings Settings { get; private set; } = new();
    internal IVirtualDesktopService? Desktops { get; private set; }
    internal DesktopImageProvider? Images { get; private set; }
    internal CubeSession? Cube { get; private set; }
    internal List<string> HotkeyProblems { get; } = [];
    internal string? StartupError { get; private set; }
    internal int DesktopsAdded { get; private set; }

    /// <summary>Raised on the UI thread for anything worth showing in the status line.</summary>
    internal event Action<string>? Status;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\TheCube.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("The Cube is already running.", "The Cube", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            Settings = CubeSettings.Load(SettingsPath);
            if (!File.Exists(SettingsPath))
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
            DesktopsAdded = DesktopActions.EnsureCount(Desktops, Settings.EnsureDesktopCount);
        }
        catch (VirtualDesktopsUnsupportedException ex)
        {
            StartupError = ex.Message;
        }

        Cube = new CubeSession(this);
        _hotkeys = new HotkeyManager();
        RegisterHotkeys();

        MainWindow = new DiagnosticsWindow(this);
        MainWindow.Show();
    }

    private void RegisterHotkeys()
    {
        void Add(string text, Action action)
        {
            if (_hotkeys!.Register(text, action) is { } problem)
            {
                HotkeyProblems.Add(problem);
            }
        }

        Add(Settings.ToggleCubeHotkey, () => Cube?.Toggle());

        for (var i = 0; i < Settings.SwitchToDesktopHotkeys.Count; i++)
        {
            var index = i;
            Add(Settings.SwitchToDesktopHotkeys[i], () => SwitchFromHotkey(index));
        }
    }

    internal void ReportStatus(string message) => Status?.Invoke(message);

    private void SwitchFromHotkey(int index)
    {
        if (Desktops is null || Cube?.IsOpen == true)
        {
            return;
        }

        if (DesktopActions.SwitchTo(Desktops, index, animate: true))
        {
            Status?.Invoke($"Switched to desktop {index + 1}");
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        MessageBox.Show(e.Exception.ToString(), "The Cube — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
