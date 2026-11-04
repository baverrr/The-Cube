namespace TheCube.Core;

/// <summary>Keys the cube view reacts to.</summary>
public enum CubeKey
{
    Escape,
    Left,
    Right,
    Enter,
    Space,
}

/// <summary>
/// Open/close lifecycle and input handling for the cube, independent of the UI toolkit.
/// The view feeds it input and frame ticks, reads <see cref="Camera"/> to render, and listens for
/// <see cref="SwitchRequested"/> (go to a desktop) and <see cref="Hidden"/> (remove the overlay).
/// </summary>
public sealed class CubeController
{
    private readonly CubeSettings _settings;
    private double _hideAt = double.NaN;

    public CubeController(CubeSettings settings)
    {
        _settings = settings;
        Camera = new CubeCamera(settings.AnimationDurationMs)
        {
            InvertX = settings.MouseInvertedX,
            InvertY = settings.MouseInvertedY,
        };
    }

    public CubeCamera Camera { get; }

    /// <summary>Null when there are fewer than three desktops (the placeholder is shown instead).</summary>
    public CubeGeometry? Geometry { get; private set; }

    /// <summary>True from Activate until Deactivate.</summary>
    public bool Activated { get; private set; }

    /// <summary>True while the overlay should be on screen (includes the closing animation).</summary>
    public bool Visible { get; private set; }

    public bool ShowsPlaceholder => Visible && Geometry is null;

    public int DesktopCount { get; private set; }

    /// <summary>Desktop index the cube will land on when it closes.</summary>
    public int CurrentDesktop { get; private set; }

    /// <summary>Raised with a 0-based desktop index when the user picks a desktop.</summary>
    public event Action<int>? SwitchRequested;

    /// <summary>Raised when the closing animation has finished and the overlay can go away.</summary>
    public event Action? Hidden;

    public void Toggle(int currentDesktop, int desktopCount, double screenWidth, double screenHeight, double now)
    {
        if (Activated)
        {
            Deactivate(now);
        }
        else
        {
            Activate(currentDesktop, desktopCount, screenWidth, screenHeight, now);
        }
    }

    public void Activate(int currentDesktop, int desktopCount, double screenWidth, double screenHeight, double now)
    {
        if (Activated || !double.IsNaN(_hideAt))
        {
            return;
        }

        DesktopCount = desktopCount;
        CurrentDesktop = Math.Clamp(currentDesktop, 0, Math.Max(0, desktopCount - 1));
        Geometry = desktopCount >= CubeGeometry.MinimumFaces
            ? new CubeGeometry(desktopCount, screenWidth, screenHeight, _settings.FaceDisplacement)
            : null;

        Visible = true;
        Activated = true;
        if (Geometry is not null)
        {
            Camera.SnapClosed(Geometry, CurrentDesktop);
            Camera.AnimateOpen(Geometry, CurrentDesktop, _settings.DistanceFactor, now);
        }
    }

    public void Deactivate(double now)
    {
        if (!Activated)
        {
            return;
        }

        Activated = false;
        if (Geometry is not null)
        {
            Camera.AnimateClosed(Geometry, CurrentDesktop, now);
        }

        _hideAt = now + _settings.AnimationDurationMs;
    }

    /// <summary>Switch to <paramref name="desktop"/> and close the cube, zooming into that face.</summary>
    public void SwitchTo(int desktop, double now)
    {
        if (!Activated || desktop < 0 || desktop >= DesktopCount)
        {
            return;
        }

        CurrentDesktop = desktop;
        SwitchRequested?.Invoke(desktop);
        Deactivate(now);
    }

    /// <summary>The desktop facing the camera right now.</summary>
    public int SelectedDesktop => Geometry?.FaceAt(Camera.Euler.Azimuth) ?? CurrentDesktop;

    public void SwitchToSelected(double now) => SwitchTo(SelectedDesktop, now);

    /// <summary>Handle a key press. Returns true if the key was used.</summary>
    public bool KeyPressed(CubeKey key, double now)
    {
        if (!Activated)
        {
            return false;
        }

        if (key == CubeKey.Escape)
        {
            Deactivate(now);
            return true;
        }

        if (Geometry is null)
        {
            return false;
        }

        switch (key)
        {
            case CubeKey.Left:
                Camera.RotateLeft(Geometry, now);
                return true;
            case CubeKey.Right:
                Camera.RotateRight(Geometry, now);
                return true;
            case CubeKey.Enter or CubeKey.Space:
                SwitchToSelected(now);
                return true;
            default:
                return false;
        }
    }

    /// <summary>A click (not a drag). <paramref name="faceHit"/> is the face under the pointer, if any.</summary>
    public void Clicked(int? faceHit, double now)
    {
        if (!Activated)
        {
            return;
        }

        if (Geometry is null)
        {
            Deactivate(now);
        }
        else if (faceHit is int face)
        {
            SwitchTo(face, now);
        }
        else
        {
            SwitchToSelected(now);
        }
    }

    public void Tick(double dt, double now)
    {
        Camera.Tick(dt, now);
        if (!double.IsNaN(_hideAt) && now >= _hideAt)
        {
            _hideAt = double.NaN;
            Visible = false;
            Hidden?.Invoke();
        }
    }
}
