using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TheCube.App.Interop;

namespace TheCube.App;

/// <summary>
/// Temporary test window: shows the picture The Cube would put on each face (one row per monitor,
/// one column per desktop), with how each was made. Lets phase 3 be checked before the cube exists.
/// </summary>
internal sealed class DesktopPreviewWindow : Window
{
    private const double ThumbWidth = 320;

    private readonly DesktopImageProvider _images;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };

    public DesktopPreviewWindow(DesktopImageProvider images)
    {
        _images = images;
        Title = "The Cube — desktop images";
        Icon = AppIcon.ForWindow();
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x21, 0x24, 0x27));
        Foreground = Brushes.WhiteSmoke;

        var refresh = new Button { Content = "Capture again", Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left };
        refresh.Click += async (_, _) => await CaptureAsync();

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(refresh);
        root.Children.Add(new ScrollViewer
        {
            Content = _rows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = SystemParameters.WorkArea.Height * 0.8,
            MaxWidth = SystemParameters.WorkArea.Width * 0.9,
        });
        root.Children.Add(_status);
        Content = root;
        Loaded += async (_, _) => await CaptureAsync();
    }

    private async Task CaptureAsync()
    {
        _status.Text = "Capturing…";
        var monitors = Monitors.All();

        // Like the cube, grab the screens before this window can get in the way.
        Hide();
        await Task.Delay(150); // let DWM remove this window from the screen
        var screens = DesktopImageProvider.CaptureScreens(monitors);
        Show();

        var scenes = await _images.BuildAsync(monitors, screens);
        var renderClock = System.Diagnostics.Stopwatch.StartNew();

        _rows.Children.Clear();
        foreach (var group in scenes.GroupBy(s => s.Monitor))
        {
            var monitor = group.Key;
            _rows.Children.Add(new TextBlock
            {
                Text = $"{monitor.DeviceName}  {monitor.Bounds.Width}×{monitor.Bounds.Height} at ({monitor.Bounds.X}, {monitor.Bounds.Y}){(monitor.IsPrimary ? "  · primary" : "")}",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 4),
            });

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var scene in group.OrderBy(s => s.Desktop))
            {
                var scale = ThumbWidth / monitor.Bounds.Width;
                BitmapSource face = FaceRenderer.Render(scene, scale);
                var live = scene.Windows.Count(w => w.Source == LayerSource.Live);
                var cached = scene.Windows.Count - live;
                var caption = scene.IsCurrent
                    ? $"Desktop {scene.Desktop + 1} · current (screen grab)"
                    : $"Desktop {scene.Desktop + 1} · {live} live, {cached} cached, {scene.MissingWindows} missing" +
                      (scene.Wallpaper is null ? " · no wallpaper" : "");

                var cell = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
                cell.Children.Add(new Border
                {
                    BorderBrush = scene.IsCurrent ? Brushes.DeepSkyBlue : Brushes.Gray,
                    BorderThickness = new Thickness(2),
                    Child = new Image { Source = face, Width = ThumbWidth },
                });
                cell.Children.Add(new TextBlock { Text = caption, Width = ThumbWidth, TextWrapping = TextWrapping.Wrap, FontSize = 11 });
                row.Children.Add(cell);
            }

            _rows.Children.Add(row);
        }

        _status.Text = $"Built in {_images.LastBuildTime.TotalMilliseconds:0} ms, thumbnails drawn in {renderClock.ElapsedMilliseconds} ms. " +
                       "\"Missing\" windows couldn't be captured and have never been seen before; they'll appear once captured.";
    }
}
