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

/// <summary>What happened when a cube window was uncloaked, for the log.</summary>
/// <param name="ByFallback">The 300 ms fallback did it because no frame was drawn in time.</param>
/// <param name="Reused">The window was kept from an earlier open (or prepared at startup), not created now.</param>
/// <param name="UncloakResult">The HRESULT of removing The Cube's own cloak: 0 is success.</param>
/// <param name="StillCloaked">
/// DWM's cloak flags after that: 0 means visible. Non-zero (e.g. 2, cloaked by the shell) means Windows still hides
/// it, usually because it's on another virtual desktop.
/// </param>
internal readonly record struct RevealInfo(bool ByFallback, bool Reused, int UncloakResult, int StillCloaked);

/// <summary>
/// Full-screen, top-most window on one monitor that draws the cube (one face per desktop) and turns
/// mouse and keyboard input into <see cref="CubeController"/> calls. One per monitor, like KWin's
/// per-screen cube views. The window outlives an open: between opens it's kept hidden (see <see cref="Recycle"/>)
/// and the next open on the same monitor uses it again.
/// </summary>
/// <remarks>
/// Its life: New (constructed, or prepared at startup by <see cref="PrepareAsync"/>) or Kept (hidden, cloaked, no
/// scene) → <see cref="Start"/> → Running (revealed once a frame of the new scene is drawn) → <see cref="Recycle"/>
/// back to Kept, or Close → Closed. Start, Recycle and PrepareAsync check this and throw if misused.
/// </remarks>
internal sealed class CubeOverlay : Window
{
    private readonly Func<double> _clock;
    private readonly Grid _root = new();
    private readonly Viewport3D _view = new() { ClipToBounds = true, IsHitTestVisible = true };
    private readonly PerspectiveCamera _camera = new() { NearPlaneDistance = 10, FarPlaneDistance = 100000 };
    private readonly Model3DGroup _faceGroup = new();
    private readonly Dictionary<GeometryModel3D, int> _faceIndex = [];
    private readonly Rectangle _background = new();
    private readonly RevealGate _gate = new();
    private ImageBrush? _skyBox;
    private GeometryModel3D[] _faces = [];
    private double _lastTick = double.NaN;
    private int _openId; // bumped each Start, so a reveal queued in one open can't fire in the next
    private bool _reused;
    private bool _closed;
    private bool _uncloaked; // this open's window has really been uncloaked: input is taken only then

    private UIElement? _placeholder;
    private System.Windows.Threading.DispatcherTimer? _revealFallback;

    private bool _pressed;
    private bool _dragging;
    private Point _pressPos;

    public CubeOverlay(MonitorInfo monitor, CubeSettings settings, Func<double> clock, int displayLayout)
    {
        Monitor = monitor;
        Settings = settings;
        DisplayLayout = displayLayout;
        _clock = clock;
        Controller = NewController();

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
            // invisible until WPF has drawn the first frame (the screen image itself), so the cube
            // appears without a flash.
            var hwnd = new WindowInteropHelper(this).Handle;
            SetDwmFlag(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, true);
            CloakResult = SetDwmFlag(hwnd, DWMWA_CLOAK, true);
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(PlaceOnMonitor);

        // WPF raises ContentRendered once per window (Content is never replaced): for a window created for this
        // open it's the first frame. A prepared or kept window drew its first frame earlier; OnRendering reveals it.
        ContentRendered += (_, _) =>
        {
            if (_gate.OnContentRendered())
            {
                QueueReveal();
            }
        };

        // The fallback counts from when the window is shown: on a slow first open, Show itself can take a while.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && _gate.Running && !_gate.Revealed)
            {
                _revealFallback?.Start();
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            Stop();
        };
    }

    public MonitorInfo Monitor { get; }

    /// <summary>The settings this window was made with (its background and the controllers' settings).</summary>
    public CubeSettings Settings { get; }

    /// <summary>Which display layout (screens, resolution) this window was made for; see CubeSession.</summary>
    public int DisplayLayout { get; }

    /// <summary>The current (or, between opens, an idle) controller. Each <see cref="Start"/> makes a new one.</summary>
    public CubeController Controller { get; private set; }

    public bool IsClosed => _closed;

    /// <summary>The controller is closing onto a desktop. Cleared by <see cref="Recycle"/>, like every event here.</summary>
    public event Action<int>? ClosingOnto;

    /// <summary>The user picked a desktop to switch to.</summary>
    public event Action<int>? SwitchRequested;

    /// <summary>The close animation finished.</summary>
    public event Action? Hidden;

    /// <summary>The placeholder's "Add Virtual Desktop" button was clicked.</summary>
    public event Action? AddDesktopRequested;

    /// <summary>The window was uncloaked.</summary>
    public event Action<RevealInfo>? Revealed;

    /// <summary>What hiding (cloaking) the window until its first frame returned: 0 is success.</summary>
    public int CloakResult { get; private set; }

    /// <summary>
    /// Open the cube (or the placeholder) zoomed out from <paramref name="current"/>. Call before Show, once per
    /// open, on a new or kept window; <paramref name="reused"/> says which, for the log.
    /// </summary>
    public void Start(int current, int desktopCount, Brush currentFace, bool reused)
    {
        if (_closed || _gate.Running)
        {
            throw new InvalidOperationException(_closed ? "Start on a closed cube window" : "Start on a cube window that's already open");
        }

        ClearScene();
        Controller = NewController();
        _reused = reused;
        _openId++;

        // Hold the camera on the screen image until the window is actually visible (see Reveal).
        Controller.Activate(current, desktopCount, Monitor.Bounds.Width, Monitor.Bounds.Height, _clock(), startAnimation: false);
        _gate.Start();
        _uncloaked = false;

        // In case no frame is reported, the fallback reveals the window anyway rather than leave an invisible window
        // covering the screen. Started when the window becomes visible (see IsVisibleChanged).
        _revealFallback?.Stop();
        _revealFallback = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _revealFallback.Tick += (_, _) =>
        {
            if (_gate.OnFallback())
            {
                Reveal(byFallback: true);
            }
        };
        if (IsVisible)
        {
            _revealFallback.Start();
        }

        if (Controller.Geometry is { } geometry)
        {
            BuildFaces(geometry);
            SetFace(current, currentFace);
        }
        else
        {
            ShowPlaceholder(desktopCount);
        }

        _lastTick = double.NaN;
        CompositionTarget.Rendering += OnRendering;
        UpdateCamera();
    }

    /// <summary>
    /// End this open but keep the window, hidden and cloaked, for the next open on the same monitor: creating and
    /// showing a new full-screen 3D window is most of an open's time. Drops this open's scene, controller and every
    /// event handler. Throws if the window is closed or can't be cloaked again (then it must be closed instead: it
    /// would show this open's last frame when next shown).
    /// </summary>
    public void Recycle()
    {
        if (_closed)
        {
            throw new InvalidOperationException("Recycle on a closed cube window");
        }

        Stop();
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        _pressed = _dragging = false;
        BecomeKept();
    }

    /// <summary>Cloak, hide and empty the window, with no handlers and an idle controller: the Kept state.</summary>
    private void BecomeKept()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0)
        {
            throw new InvalidOperationException("the cube window has no handle");
        }

        var hr = SetDwmFlag(hwnd, DWMWA_CLOAK, true); // stays cloaked until the next open's Reveal
        if (hr != 0)
        {
            throw new COMException("couldn't cloak the cube window again", hr);
        }

        Hide();
        ClearScene();
        ClosingOnto = null;
        SwitchRequested = null;
        Hidden = null;
        AddDesktopRequested = null;
        Revealed = null;
        Controller = NewController();
    }

    /// <summary>
    /// Create the window for <paramref name="monitor"/> ahead of time and draw a cube in it a few times while it
    /// stays cloaked (invisible), takes no input and never takes focus, then keep it hidden for the first open, which
    /// then skips creating the window and most one-time setup (code loading, WPF's 3D pipeline, the graphics
    /// driver's caches). Gives up after 3 s if nothing is drawn. Returns the window (null if it couldn't be prepared;
    /// it's closed then) and what happened, for the log.
    /// </summary>
    public static async Task<(CubeOverlay? Kept, string Status)> PrepareAsync(MonitorInfo monitor, CubeSettings settings, Func<double> clock, int displayLayout, Brush face)
    {
        var size = $"{monitor.Bounds.Width}x{monitor.Bounds.Height}";
        var overlay = new CubeOverlay(monitor, settings, clock, displayLayout) { ShowActivated = false, IsEnabled = false, IsHitTestVisible = false };
        using var timeout = new CancellationTokenSource();
        var drawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contentRendered = false;
        var frames = 0;
        var lastFrame = TimeSpan.MinValue;
        void Tick(object? sender, EventArgs e)
        {
            // Three distinct UI frames after the first, giving the render thread time to draw. These are UI-thread
            // events, so "ok" means WPF drew 3 frames, not that the GPU is proven warm: the first open's log shows that.
            if (contentRendered && e is RenderingEventArgs r && r.RenderingTime != lastFrame)
            {
                lastFrame = r.RenderingTime;
                if (++frames >= 3)
                {
                    drawn.TrySetResult();
                }
            }
        }

        void SetCamera(object? sender, RoutedEventArgs e) => overlay.UpdateCamera();
        overlay.ContentRendered += (_, _) => contentRendered = true;

        // Subscribed before Show and removed in finally, so it can't outlive the preparation (a Rendering handler
        // keeps WPF drawing every frame).
        CompositionTarget.Rendering += Tick;
        try
        {
            // Create the window (which cloaks it in SourceInitialized) without showing it, so a failed cloak is
            // caught before anything could appear on screen.
            var hwnd = new WindowInteropHelper(overlay).EnsureHandle();
            if (overlay.CloakResult != 0)
            {
                overlay.Close();
                return (null, $"{size} skipped: couldn't hide the window (DWM error 0x{overlay.CloakResult:X8})");
            }

            // While preparing: never activated and disabled at the Windows level (WPF's IsEnabled/IsHitTestVisible
            // alone don't stop Windows from activating a clicked window). Not made layered or click-through: that
            // would draw it a different way from the real cube. Being cloaked, it isn't clicked anyway, like windows
            // on other virtual desktops.
            var hadNoActivate = ((long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_NOACTIVATE) != 0;
            SetExStyleBit(hwnd, NativeMethods.WS_EX_NOACTIVATE, on: true);
            EnableWindow(hwnd, false);

            // No Rendering handler of the overlay's own: set the camera once layout has given the window a size.
            overlay.Loaded += SetCamera;
            overlay.Controller.Activate(0, CubeGeometry.MinimumFaces + 1, monitor.Bounds.Width, monitor.Bounds.Height, clock(), startAnimation: false);
            if (overlay.Controller.Geometry is { } geometry)
            {
                overlay.BuildFaces(geometry);
                overlay.SetFace(0, face);
            }

            overlay.Show();
            var first = await Task.WhenAny(drawn.Task, Task.Delay(3000, timeout.Token));
            if (first != drawn.Task)
            {
                overlay.Close();
                return (null, $"{size} not drawn within 3 s");
            }

            // Back to a normal window (only the bit set above is cleared; other style bits WPF changed meanwhile stay),
            // then hidden and emptied for the open that uses it.
            overlay.Loaded -= SetCamera;
            if (!hadNoActivate)
            {
                SetExStyleBit(hwnd, NativeMethods.WS_EX_NOACTIVATE, on: false);
            }

            EnableWindow(hwnd, true);
            if ((((long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_NOACTIVATE) != 0) != hadNoActivate || !IsWindowEnabled(hwnd))
            {
                overlay.Close();
                return (null, $"{size} not kept: couldn't make it a normal window again");
            }

            overlay.IsEnabled = true;
            overlay.IsHitTestVisible = true;
            overlay.ShowActivated = true;
            overlay.BecomeKept();
            return (overlay, $"{size} ok");
        }
        catch
        {
            overlay.Close(); // never leave a half-prepared window behind
            throw;
        }
        finally
        {
            CompositionTarget.Rendering -= Tick;
            timeout.Cancel();
        }
    }

    private CubeController NewController()
    {
        var controller = new CubeController(Settings);
        controller.Closing += desktop => ClosingOnto?.Invoke(desktop);
        controller.SwitchRequested += desktop => SwitchRequested?.Invoke(desktop);
        controller.Hidden += () => Hidden?.Invoke();
        return controller;
    }

    /// <summary>Reveal once the dispatcher has caught up (the frame has gone to the render thread), unless a newer open started.</summary>
    private void QueueReveal()
    {
        var openId = _openId;
        Dispatcher.BeginInvoke(() =>
        {
            if (openId == _openId)
            {
                Reveal(byFallback: false);
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Uncloak the window (this open's first frame is drawn) and start zooming out. The gate already agreed.</summary>
    private void Reveal(bool byFallback)
    {
        _revealFallback?.Stop();
        if (!_gate.Running || _closed) // closed or kept for later before the queued reveal ran
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        var uncloak = hwnd != 0 ? SetDwmFlag(hwnd, DWMWA_CLOAK, false) : -1;
        var cloaked = 0;
        if (hwnd != 0)
        {
            cloaked = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var flags, sizeof(int)) == 0 ? flags : -1; // -1: couldn't tell
        }

        // Input only once Windows really shows it (-1, "couldn't tell", counts as shown: nothing better is known).
        _uncloaked = uncloak == 0 && cloaked is 0 or -1;
        Controller.BeginOpenAnimation(_clock());
        Revealed?.Invoke(new RevealInfo(byFallback, _reused, uncloak, cloaked));
    }

    /// <summary>
    /// The window turned out to be visible after all (a recovery moved it to this desktop): take input from now on
    /// and start the zoom-out again, since the first one ran while it couldn't be seen.
    /// </summary>
    public void MarkVisible()
    {
        if (_gate.Running && !_closed)
        {
            _uncloaked = true;
            Controller.BeginOpenAnimation(_clock());
        }
    }

    /// <summary>A recovery found this window hidden by Windows: never keep it for another open.</summary>
    public bool DoNotKeep { get; set; }

    /// <summary>DWM's cloak flags now: 0 means the window is visible.</summary>
    public int CloakedFlags()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        return hwnd != 0 && DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 ? cloaked : -1;
    }

    /// <summary>Remove the previous open's faces and placeholder.</summary>
    private void ClearScene()
    {
        _faceGroup.Children.Clear();
        _faceIndex.Clear();
        _faces = [];
        if (_placeholder is not null)
        {
            _root.Children.Remove(_placeholder);
            _placeholder = null;
        }
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

    /// <summary>Stop animating (the open is over); nothing can reveal the window after this.</summary>
    public void Stop()
    {
        _revealFallback?.Stop();
        if (_gate.Running)
        {
            CompositionTarget.Rendering -= OnRendering;
            _gate.Stop();
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
            if (_uncloaked) // no input while still invisible
            {
                AddDesktopRequested?.Invoke();
            }
        };

        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 600 };
        panel.Children.Add(new TextBlock { Text = "▦", FontSize = 64, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = text, FontSize = 18, Foreground = Brushes.WhiteSmoke, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        panel.Children.Add(add);
        _root.Children.Add(panel);
        _placeholder = panel;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _clock();
        var dt = double.IsNaN(_lastTick) ? 0 : (now - _lastTick) / 1000.0;
        _lastTick = now;
        Controller.Tick(dt, now);
        UpdateCamera();

        // A prepared or kept window gets no ContentRendered: once it's visible (still cloaked) and two distinct frames
        // have gone by, the new scene has usually been drawn; the reveal is queued behind them like a new window's.
        if (e is RenderingEventArgs r && _gate.OnFrame(IsVisible, r.RenderingTime.TotalMilliseconds))
        {
            QueueReveal();
        }
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
        if (!Controller.Activated || !_uncloaked) // no input while still invisible
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
        if (_uncloaked && Controller.Activated && Controller.Geometry is not null)
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

        if (_uncloaked && key is { } k && Controller.KeyPressed(k, _clock()))
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
    private const int DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint hwnd);

    private static void SetExStyleBit(nint hwnd, long bit, bool on)
    {
        var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (nint)(on ? ex | bit : ex & ~bit));
    }

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
