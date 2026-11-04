using System.Numerics;
using TheCube.Core;

namespace TheCube.Core.Tests;

public class QuaternionMathTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-20, 90)]
    [InlineData(30, -135)]
    [InlineData(-30, 180)]
    public void EulerRoundTrips(double elevation, double azimuth)
    {
        var (e, a) = QuaternionMath.ToEulerDegrees(QuaternionMath.FromEulerDegrees(elevation, azimuth));
        Assert.Equal(elevation, e, 3);
        Assert.Equal(0, AngleDiff(azimuth, a), 3);
    }

    [Fact]
    public void PreMultipliedYawAddsToAzimuthAndKeepsElevation()
    {
        var q = QuaternionMath.FromEulerDegrees(0, 45) * QuaternionMath.FromEulerDegrees(-20, 30);
        var (e, a) = QuaternionMath.ToEulerDegrees(q);
        Assert.Equal(-20, e, 3);
        Assert.Equal(75, a, 3);
    }

    internal static double AngleDiff(double a, double b) => ((a - b) % 360 + 540) % 360 - 180;
}

public class CubeGeometryTests
{
    private static CubeGeometry Four() => new(4, 1920, 1080, 100);

    [Fact]
    public void FourFacesAreNinetyDegreesApart()
    {
        var cube = Four();
        Assert.Equal(90, cube.AngleTick);
        Assert.Equal(1920 / 2.0 + 100, cube.FaceDistance, 6); // tan(45°) = 1
    }

    [Fact]
    public void FacesSitAroundTheYAxis()
    {
        var cube = Four();
        var (x0, _, z0) = cube.FaceCenter(0);
        var (x1, _, z1) = cube.FaceCenter(1);
        Assert.Equal(0, x0, 6);
        Assert.Equal(cube.FaceDistance, z0, 6);
        Assert.Equal(cube.FaceDistance, x1, 6);
        Assert.Equal(0, z1, 6);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(44, 0)]
    [InlineData(45, 1)] // rounds half up
    [InlineData(90, 1)]
    [InlineData(180, 2)]
    [InlineData(-90, 3)]
    [InlineData(-46, 3)]
    [InlineData(-45, 0)]
    [InlineData(719, 0)]
    public void FaceAtPicksNearestFace(double azimuth, int expected) => Assert.Equal(expected, Four().FaceAt(azimuth));

    [Theory]
    [InlineData(90, 0)] // on a face: go one further
    [InlineData(60, 0)]
    [InlineData(2, -90)] // within 5% of a face counts as on it
    [InlineData(-10, -90)]
    public void LeftTarget(double from, double expected) => Assert.Equal(expected, Four().LeftTarget(from), 6);

    [Theory]
    [InlineData(0, 90)]
    [InlineData(60, 90)]
    [InlineData(88, 180)]
    public void RightTarget(double from, double expected) => Assert.Equal(expected, Four().RightTarget(from), 6);

    [Fact]
    public void ClosedCameraIsCloserThanOpenCamera()
    {
        var cube = Four();
        Assert.True(cube.CloseRadius(60) < cube.DistantRadius(60, 1.75));
        Assert.Equal(cube.FaceDistance + 540 / Math.Tan(Math.PI / 6), cube.CloseRadius(60), 6);
    }

    [Fact]
    public void FewerThanThreeFacesIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CubeGeometry(2, 100, 100, 0));
}

public class CubeCameraTests
{
    private static readonly CubeGeometry Cube = new(4, 1920, 1080, 100);

    [Fact]
    public void OpensTiltedAndFurtherOut()
    {
        var cam = new CubeCamera(500);
        cam.SnapClosed(Cube, 1);
        Assert.Equal(90, cam.Euler.Azimuth, 3);
        Assert.Equal(Cube.CloseRadius(60), cam.Radius, 3);

        cam.AnimateOpen(Cube, 1, 1.75, now: 0);
        cam.Tick(0.016, 250);
        Assert.True(cam.Animating);
        cam.Tick(0.016, 500);
        Assert.False(cam.Animating);
        Assert.Equal(-20, cam.Euler.Elevation, 2);
        Assert.Equal(90, cam.Euler.Azimuth, 2);
        Assert.Equal(Cube.DistantRadius(60, 1.75), cam.Radius, 2);
        Assert.True(cam.Position.Y > 0); // looking down from above
        Assert.True(cam.Position.X > 0); // in front of face 1 (on +X)
    }

    [Fact]
    public void ArrowRotationIsIgnoredWhileAnimating()
    {
        var cam = new CubeCamera(500);
        cam.SnapClosed(Cube, 0);
        cam.RotateRight(Cube, 0);
        cam.RotateRight(Cube, 100); // ignored
        cam.Tick(0.016, 600);
        Assert.Equal(90, cam.Euler.Azimuth, 2);
    }

    [Fact]
    public void ArrowRotationKeepsElevation()
    {
        var cam = new CubeCamera(0);
        cam.SnapClosed(Cube, 0);
        cam.AnimateOpen(Cube, 0, 1.75, 0);
        cam.RotateLeft(Cube, 0);
        Assert.Equal(-20, cam.Euler.Elevation, 2);
        Assert.Equal(0, QuaternionMathTests.AngleDiff(-90, cam.Euler.Azimuth), 2);
    }

    [Fact]
    public void DraggingRotatesAndClampsElevation()
    {
        var cam = new CubeCamera(500);
        cam.SnapClosed(Cube, 0);
        cam.BeginDrag(new Vector2(500, 500));
        cam.DragTo(new Vector2(400, 0)); // left by 100 px, up by 500 px
        cam.Tick(0.016, 16);
        var (e, a) = cam.Euler;
        Assert.Equal(100 * 10 * 0.016, a, 2);
        Assert.Equal(CubeCamera.MaxElevation, e, 2);
        cam.EndDrag();
        Assert.False(cam.Busy);
    }

    [Fact]
    public void InvertedXDragGoesTheOtherWay()
    {
        var cam = new CubeCamera(500) { InvertX = true };
        cam.SnapClosed(Cube, 0);
        cam.BeginDrag(new Vector2(500, 500));
        cam.DragTo(new Vector2(400, 500));
        cam.Tick(0.016, 16);
        Assert.Equal(-16, cam.Euler.Azimuth, 2);
    }

    [Fact]
    public void WheelUpZoomsOutByTwelvePercentPerNotch()
    {
        var cam = new CubeCamera(0);
        cam.SnapClosed(Cube, 0);
        var r = cam.Radius;
        cam.Wheel(120, inverted: false, now: 0);
        Assert.Equal(r * 1.12, cam.Radius, 3);
    }
}

public class CubeControllerTests
{
    private static (CubeController C, List<int> Switches, List<bool> Hidden) Make(int duration = 500)
    {
        var c = new CubeController(new CubeSettings { AnimationDurationMs = duration });
        var switches = new List<int>();
        var hidden = new List<bool>();
        c.SwitchRequested += switches.Add;
        c.Hidden += () => hidden.Add(true);
        return (c, switches, hidden);
    }

    [Fact]
    public void EnterSwitchesToTheFaceInFront()
    {
        var (c, switches, hidden) = Make();
        c.Activate(0, 4, 1920, 1080, 0);
        c.Tick(0.016, 600);
        c.KeyPressed(CubeKey.Right, 600);
        c.Tick(0.016, 1200);
        c.KeyPressed(CubeKey.Enter, 1200);
        Assert.Empty(switches); // switch waits for the zoom-in to finish
        Assert.False(c.Activated);
        Assert.True(c.Visible); // closing animation still running
        c.Tick(0.016, 1700);
        Assert.False(c.Visible);
        Assert.Equal([1], switches);
        Assert.Single(hidden);
    }

    [Fact]
    public void EscapeClosesWithoutSwitching()
    {
        var (c, switches, _) = Make();
        c.Activate(2, 4, 1920, 1080, 0);
        c.KeyPressed(CubeKey.Escape, 10);
        Assert.Empty(switches);
        Assert.False(c.Activated);
    }

    [Fact]
    public void ClickOnAFaceSwitchesToIt()
    {
        var (c, switches, _) = Make();
        c.Activate(0, 4, 1920, 1080, 0);
        c.Clicked(3, 100);
        Assert.Equal(3, c.CurrentDesktop); // camera zooms into face 3
        c.Tick(0.016, 700);
        Assert.Equal([3], switches);
    }

    [Fact]
    public void ClickOnEmptySpaceSwitchesToSelected()
    {
        var (c, switches, _) = Make();
        c.Activate(2, 4, 1920, 1080, 0);
        c.Tick(0.016, 600);
        c.Clicked(null, 600);
        c.Tick(0.016, 1200);
        Assert.Empty(switches); // face 2 is already the current desktop
        Assert.False(c.Visible);
    }

    [Fact]
    public void ClickingAwayFromCurrentFaceSwitchesAfterAnimation()
    {
        var (c, switches, _) = Make();
        c.Activate(2, 4, 1920, 1080, 0);
        c.Tick(0.016, 600);
        c.KeyPressed(CubeKey.Left, 600);
        c.Tick(0.016, 1200);
        c.Clicked(null, 1200);
        c.Tick(0.016, 1800);
        Assert.Equal([1], switches);
    }

    [Fact]
    public void EscapeAfterReopenDoesNotReplayOldSwitch()
    {
        var (c, switches, _) = Make(duration: 0);
        c.Activate(0, 4, 1920, 1080, 0);
        c.Clicked(2, 0);
        c.Tick(0.016, 1);
        c.Activate(2, 4, 1920, 1080, 2);
        c.KeyPressed(CubeKey.Escape, 3);
        c.Tick(0.016, 4);
        Assert.Equal([2], switches);
    }

    [Fact]
    public void CannotReopenWhileClosing()
    {
        var (c, _, _) = Make();
        c.Activate(0, 4, 1920, 1080, 0);
        c.Deactivate(10);
        c.Activate(0, 4, 1920, 1080, 20);
        Assert.False(c.Activated);
        c.Tick(0.016, 600);
        c.Activate(0, 4, 1920, 1080, 700);
        Assert.True(c.Activated);
    }

    [Fact]
    public void ClosingTellsWhichDesktopAndCloseToFollowsWithoutSwitching()
    {
        var (a, switchesA, _) = Make();
        var (b, switchesB, _) = Make();
        a.Closing += d => b.CloseTo(d, 100);
        b.Closing += d => a.CloseTo(d, 100);
        a.Activate(0, 4, 1920, 1080, 0);
        b.Activate(0, 4, 2560, 1440, 0);
        a.Clicked(2, 100);
        Assert.False(b.Activated);
        Assert.Equal(2, b.CurrentDesktop);
        a.Tick(0.016, 700);
        b.Tick(0.016, 700);
        Assert.Equal([2], switchesA);
        Assert.Empty(switchesB);
    }

    [Fact]
    public void HeldOpenStaysOnScreenImageUntilBegun()
    {
        var (c, _, _) = Make();
        c.Activate(1, 4, 1920, 1080, 0, startAnimation: false);
        var closeRadius = c.Geometry!.CloseRadius(CubeCamera.VerticalFieldOfView);
        c.Tick(0.016, 300);
        Assert.Equal(closeRadius, c.Camera.Radius, 3);
        c.BeginOpenAnimation(300);
        c.Tick(0.016, 800);
        Assert.Equal(c.Geometry.DistantRadius(CubeCamera.VerticalFieldOfView, 1.75), c.Camera.Radius, 2);
        Assert.Equal(-20, c.Camera.Euler.Elevation, 2);
    }

    [Fact]
    public void TwoDesktopsShowsPlaceholderAndClickCloses()
    {
        var (c, switches, _) = Make();
        c.Activate(0, 2, 1920, 1080, 0);
        Assert.True(c.ShowsPlaceholder);
        Assert.False(c.KeyPressed(CubeKey.Enter, 10));
        c.Clicked(null, 10);
        Assert.False(c.Activated);
        Assert.Empty(switches);
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Win+C", HotkeyModifiers.Win, 'C')]
    [InlineData("Meta+C", HotkeyModifiers.Win, 'C')]
    [InlineData("Ctrl+F1", HotkeyModifiers.Ctrl, 0x70)]
    [InlineData("ctrl + f4", HotkeyModifiers.Ctrl, 0x73)]
    [InlineData("Ctrl+Alt+Left", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x25)]
    public void Parses(string text, HotkeyModifiers mods, uint vk)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(new Hotkey(mods, vk), hk);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+F25")]
    [InlineData("Hyper+X")]
    public void RejectsInvalid(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void FormatsBack() => Assert.Equal("Ctrl+Win+F2", new Hotkey(HotkeyModifiers.Ctrl | HotkeyModifiers.Win, 0x71).ToString());
}

public class SettingsTests
{
    [Fact]
    public void DefaultsMirrorThePlasmaSetup()
    {
        var s = new CubeSettings();
        Assert.Equal(100, s.FaceDisplacement);
        Assert.Equal(1.75, s.DistanceFactor);
        Assert.Equal("#212427", s.BackgroundColor);
        Assert.Equal("Win+C", s.ToggleCubeHotkey);
        Assert.Equal(["Ctrl+F1", "Ctrl+F2", "Ctrl+F3", "Ctrl+F4"], s.SwitchToDesktopHotkeys);
        Assert.Equal(4, s.EnsureDesktopCount);
    }

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "sub", "settings.json");
            new CubeSettings { DistanceFactor = 2.5, Background = CubeBackground.SkyBox, BorderActivate = [ScreenEdge.TopLeft] }.Save(path);
            var loaded = CubeSettings.Load(path);
            Assert.Equal(2.5, loaded.DistanceFactor);
            Assert.Equal(CubeBackground.SkyBox, loaded.Background);
            Assert.Equal([ScreenEdge.TopLeft], loaded.BorderActivate);
            Assert.Contains("\"SkyBox\"", File.ReadAllText(path));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void NormalizeFixesBadValues()
    {
        var s = new CubeSettings { DistanceFactor = 0.2, BackgroundColor = "red", AnimationDurationMs = -5, EnsureDesktopCount = 99 };
        s.Normalize();
        Assert.Equal(1.75, s.DistanceFactor);
        Assert.Equal("#212427", s.BackgroundColor);
        Assert.Equal(0, s.AnimationDurationMs);
        Assert.Equal(20, s.EnsureDesktopCount);
    }

    [Fact]
    public void CloneIsDeep()
    {
        var a = new CubeSettings();
        var b = a.Clone();
        b.SwitchToDesktopHotkeys[0] = "Ctrl+F9";
        b.BorderActivate.Add(ScreenEdge.Top);
        Assert.Equal("Ctrl+F1", a.SwitchToDesktopHotkeys[0]);
        Assert.Empty(a.BorderActivate);
        Assert.Equal(a.DistanceFactor, b.DistanceFactor);
    }

    [Fact]
    public void MissingFileGivesDefaults() => Assert.Equal(1.75, CubeSettings.Load("/nonexistent/x.json").DistanceFactor);
}

public class DesktopTests
{
    private sealed class FakeDesktops(int count, int current = 0) : IVirtualDesktopService
    {
        public int Count = count;
        public int Current = current;
        public List<(int Index, bool Animate)> Switches = [];

        public string BackendName => "fake";
        public IReadOnlyList<DesktopInfo> GetDesktops() =>
            Enumerable.Range(0, Count).Select(i => new DesktopInfo(i, Guid.NewGuid(), null)).ToList();
        public int GetCurrentIndex() => Current;
        public void SwitchTo(int index, bool animate) { Switches.Add((index, animate)); Current = index; }
        public void CreateDesktop() => Count++;
        public int? GetWindowDesktopIndex(nint hwnd) => null;
    }

    [Fact]
    public void EnsureCountAddsMissingDesktops()
    {
        var d = new FakeDesktops(1);
        Assert.Equal(3, DesktopActions.EnsureCount(d, 4));
        Assert.Equal(4, d.Count);
        Assert.Equal(0, DesktopActions.EnsureCount(d, 4));
        Assert.Equal(0, DesktopActions.EnsureCount(d, 0));
    }

    [Fact]
    public void SwitchIgnoresMissingAndCurrentDesktops()
    {
        var d = new FakeDesktops(4, current: 1);
        Assert.False(DesktopActions.SwitchTo(d, 1, true));
        Assert.False(DesktopActions.SwitchTo(d, 4, true));
        Assert.False(DesktopActions.SwitchTo(d, -1, true));
        Assert.True(DesktopActions.SwitchTo(d, 3, true));
        Assert.Equal([(3, true)], d.Switches);
    }

    [Theory]
    [InlineData(19045, DesktopApiVersion.Windows10)]
    [InlineData(19044, DesktopApiVersion.Windows10)]
    [InlineData(19041, DesktopApiVersion.Unsupported)]
    [InlineData(22000, DesktopApiVersion.Unsupported)]
    [InlineData(22631, DesktopApiVersion.Windows11)]
    [InlineData(26100, DesktopApiVersion.Windows11_24H2)]
    [InlineData(26200, DesktopApiVersion.Windows11_24H2)]
    public void PicksInterfacesByBuild(int build, DesktopApiVersion expected) =>
        Assert.Equal(expected, DesktopApiSelector.ForBuild(build));
}

public class SceneTests
{
    private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);

    [Fact]
    public void FillCoversAndCentres()
    {
        var r = Assert.Single(WallpaperLayout.Place(WallpaperPosition.Fill, 1000, 1000, Monitor, Monitor));
        Assert.Equal(new PixelRect(0, -420, 1920, 1920), r);
    }

    [Fact]
    public void FitLetterboxes()
    {
        var r = Assert.Single(WallpaperLayout.Place(WallpaperPosition.Fit, 1000, 1000, Monitor, Monitor));
        Assert.Equal(new PixelRect(420, 0, 1080, 1080), r);
    }

    [Fact]
    public void CenterKeepsSize() =>
        Assert.Equal(new PixelRect(860, 440, 200, 200), Assert.Single(WallpaperLayout.Place(WallpaperPosition.Center, 200, 200, Monitor, Monitor)));

    [Fact]
    public void StretchFillsMonitor() =>
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), Assert.Single(WallpaperLayout.Place(WallpaperPosition.Stretch, 10, 10, Monitor, Monitor)));

    [Fact]
    public void TileCoversMonitor() =>
        Assert.Equal(10 * 6, WallpaperLayout.Place(WallpaperPosition.Tile, 200, 200, Monitor, Monitor).Count);

    [Fact]
    public void TinyTilesAreStretchedInsteadOfDrawnThousandsOfTimes() =>
        Assert.Single(WallpaperLayout.Place(WallpaperPosition.Tile, 8, 8, Monitor, Monitor));

    [Fact]
    public void SpanShowsThisMonitorsPart()
    {
        var right = new PixelRect(1920, 0, 1920, 1080);
        var virtualScreen = new PixelRect(0, 0, 3840, 1080);
        var r = Assert.Single(WallpaperLayout.Place(WallpaperPosition.Span, 3840, 1080, right, virtualScreen));
        Assert.Equal(new PixelRect(-1920, 0, 3840, 1080), r);
    }

    [Fact]
    public void WindowsForPicksDesktopMonitorAndBottomToTop()
    {
        var other = new PixelRect(1920, 0, 1920, 1080);
        var list = new List<WindowEntry>
        {
            new(1, new PixelRect(0, 1040, 1920, 40), null, AllDesktops: true, Minimized: false), // taskbar, top
            new(2, new PixelRect(100, 100, 800, 600), 1, false, false),
            new(3, new PixelRect(100, 100, 800, 600), 0, false, false),                // other desktop
            new(4, new PixelRect(2000, 100, 800, 600), 1, false, false),               // other monitor
            new(5, new PixelRect(50, 50, 800, 600), 1, false, Minimized: true),
            new(6, new PixelRect(1800, 0, 400, 400), 1, false, false),                 // straddles both
        };
        var result = SceneBuilder.WindowsFor(list, 1, Monitor).Select(w => w.Handle).ToList();
        Assert.Equal([6, 2, 1], result);
        Assert.Equal([6, 4], SceneBuilder.WindowsFor(list, 1, other).Select(w => w.Handle).ToList());
    }

    [Fact]
    public void BlankDetection()
    {
        var black = new byte[100 * 100 * 4];
        Assert.True(PixelChecks.LooksBlank(black, 100, 100, 400));
        var img = (byte[])black.Clone();
        for (var i = 0; i < img.Length; i += 4) { img[i] = 200; img[i + 3] = 255; }
        Assert.False(PixelChecks.LooksBlank(img, 100, 100, 400));
        Assert.True(PixelChecks.LooksBlank([], 0, 0, 0));
        var bgr = new byte[100 * 100 * 4];
        for (var i = 0; i < bgr.Length; i += 4) { bgr[i + 1] = 90; } // alpha byte stays 0 (padding)
        Assert.True(PixelChecks.LooksBlank(bgr, 100, 100, 400));
        Assert.False(PixelChecks.LooksBlank(bgr, 100, 100, 400, ignoreAlpha: true));
    }

    [Fact]
    public void CacheMatchesOnlySameSizeAndPrunes()
    {
        var cache = new WindowImageCache<string>();
        cache.Put(1, 800, 600, "a");
        cache.Put(2, 100, 100, "b");
        Assert.Equal("a", cache.Get(1, 800, 600));
        Assert.Null(cache.Get(1, 801, 600));
        cache.Prune([1]);
        Assert.Null(cache.Get(2, 100, 100));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void RectIntersect()
    {
        Assert.Equal(new PixelRect(1800, 0, 120, 400), new PixelRect(1800, 0, 400, 400).Intersect(Monitor));
        Assert.True(new PixelRect(1920, 0, 10, 10).Intersect(Monitor).IsEmpty);
    }
}

public class ScreenEdgeTests
{
    private static readonly PixelRect Left = new(0, 0, 1920, 1080);
    private static readonly PixelRect Right = new(1920, 0, 2560, 1440);

    private static bool OnAny(int x, int y) =>
        (x >= Left.X && x < Left.Right && y >= Left.Y && y < Left.Bottom) ||
        (x >= Right.X && x < Right.Right && y >= Right.Y && y < Right.Bottom);

    [Theory]
    [InlineData(0, 0, ScreenEdge.TopLeft)]
    [InlineData(5, 0, ScreenEdge.TopLeft)]
    [InlineData(0, 5, ScreenEdge.TopLeft)]
    [InlineData(500, 0, ScreenEdge.Top)]
    [InlineData(0, 500, ScreenEdge.Left)]
    [InlineData(0, 1079, ScreenEdge.BottomLeft)]
    [InlineData(500, 1079, ScreenEdge.Bottom)]
    [InlineData(500, 500, null)]
    [InlineData(1919, 500, null)] // shared with the right-hand monitor
    public void ClassifiesLeftMonitor(int x, int y, ScreenEdge? expected) =>
        Assert.Equal(expected, ScreenEdges.Classify(x, y, Left, OnAny));

    [Fact]
    public void RightMonitorRightEdgeAndLowerLeftEdge()
    {
        Assert.Equal(ScreenEdge.Right, ScreenEdges.Classify(4479, 700, Right, OnAny));
        Assert.Equal(ScreenEdge.TopRight, ScreenEdges.Classify(4479, 0, Right, OnAny));
        // Below the shorter left monitor, the right monitor's left edge is an outer edge.
        Assert.Equal(ScreenEdge.Left, ScreenEdges.Classify(1920, 1300, Right, OnAny));
        Assert.Null(ScreenEdges.Classify(1920, 500, Right, OnAny));
    }

    [Fact]
    public void DwellFiresOnceAfterDelay()
    {
        var t = new EdgeDwellTracker(150);
        Assert.Null(t.Update(ScreenEdge.TopLeft, 0));
        Assert.Null(t.Update(ScreenEdge.TopLeft, 100));
        Assert.Equal(ScreenEdge.TopLeft, t.Update(ScreenEdge.TopLeft, 160));
        Assert.Null(t.Update(ScreenEdge.TopLeft, 400));
        Assert.Null(t.Update(null, 410));
        Assert.Null(t.Update(ScreenEdge.TopLeft, 420));
        Assert.Equal(ScreenEdge.TopLeft, t.Update(ScreenEdge.TopLeft, 600));
    }

    [Fact]
    public void MovingToAnotherEdgeRestartsDwell()
    {
        var t = new EdgeDwellTracker(150);
        t.Update(ScreenEdge.Top, 0);
        Assert.Null(t.Update(ScreenEdge.TopLeft, 140));
        Assert.Null(t.Update(ScreenEdge.TopLeft, 200));
        Assert.Equal(ScreenEdge.TopLeft, t.Update(ScreenEdge.TopLeft, 300));
    }
}

public class PagerTests
{
    private static readonly PixelRect Taskbar = new(0, 1032, 1920, 48);

    [Fact]
    public void Sits_just_left_of_the_start_button()
    {
        var start = new PixelRect(800, 1036, 40, 40);
        var r = Pager.Place(Taskbar, start, null, 4, 16.0 / 9)!.Value;
        Assert.Equal(start.X - 12, r.Right); // gap = 0.25 × 48
        Assert.Equal(1032 + 8, r.Y);         // padding = 0.16 × 48 ≈ 8
        Assert.Equal(32, r.Height);
    }

    [Fact]
    public void Falls_back_to_the_left_of_the_tray_when_start_is_at_the_far_left()
    {
        var start = new PixelRect(10, 1036, 40, 40);
        var tray = new PixelRect(1700, 1032, 220, 48);
        var r = Pager.Place(Taskbar, start, tray, 4, 16.0 / 9)!.Value;
        Assert.Equal(1700 - 12, r.Right);
    }

    [Fact]
    public void Uses_the_right_end_without_a_tray()
    {
        var r = Pager.Place(Taskbar, null, null, 4, 16.0 / 9)!.Value;
        Assert.Equal(1920 - 12, r.Right);
    }

    [Fact]
    public void Not_on_a_vertical_taskbar()
    {
        Assert.Null(Pager.Place(new PixelRect(0, 0, 48, 1080), null, null, 4, 16.0 / 9));
        Assert.Null(Pager.Place(Taskbar, null, null, 0, 16.0 / 9));
    }

    [Fact]
    public void Boxes_fill_the_width_left_to_right()
    {
        var height = 32.0;
        var width = Pager.Width(4, height, 16.0 / 9);
        var boxes = Pager.Boxes(4, width, height);
        Assert.Equal(4, boxes.Count);
        Assert.Equal(0, boxes[0].X, 6);
        Assert.Equal(width, boxes[3].Right, 6);
        Assert.Equal(height * 16 / 9, boxes[0].Width, 6);
        Assert.True(boxes[1].X > boxes[0].Right);
    }

    [Fact]
    public void Finds_the_box_under_the_mouse()
    {
        var boxes = Pager.Boxes(2, 100, 20);
        Assert.Equal(0, Pager.BoxAt(boxes, 1, 1));
        Assert.Equal(1, Pager.BoxAt(boxes, 99, 19));
        Assert.Null(Pager.BoxAt(boxes, 50, 10)); // the gap
        Assert.Null(Pager.BoxAt(boxes, 50, 30));
    }

    [Fact]
    public void Window_outlines_scale_from_the_monitor()
    {
        var monitor = new PixelRect(1920, 0, 1920, 1080);
        var box = new BoxRect(10, 0, 192, 108);
        var mini = Pager.MiniWindow(new PixelRect(1920 + 960, 540, 960, 540), monitor, box)!.Value;
        Assert.Equal(10 + 96, mini.X, 6);
        Assert.Equal(54, mini.Y, 6);
        Assert.Equal(96, mini.Width, 6);
        Assert.Equal(54, mini.Height, 6);
        Assert.Null(Pager.MiniWindow(new PixelRect(0, 0, 100, 100), monitor, box));
    }

    [Theory]
    [InlineData(0, -120, 1)]
    [InlineData(3, -120, 0)]
    [InlineData(0, 120, 3)]
    [InlineData(2, 120, 1)]
    [InlineData(2, 0, 2)]
    public void Wheel_steps_and_wraps(int current, int delta, int expected) =>
        Assert.Equal(expected, Pager.Wheel(current, 4, delta));
}

public class ReviewFollowUpTests
{
    [Theory]
    [InlineData("#12345 ")]
    [InlineData("# 12345")]
    [InlineData("#12G456")]
    [InlineData("212427")]
    public void Colours_must_be_exactly_hash_and_six_hex_digits(string text) =>
        Assert.False(CubeSettings.TryParseColor(text, out _));

    [Fact]
    public void Colour_parses_to_its_components()
    {
        Assert.True(CubeSettings.TryParseColor("#212427", out var rgb));
        Assert.Equal(((byte)0x21, (byte)0x24, (byte)0x27), rgb);
    }

    [Theory]
    [InlineData(-5000, 0)]
    [InlineData(5000, 1000)]
    [InlineData(double.NaN, 100)]
    public void Face_displacement_is_kept_in_range(double value, double expected)
    {
        var s = new CubeSettings { FaceDisplacement = value };
        s.Normalize();
        Assert.Equal(expected, s.FaceDisplacement);
    }

    [Fact]
    public void Lowercase_hotkeys_map_to_letter_keys_not_numpad()
    {
        Assert.True(Hotkey.TryParse("ctrl+c", out var h));
        Assert.Equal(0x43u, (uint)h.VirtualKey);
    }

    [Fact]
    public void Malformed_settings_file_throws_json_exception()
    {
        var path = Path.Combine(Path.GetTempPath(), $"thecube-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            Assert.ThrowsAny<System.Text.Json.JsonException>(() => CubeSettings.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Pager_never_covers_the_tray()
    {
        var taskbar = new PixelRect(0, 1032, 1920, 48);
        var start = new PixelRect(1800, 1036, 40, 40);
        var tray = new PixelRect(1700, 1032, 220, 48);
        var r = Pager.Place(taskbar, start, tray, 4, 16.0 / 9);
        Assert.True(r is null || (!r.Value.IntersectsWith(tray) && !r.Value.IntersectsWith(start)));
    }

    [Fact]
    public void Animation_with_zero_duration_jumps_to_the_target()
    {
        var a = new Animation<double>(0, 0, (x, y, t) => x + (y - x) * t);
        a.AnimateTo(10, 0);
        a.Update(0);
        Assert.Equal(10, a.Value);
        Assert.False(a.Running);
    }

    [Fact]
    public void Animation_lands_exactly_on_the_target_at_the_end()
    {
        var a = new Animation<double>(0, 100, (x, y, t) => x + (y - x) * t);
        a.AnimateTo(10, 0);
        a.Update(50);
        Assert.InRange(a.Value, 0.1, 9.9);
        a.Update(150);
        Assert.Equal(10, a.Value);
        Assert.False(a.Running);
    }

    [Fact]
    public void Closing_cascade_across_monitors_switches_once_and_hides_each_once()
    {
        var settings = new CubeSettings { AnimationDurationMs = 500 };
        var cubes = Enumerable.Range(0, 3).Select(_ => new CubeController(settings)).ToList();
        var closing = new int[3];
        var hidden = new int[3];
        var switches = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var me = i;
            cubes[i].Closing += d =>
            {
                closing[me]++;
                foreach (var other in cubes.Where((_, j) => j != me))
                {
                    other.CloseTo(d, 600);
                }
            };
            cubes[i].Hidden += () => hidden[me]++;
            cubes[i].SwitchRequested += switches.Add;
            cubes[i].Activate(0, 4, 1920, 1080, 0);
            cubes[i].Tick(0.016, 600);
        }

        cubes[1].SwitchTo(2, 600);
        foreach (var c in cubes)
        {
            c.Tick(0.016, 1200);
            c.Tick(0.016, 1300);
        }

        Assert.Equal([1, 1, 1], closing);
        Assert.Equal([1, 1, 1], hidden);
        Assert.Equal([2], switches);
    }
}

public class PagerFollowTests
{
    private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);
    private static readonly PixelRect Taskbar = new(0, 1032, 1920, 48);
    private static readonly PixelRect Placed = new(600, 1040, 240, 32);

    [Fact]
    public void Stays_put_while_the_taskbar_does() =>
        Assert.Equal(Placed, Pager.Follow(Placed, Taskbar, Taskbar, Monitor));

    [Fact]
    public void Slides_with_an_auto_hide_taskbar()
    {
        var sliding = Taskbar with { Y = 1036 };
        Assert.Equal(Placed with { Y = 1044 }, Pager.Follow(Placed, Taskbar, sliding, Monitor));
    }

    [Fact]
    public void Hides_once_it_would_leave_the_monitor()
    {
        var hidden = Taskbar with { Y = 1078 }; // a 2 px sliver stays on screen
        Assert.Null(Pager.Follow(Placed, Taskbar, hidden, Monitor));
    }
}

public class PagerObstacleTests
{
    private static readonly PixelRect Taskbar = new(0, 1032, 1920, 48);
    private static readonly PixelRect Tray = new(1600, 1032, 320, 48);
    private const double Aspect = 16.0 / 9;

    [Fact]
    public void Keeps_clear_of_the_weather_button()
    {
        var weather = new PixelRect(0, 1032, 300, 48); // leaves 276 px; full size needs about 236
        var start = new PixelRect(600, 1036, 40, 40);
        var r = Pager.Place(Taskbar, start, Tray, 4, Aspect, [weather])!.Value;
        Assert.False(r.IntersectsWith(weather));
        Assert.Equal(start.X - 12, r.Right);
        Assert.Equal(32, r.Height); // room for full size
    }

    [Fact]
    public void Shrinks_to_fit_a_tight_gap_next_to_start()
    {
        // Full size is about 236 px wide; give it 200 between the weather and Start.
        var weather = new PixelRect(0, 1032, 376, 48); // 388..588 free
        var start = new PixelRect(600, 1036, 40, 40);
        var r = Pager.Place(Taskbar, start, Tray, 4, Aspect, [weather])!.Value;
        Assert.False(r.IntersectsWith(weather));
        Assert.Equal(start.X - 12, r.Right);
        Assert.InRange(r.Height, 20, 31);
        Assert.Equal(1032 + (48 - r.Height) / 2, r.Y); // centred
    }

    [Fact]
    public void Moves_next_to_the_tray_when_the_gap_is_too_small()
    {
        var weather = new PixelRect(0, 1032, 500, 48);
        var start = new PixelRect(600, 1036, 40, 40);
        var r = Pager.Place(Taskbar, start, Tray, 4, Aspect, [weather])!.Value;
        Assert.Equal(Tray.X - 12, r.Right);
        Assert.False(r.IntersectsWith(weather));
    }
}
