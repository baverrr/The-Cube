using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TheCube.App.Interop;
using TheCube.Core;

namespace TheCube.App;

/// <summary>
/// Temporary window for testing the virtual desktop layer on real Windows before the cube and tray
/// exist: shows the build, interface set, desktops and hotkey status, and can switch or add desktops.
/// </summary>
internal sealed class DiagnosticsWindow : Window
{
    private readonly App _app;
    private readonly StackPanel _desktopList = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private int _lastCurrent = -1;
    private int _lastCount = -1;

    public DiagnosticsWindow(App app)
    {
        _app = app;
        Title = "The Cube — diagnostics";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x21, 0x24, 0x27));
        Foreground = Brushes.WhiteSmoke;

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(Heading("The Cube"));

        var (build, revision) = NativeMethods.GetWindowsBuild();
        root.Children.Add(Line($"Windows build: {build}.{revision}"));
        root.Children.Add(Line($"Desktop interfaces: {app.Desktops?.BackendName ?? "not available"}"));
        root.Children.Add(Line($"Settings: {App.SettingsPath}"));

        if (app.StartupError is { } error)
        {
            root.Children.Add(Line(error, Brushes.OrangeRed));
        }

        if (app.DesktopsAdded > 0)
        {
            root.Children.Add(Line($"Added {app.DesktopsAdded} desktop(s) to reach {app.Settings.EnsureDesktopCount}."));
        }

        root.Children.Add(Heading("Hotkeys"));
        var hotkeys = new List<string> { app.Settings.ToggleCubeHotkey };
        hotkeys.AddRange(app.Settings.SwitchToDesktopHotkeys);
        root.Children.Add(Line(string.Join(", ", hotkeys.Where(h => !string.IsNullOrWhiteSpace(h)))));
        foreach (var problem in app.HotkeyProblems)
        {
            root.Children.Add(Line("Not registered: " + problem, Brushes.Orange));
        }

        root.Children.Add(Heading("Desktops"));
        root.Children.Add(_desktopList);

        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(Button("Add desktop", () => Run(() => _app.Desktops!.CreateDesktop(), "Added a desktop")));
        buttons.Children.Add(Button("Open settings file", () => Process.Start(new ProcessStartInfo(App.SettingsPath) { UseShellExecute = true })));
        root.Children.Add(buttons);

        root.Children.Add(_status);
        Content = root;

        _app.Status += message => _status.Text = message;
        _poll.Tick += (_, _) => Refresh();
        _poll.Start();
        Refresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        _poll.Stop();
        base.OnClosed(e);
    }

    private void Refresh()
    {
        if (_app.Desktops is not { } desktops)
        {
            return;
        }

        IReadOnlyList<DesktopInfo> list;
        int current;
        try
        {
            list = desktops.GetDesktops();
            current = desktops.GetCurrentIndex();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            _status.Text = "Couldn't read desktops: " + ex.Message;
            return;
        }

        // Only rebuild when something changed, so buttons don't flicker under the mouse.
        if (current == _lastCurrent && list.Count == _lastCount)
        {
            return;
        }

        _lastCurrent = current;
        _lastCount = list.Count;
        _desktopList.Children.Clear();
        foreach (var d in list)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var index = d.Index;
            var switchButton = Button("Switch", () => Run(() => desktops.SwitchTo(index, animate: true), $"Switched to desktop {index + 1}"));
            switchButton.IsEnabled = d.Index != current;
            DockPanel.SetDock(switchButton, Dock.Right);
            row.Children.Add(switchButton);
            var label = $"{(d.Index == current ? "▶ " : "   ")}Desktop {d.Index + 1}";
            if (d.WallpaperPath is { } wallpaper)
            {
                label += $"  ·  {System.IO.Path.GetFileName(wallpaper)}";
            }

            row.Children.Add(Line(label));
            _desktopList.Children.Add(row);
        }
    }

    private void Run(Action action, string done)
    {
        try
        {
            action();
            _status.Text = done;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            _status.Text = "Failed: " + ex.Message;
        }

        _lastCurrent = -1;
        Refresh();
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };

    private static TextBlock Line(string text, Brush? brush = null)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        if (brush is not null)
        {
            block.Foreground = brush;
        }

        return block;
    }

    private static Button Button(string text, Action onClick)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        button.Click += (_, _) => onClick();
        return button;
    }
}
