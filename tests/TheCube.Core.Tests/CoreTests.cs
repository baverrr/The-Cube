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
