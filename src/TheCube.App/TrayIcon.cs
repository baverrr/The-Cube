using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace TheCube.App;

/// <summary>The app icon, for the tray and for windows.</summary>
internal static class AppIcon
{
    private static Stream Open() =>
        typeof(AppIcon).Assembly.GetManifestResourceStream("TheCube.ico")
        ?? throw new InvalidOperationException("The embedded icon is missing.");

    public static System.Drawing.Icon ForTray()
    {
        using var stream = Open();
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public static ImageSource ForWindow()
    {
        using var stream = Open();
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        frame.Freeze();
        return frame;
    }
}

/// <summary>Adds or removes The Cube from the current user's sign-in programs.</summary>
internal static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "The Cube";
    public const string BackgroundArgument = "--background";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled && Environment.ProcessPath is { } exe)
        {
            key.SetValue(ValueName, $"\"{exe}\" {BackgroundArgument}");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

/// <summary>Notification-area icon: left-click opens the cube; right-click shows the menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly App _app;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _openItem;
    private readonly Forms.ToolStripMenuItem _startItem;

    public TrayIcon(App app)
    {
        _app = app;
        _openItem = new Forms.ToolStripMenuItem("Open cube", null, (_, _) => _app.Cube?.Toggle()) { Font = BoldMenuFont() };
        _startItem = new Forms.ToolStripMenuItem("Start with Windows", null, (_, _) => _app.SetStartWithWindows(!_app.Settings.StartWithWindows));

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_openItem);
        menu.Items.Add("Settings…", null, (_, _) => _app.ShowSettings());
        menu.Items.Add("Diagnostics…", null, (_, _) => _app.ShowDiagnostics());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_startItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit The Cube", null, (_, _) => _app.Shutdown());
        menu.Opening += (_, _) => Refresh();

        _icon = new Forms.NotifyIcon
        {
            Icon = AppIcon.ForTray(),
            Text = "The Cube",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                _app.Cube?.Toggle();
            }
        };
        Refresh();
    }

    public void Refresh()
    {
        var hotkey = _app.Settings.ToggleCubeHotkey;
        _openItem.Text = string.IsNullOrWhiteSpace(hotkey) ? "Open cube" : $"Open cube\t{hotkey}";
        _startItem.Checked = _app.Settings.StartWithWindows;
        _icon.Text = string.IsNullOrWhiteSpace(hotkey) ? "The Cube" : $"The Cube — {hotkey}";
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);

    private static System.Drawing.Font BoldMenuFont() =>
        new(Forms.SystemInformation.MenuFont, System.Drawing.FontStyle.Bold);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
