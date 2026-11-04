using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TheCube.Core;

namespace TheCube.App;

/// <summary>Settings dialog. Edits a copy; nothing changes until Save.</summary>
internal sealed class SettingsWindow : Window
{
    private readonly App _app;
    private CubeSettings _edit;
    private readonly StackPanel _root = new() { Margin = new Thickness(18) };

    public SettingsWindow(App app)
    {
        _app = app;
        _edit = app.Settings.Clone();
        Title = "The Cube — Settings";
        Icon = AppIcon.ForWindow();
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height * 0.9 };
        Build();
    }

    private void Build()
    {
        _root.Children.Clear();

        Heading("General");
        _root.Children.Add(Check("Start with Windows", _edit.StartWithWindows, v => _edit.StartWithWindows = v));
        _root.Children.Add(Row("Keep at least this many virtual desktops", NumberBox(_edit.EnsureDesktopCount, 0, 20, v => _edit.EnsureDesktopCount = v),
            "The Cube adds desktops when it starts until there are this many. 0 = never add any."));

        Heading("Shortcuts");
        _root.Children.Add(Row("Open / close the cube", HotkeyBox(_edit.ToggleCubeHotkey, v => _edit.ToggleCubeHotkey = v)));
        var rows = Math.Max(4, Math.Max(_edit.SwitchToDesktopHotkeys.Count, _edit.EnsureDesktopCount));
        while (_edit.SwitchToDesktopHotkeys.Count < rows)
        {
            _edit.SwitchToDesktopHotkeys.Add("");
        }

        for (var i = 0; i < rows; i++)
        {
            var index = i;
            _root.Children.Add(Row($"Switch to desktop {i + 1}", HotkeyBox(_edit.SwitchToDesktopHotkeys[i], v => _edit.SwitchToDesktopHotkeys[index] = v)));
        }

        _root.Children.Add(Note("Click a box and press the keys. Backspace clears it. Note that Ctrl+F4 normally closes tabs and documents in many apps."));

        Heading("Cube");
        var colorBox = new TextBox { Text = _edit.BackgroundColor, Width = 90 };
        var swatch = new Border { Width = 22, Height = 22, Margin = new Thickness(6, 0, 0, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
        void UpdateSwatch()
        {
            if (CubeSettings.TryParseColor(colorBox.Text.Trim(), out var c))
            {
                swatch.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
                _edit.BackgroundColor = colorBox.Text.Trim();
            }
        }

        colorBox.TextChanged += (_, _) => UpdateSwatch();
        UpdateSwatch();

        var skyPath = new TextBox { Text = _edit.SkyBox ?? "", Width = 250 };
        skyPath.TextChanged += (_, _) => _edit.SkyBox = string.IsNullOrWhiteSpace(skyPath.Text) ? null : skyPath.Text.Trim();
        var browse = new Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1) };
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All files|*.*" };
            if (dialog.ShowDialog(this) == true)
            {
                skyPath.Text = dialog.FileName;
            }
        };

        var colorRadio = new RadioButton { Content = "Colour", GroupName = "bg", IsChecked = _edit.Background == CubeBackground.Color, VerticalAlignment = VerticalAlignment.Center, Width = 110 };
        var skyRadio = new RadioButton { Content = "Skybox image", GroupName = "bg", IsChecked = _edit.Background == CubeBackground.SkyBox, VerticalAlignment = VerticalAlignment.Center, Width = 110 };
        colorRadio.Checked += (_, _) => _edit.Background = CubeBackground.Color;
        skyRadio.Checked += (_, _) => _edit.Background = CubeBackground.SkyBox;
        _root.Children.Add(Line(colorRadio, colorBox, swatch));
        _root.Children.Add(Line(skyRadio, skyPath, browse));
        _root.Children.Add(Note("A skybox should be a 360° panorama (equirectangular, about 2:1)."));

        _root.Children.Add(SliderRow("Face displacement", 0, 1000, 10, _edit.FaceDisplacement, v => _edit.FaceDisplacement = v, "0"));
        _root.Children.Add(SliderRow("Distance factor", 1, 5, 0.05, _edit.DistanceFactor, v => _edit.DistanceFactor = v, "0.00"));
        _root.Children.Add(SliderRow("Animation length (ms)", 0, 2000, 10, _edit.AnimationDurationMs, v => _edit.AnimationDurationMs = (int)Math.Round(v), "0"));
        _root.Children.Add(Check("Invert horizontal mouse dragging", _edit.MouseInvertedX, v => _edit.MouseInvertedX = v));
        _root.Children.Add(Check("Invert vertical mouse dragging", _edit.MouseInvertedY, v => _edit.MouseInvertedY = v));

        Heading("Screen edges");
        _root.Children.Add(Note("Open the cube by resting the pointer on these edges or corners:"));
        _root.Children.Add(EdgeGrid());

        var buttons = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
        var defaults = new Button { Content = "Restore defaults", Padding = new Thickness(10, 3, 10, 3) };
        defaults.Click += (_, _) =>
        {
            _edit = new CubeSettings();
            Build();
        };
        var save = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(18, 3, 18, 3), Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(defaults, Dock.Left);
        DockPanel.SetDock(save, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(defaults);
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        _root.Children.Add(buttons);
    }

    private void Save()
    {
        // Drop trailing unbound desktop hotkeys so the file stays tidy.
        while (_edit.SwitchToDesktopHotkeys.Count > 0 && string.IsNullOrWhiteSpace(_edit.SwitchToDesktopHotkeys[^1]))
        {
            _edit.SwitchToDesktopHotkeys.RemoveAt(_edit.SwitchToDesktopHotkeys.Count - 1);
        }

        var all = new[] { _edit.ToggleCubeHotkey }.Concat(_edit.SwitchToDesktopHotkeys).Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
        var duplicate = all.GroupBy(h => h, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            MessageBox.Show(this, $"{duplicate.Key} is used for more than one action.", "The Cube", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_edit.Background == CubeBackground.SkyBox && (_edit.SkyBox is null || !System.IO.File.Exists(_edit.SkyBox)))
        {
            MessageBox.Show(this, "Choose a skybox image that exists, or use a colour background.", "The Cube", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _edit.Normalize();
        if (_app.ApplySettings(_edit) is { } error)
        {
            MessageBox.Show(this, error, "The Cube", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Close();
    }

    private UIElement EdgeGrid()
    {
        var grid = new Grid { Width = 260, Height = 150, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        for (var i = 0; i < 3; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
        }

        grid.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x21, 0x24, 0x27)),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(18),
        }.Also(b => { Grid.SetRowSpan(b, 3); Grid.SetColumnSpan(b, 3); }));

        void Add(ScreenEdge edge, int row, int col, HorizontalAlignment h, VerticalAlignment v)
        {
            var box = new CheckBox { IsChecked = _edit.BorderActivate.Contains(edge), HorizontalAlignment = h, VerticalAlignment = v, ToolTip = edge.ToString() };
            box.Checked += (_, _) => { if (!_edit.BorderActivate.Contains(edge)) _edit.BorderActivate.Add(edge); };
            box.Unchecked += (_, _) => _edit.BorderActivate.Remove(edge);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, col);
            grid.Children.Add(box);
        }

        Add(ScreenEdge.TopLeft, 0, 0, HorizontalAlignment.Left, VerticalAlignment.Top);
        Add(ScreenEdge.Top, 0, 1, HorizontalAlignment.Center, VerticalAlignment.Top);
        Add(ScreenEdge.TopRight, 0, 2, HorizontalAlignment.Right, VerticalAlignment.Top);
        Add(ScreenEdge.Left, 1, 0, HorizontalAlignment.Left, VerticalAlignment.Center);
        Add(ScreenEdge.Right, 1, 2, HorizontalAlignment.Right, VerticalAlignment.Center);
        Add(ScreenEdge.BottomLeft, 2, 0, HorizontalAlignment.Left, VerticalAlignment.Bottom);
        Add(ScreenEdge.Bottom, 2, 1, HorizontalAlignment.Center, VerticalAlignment.Bottom);
        Add(ScreenEdge.BottomRight, 2, 2, HorizontalAlignment.Right, VerticalAlignment.Bottom);
        return grid;
    }

    /// <summary>A read-only box that records the next key combination pressed in it.</summary>
    private TextBox HotkeyBox(string value, Action<string> set)
    {
        var box = new TextBox { Text = value, Width = 170, IsReadOnly = true, IsReadOnlyCaretVisible = false, Cursor = Cursors.Hand };
        box.GotKeyboardFocus += (_, _) => _app.SuspendHotkeys(true);
        box.LostKeyboardFocus += (_, _) => _app.SuspendHotkeys(false);
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                return; // wait for the actual key
            }

            if (Keyboard.Modifiers == ModifierKeys.None && key is Key.Back or Key.Delete)
            {
                box.Text = "";
                set("");
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.None && key is Key.Tab)
            {
                e.Handled = false; // let Tab move focus
                return;
            }

            var mods = HotkeyModifiers.None;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Ctrl;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;

            var text = new Hotkey(mods, (uint)KeyInterop.VirtualKeyFromKey(key)).ToString();
            if (!Hotkey.TryParse(text, out var parsed) || parsed.ToString() != text)
            {
                return; // a key The Cube can't name (e.g. media keys)
            }

            box.Text = text;
            set(text);
        };
        return box;
    }

    private void Heading(string text) =>
        _root.Children.Add(new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, _root.Children.Count == 0 ? 0 : 16, 0, 6) });

    private static TextBlock Note(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 4) };

    private static CheckBox Check(string text, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 3, 0, 3) };
        box.Checked += (_, _) => set(true);
        box.Unchecked += (_, _) => set(false);
        return box;
    }

    private static UIElement Row(string label, UIElement control, string? note = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
        var line = new DockPanel();
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Width = 260 };
        line.Children.Add(text);
        line.Children.Add(control);
        panel.Children.Add(line);
        if (note is not null)
        {
            panel.Children.Add(Note(note));
        }

        return panel;
    }

    private static StackPanel Line(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        foreach (var c in children)
        {
            panel.Children.Add(c);
        }

        return panel;
    }

    private static TextBox NumberBox(int value, int min, int max, Action<int> set)
    {
        var box = new TextBox { Text = value.ToString(), Width = 60 };
        box.TextChanged += (_, _) =>
        {
            if (int.TryParse(box.Text, out var v))
            {
                set(Math.Clamp(v, min, max));
            }
        };
        return box;
    }

    private static UIElement SliderRow(string label, double min, double max, double step, double value, Action<double> set, string format)
    {
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, SmallChange = step, LargeChange = step * 10, TickFrequency = step, IsSnapToTickEnabled = true, Width = 180 };
        var shown = new TextBlock { Text = value.ToString(format), Width = 50, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) =>
        {
            shown.Text = e.NewValue.ToString(format);
            set(e.NewValue);
        };
        return Row(label, Line(slider, shown));
    }
}

internal static class FluentExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
