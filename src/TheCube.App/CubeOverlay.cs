using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using TheCube.App.Interop;
using TheCube.Core;

namespace TheCube.App;

/// <summary>
/// Full-screen, top-most window on one monitor that draws the cube (one face per desktop) and turns
/// mouse and keyboard input into <see cref="CubeController"/> calls. One per monitor, like KWin's
/// per-screen cube views.
/// </summary>
internal sealed class CubeOverlay : Window
{
    private readonly CubeSettings _settings;
    private readonly Func<double> _clock;
    private readonly Grid _root = new();
    private readonly Viewport3D _view = new() { ClipToBounds = true, IsHitTestVisible = true };
    private readonly PerspectiveCamera _camera = new() { NearPlaneDistance = 10, FarPlaneDistance = 100000 };
    private readonly Model3DGroup _faceGroup = new();
    private readonly Dictionary<GeometryModel3D, int> _faceIndex = [];
    private readonly Rectangle _background = new();
    private ImageBrush? _skyBox;
    private GeometryModel3D[] _faces = [];
    private double _lastTick = double.NaN;
    private bool _running;

    private bool _revealed;
    private System.Windows.Threading.DispatcherTimer? _revealFallback;

    private bool _pressed;
    private bool _dragging;
    private Point _pressPos;

    public CubeOverlay(MonitorInfo monitor, CubeSettings settings, Func<double> clock)
    {
        Monitor = monitor;
        _settings = settings;
        _clock = clock;
        Controller = new CubeController(settings);

        Title = "The Cube";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        CubeSettings.TryParseColor(settings.BackgroundColor, out var rgb);
        var backgroundColor = Color.FromRgb(rgb.R, rgb.G, rgb.B);
        Background = new SolidColorBrush(backgroundColor);
        Cursor = Cursors.Arrow;
        Focusable = true;

        _background.Fill = Background;
        if (settings.Background == CubeBackground.SkyBox && LoadSkyBox(settings.SkyBox) is { } sky)
        {
            _skyBox = new ImageBrush(sky)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                Stretch = Stretch.Fill,
            };
            _background.Fill = _skyBox;
        }

        _view.Camera = _camera;
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Colors.White)); // faces are unlit, like KWin's NoLighting material
        scene.Children.Add(_faceGroup);
        _view.Children.Add(new ModelVisual3D { Content = scene });

        _root.Children.Add(_background);
        _root.Children.Add(_view);
        Content = _root;

        SourceInitialized += (_, _) =>
        {
            PlaceOnMonitor();

            // No Windows open/close animation (it faded the overlay in over the desktop), and stay
            // invisible until WPF has drawn the first frame — the screen image itself — so the cube
            // appears without a flash.
            var hwnd = new WindowInteropHelper(this).Handle;
            SetDwmFlag(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, true);
            CloakResult = SetDwmFlag(hwnd, DWMWA_CLOAK, true);
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(PlaceOnMonitor);
        Closed += (_, _) => Stop();
    }

    public MonitorInfo Monitor { get; }

    public CubeController Controller { get; }

    /// <summary>The placeholder's "Add Virtual Desktop" button was clicked.</summary>
    public event Action? AddDesktopRequested;

    /// <summary>
    /// The window was uncloaked; true if the 300 ms fallback did it because no frame was drawn in time (it may then
    /// show the background for a moment rather than the screen image).
    /// </summary>
    public event Action<bool>? Revealed;

    /// <summary>What hiding (cloaking) the window until its first frame returned: 0 is success.</summary>
    public int CloakResult { get; private set; }

    /// <summary>Open the cube (or the placeholder) zoomed out from <paramref name="current"/>.</summary>
    public void Start(int current, int desktopCount, Brush currentFace)
    {
        // Hold the camera on the screen image until the window is actually visible (see Reveal).
        Controller.Activate(current, desktopCount, Monitor.Bounds.Width, Monitor.Bounds.Height, _clock(), startAnimation: false);

        // Only a started overlay is ever revealed (a warm-up one never calls Start). In case ContentRendered
        // never comes, the fallback reveals it anyway rather than leave an invisible window covering the screen.
        System.Diagnostics.Debug.Assert(!_running && _revealFallback is null, "Start is called once per overlay");
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(() => Reveal(byFallback: false), System.Windows.Threading.DispatcherPriority.Background);
        _revealFallback = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _revealFallback.Tick += (_, _) => Reveal(byFallback: true);

        // Count the 300 ms from when the window exists and is laid out: on a slow first open, Show itself can take longer.
        Loaded += (_, _) => _revealFallback?.Start();
        if (Controller.Geometry is { } geometry)
        {
            BuildFaces(geometry);
            SetFace(current, currentFace);
        }
        else
        {
            ShowPlaceholder(desktopCount);
        }

        _running = true;
        _lastTick = double.NaN;
        CompositionTarget.Rendering += OnRendering;
        UpdateCamera();
    }

    /// <summary>
    /// Draw a cube on <paramref name="monitor"/> a few times in a window that stays cloaked (invisible), takes no
    /// input and never takes focus, then close it, so the first real open skips most of the one-time setup (code
    /// loading, WPF's 3D pipeline, the graphics driver's caches). Gives up after 3 s if nothing is drawn.
    /// Returns what happened, for the log.
    /// </summary>
    public static async Task<string> WarmUpAsync(MonitorInfo monitor, CubeSettings settings, Func<double> clock, Brush face)
    {
        var size = $"{monitor.Bounds.Width}x{monitor.Bounds.Height}";
        var overlay = new CubeOverlay(monitor, settings, clock) { ShowActivated = false, IsEnabled = false, IsHitTestVisible = false };
        using var timeout = new CancellationTokenSource();
        var drawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contentRendered = false;
        var ticks = 0;
        void Tick(object? sender, EventArgs e)
        {
            // A few UI frames after the first, giving the render thread time to draw. These are UI-thread events, so
            // "ok" means WPF drew 3 frames, not that the GPU is proven warm: the first open's log shows that.
            if (contentRendered && ++ticks >= 3)
            {
                drawn.TrySetResult();
            }
        }

        // Subscribed before Show and removed in finally, so it can't outlive the warm-up (a Rendering handler
        // keeps WPF drawing every frame).
        CompositionTarget.Rendering += Tick;
        try
        {
            // Create the window (which cloaks it in SourceInitialized) without showing it, so a failed cloak is
            // caught before anything could appear on screen.
            var hwnd = new WindowInteropHelper(overlay).EnsureHandle();
            if (overlay.CloakResult != 0)
            {
                return $"{size} skipped: couldn't hide the window (DWM error 0x{overlay.CloakResult:X8})";
            }

            // Never activated and disabled at the Windows level (WPF's IsEnabled/IsHitTestVisible alone don't stop
            // Windows from activating a clicked window). Not made layered or click-through: that would draw it a
            // different way from the real cube and warm up the wrong thing. Being cloaked, it isn't clicked anyway,
            // like windows on other virtual desktops.
            var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (nint)(ex | NativeMethods.WS_EX_NOACTIVATE));
            EnableWindow(hwnd, false);

            // No Rendering handler of the overlay's own: set the camera once layout has given the window a size.
            overlay.Loaded += (_, _) => overlay.UpdateCamera();
            overlay.ContentRendered += (_, _) => contentRendered = true;
            overlay.Controller.Activate(0, CubeGeometry.MinimumFaces + 1, monitor.Bounds.Width, monitor.Bounds.Height, clock(), startAnimation: false);
            if (overlay.Controller.Geometry is { } geometry)
            {
                overlay.BuildFaces(geometry);
                overlay.SetFace(0, face);
            }

            overlay.Show();
            var first = await Task.WhenAny(drawn.Task, Task.Delay(3000, timeout.Token));
            return first == drawn.Task ? $"{size} ok" : $"{size} not drawn within 3 s";
        }
        finally
        {
            CompositionTarget.Rendering -= Tick;
            timeout.Cancel();
            overlay.Close();
        }
    }

    /// <summary>Uncloak the window (its first frame is drawn) and start zooming out. Only started overlays get here.</summary>
    private void Reveal(bool byFallback)
    {
        _revealFallback?.Stop();
        if (_revealed || !IsLoaded) // already shown, or closed before the queued reveal ran
        {
            return;
        }

        _revealed = true;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0)
        {
            SetDwmFlag(hwnd, DWMWA_CLOAK, false);
        }

        Controller.BeginOpenAnimation(_clock());
        Revealed?.Invoke(byFallback);
    }

    public void SetFace(int desktop, Brush brush)
    {
        if (desktop >= 0 && desktop < _faces.Length)
        {
            var material = new DiffuseMaterial(brush);
            material.Freeze();
            _faces[desktop].Material = material;
            _faces[desktop].BackMaterial = material; // KWin draws faces without culling
        }
    }

    public void Stop()
    {
        _revealFallback?.Stop();
        if (_running)
        {
            CompositionTarget.Rendering -= OnRendering;
            _running = false;
        }
    }

    private void BuildFaces(CubeGeometry geometry)
    {
        double w = geometry.FaceWidth, h = geometry.FaceHeight;
        var mesh = new MeshGeometry3D
        {
            Positions = [new(-w / 2, -h / 2, 0), new(w / 2, -h / 2, 0), new(w / 2, h / 2, 0), new(-w / 2, h / 2, 0)],
            TextureCoordinates = [new(0, 1), new(1, 1), new(1, 0), new(0, 0)],
            TriangleIndices = [0, 1, 2, 0, 2, 3],
        };
        mesh.Freeze();

        var placeholder = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x31, 0x36, 0x3b)));
        placeholder.Freeze();

        _faces = new GeometryModel3D[geometry.FaceCount];
        for (var i = 0; i < geometry.FaceCount; i++)
        {
            var transform = new Transform3DGroup();
            transform.Children.Add(new TranslateTransform3D(0, 0, geometry.FaceDistance));
            transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), geometry.FaceAzimuth(i))));
            transform.Freeze();
            var model = new GeometryModel3D(mesh, placeholder) { BackMaterial = placeholder, Transform = transform };
            _faces[i] = model;
            _faceIndex[model] = i;
            _faceGroup.Children.Add(model);
        }
    }

    private void ShowPlaceholder(int desktopCount)
    {
        var text = desktopCount == 1
            ? "At least 3 virtual desktops are required to display the Cube, but only 1 is present"
            : $"At least 3 virtual desktops are required to display the Cube, but only {desktopCount} are present";

        var add = new Button { Content = "Add Virtual Desktop", Padding = new Thickness(14, 6, 14, 6), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
        add.Click += (_, _) =>
        {
            if (_revealed) // no input while still invisible
            {
                AddDesktopRequested?.Invoke();
            }
        };

        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 600 };
        panel.Children.Add(new TextBlock { Text = "▦", FontSize = 64, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = text, FontSize = 18, Foreground = Brushes.WhiteSmoke, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        panel.Children.Add(add);
        _root.Children.Add(panel);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _clock();
        var dt = double.IsNaN(_lastTick) ? 0 : (now - _lastTick) / 1000.0;
        _lastTick = now;
        Controller.Tick(dt, now);
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        if (Controller.Geometry is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var p = Controller.Camera.Position;
        _camera.Position = new Point3D(p.X, p.Y, p.Z);
        _camera.LookDirection = new Vector3D(-p.X, -p.Y, -p.Z);
        _camera.UpDirection = new Vector3D(0, 1, 0);

        // KWin's camera has a 60° vertical field of view; WPF's FieldOfView is horizontal.
        var aspect = ActualWidth / ActualHeight;
        var vertical = CubeCamera.VerticalFieldOfView * Math.PI / 180;
        var horizontal = 2 * Math.Atan(Math.Tan(vertical / 2) * aspect) * 180 / Math.PI;
        _camera.FieldOfView = horizontal;

        if (_skyBox is not null)
        {
            // Approximate a panorama skybox: show the slice of a 360°×180° image the camera is facing.
            var (elevation, azimuth) = Controller.Camera.Euler;
            var tileW = 360 / horizontal;
            var tileH = 180 / CubeCamera.VerticalFieldOfView;
            var u = ((azimuth + 180) / 360 % 1 + 1) % 1;
            var v = 0.5 - elevation / 180;
            _skyBox.Viewport = new Rect(0.5 - u * tileW, 0.5 - v * tileH, tileW, tileH);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!Controller.Activated || !_revealed) // no input while still invisible
        {
            return;
        }

        Activate();
        Focus();
        _pressed = true;
        _dragging = false;
        _pressPos = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed || Controller.Geometry is null)
        {
            return;
        }

        var pos = e.GetPosition(this);
        if (!_dragging
            && (Math.Abs(pos.X - _pressPos.X) >= SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(pos.Y - _pressPos.Y) >= SystemParameters.MinimumVerticalDragDistance))
        {
            _dragging = true;
            Controller.Camera.BeginDrag(new System.Numerics.Vector2((float)_pressPos.X, (float)_pressPos.Y));
        }

        if (_dragging)
        {
            Controller.Camera.DragTo(new System.Numerics.Vector2((float)pos.X, (float)pos.Y));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        ReleaseMouseCapture();
        if (_dragging)
        {
            _dragging = false;
            Controller.Camera.EndDrag();
            return;
        }

        Controller.Clicked(FaceAt(e.GetPosition(_view)), _clock());
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging)
        {
            Controller.Camera.EndDrag();
        }

        _pressed = _dragging = false;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_revealed && Controller.Activated && Controller.Geometry is not null)
        {
            Controller.Camera.Wheel(e.Delta, inverted: false, _clock());
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        CubeKey? key = e.Key switch
        {
            Key.Escape => CubeKey.Escape,
            Key.Left => CubeKey.Left,
            Key.Right => CubeKey.Right,
            Key.Enter => CubeKey.Enter,
            Key.Space => CubeKey.Space,
            _ => null,
        };

        if (_revealed && key is { } k && Controller.KeyPressed(k, _clock()))
        {
            e.Handled = true;
        }
    }

    private int? FaceAt(Point point)
    {
        int? hit = null;
        VisualTreeHelper.HitTest(_view, null, result =>
        {
            if (result is RayMeshGeometry3DHitTestResult ray && ray.ModelHit is GeometryModel3D model && _faceIndex.TryGetValue(model, out var index))
            {
                hit = index;
                return HitTestResultBehavior.Stop; // results come nearest first
            }

            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(point));
        return hit;
    }

    /// <summary>Cover the monitor exactly, in physical pixels (WPF positions are DPI-scaled).</summary>
    private void PlaceOnMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0)
        {
            return;
        }

        const uint SWP_NOACTIVATE = 0x0010;
        var b = Monitor.Bounds;
        SetWindowPos(hwnd, -1 /* HWND_TOPMOST */, b.X, b.Y, b.Width, b.Height, SWP_NOACTIVATE);
    }

    private static BitmapSource? LoadSkyBox(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(System.IO.Path.GetFullPath(path));
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or NotSupportedException or System.IO.FileFormatException or UriFormatException or COMException)
        {
            EventLog.Write($"skybox: couldn't load {System.IO.Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    private const int DWMWA_CLOAK = 13;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    /// <returns>The HRESULT: 0 on success.</returns>
    private static int SetDwmFlag(nint hwnd, int attribute, bool value)
    {
        var v = value ? 1 : 0;
        return DwmSetWindowAttribute(hwnd, attribute, ref v, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
