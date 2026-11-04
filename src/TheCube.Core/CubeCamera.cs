using System.Numerics;

namespace TheCube.Core;

/// <summary>
/// Orbit camera around the cube, mirroring the KDE cube's controller: a rotation (elevation about X,
/// azimuth about Y) and a distance from the centre. Rotation and distance changes animate with
/// OutCubic easing, except while the user is dragging, when rotation follows the pointer directly.
/// Elevation is limited to ±30° while dragging; the mouse wheel zooms.
/// </summary>
public sealed class CubeCamera
{
    public const double VerticalFieldOfView = 60;
    public const double OpenElevation = -20;
    public const double MinElevation = -30;
    public const double MaxElevation = 30;
    public const double DragSpeed = 10;

    private readonly Animation<Quaternion> _rotation;
    private readonly Animation<double> _radius;

    private bool _dragging;
    private Vector2 _lastPos;
    private Vector2 _currentPos;

    public CubeCamera(double durationMs)
    {
        _rotation = new Animation<Quaternion>(Quaternion.Identity, durationMs, (a, b, t) => Quaternion.Slerp(a, b, (float)t));
        _radius = new Animation<double>(2000, durationMs, (a, b, t) => a + (b - a) * t);
    }

    public bool InvertX { get; set; }
    public bool InvertY { get; set; }

    /// <summary>True while the user is dragging the cube round.</summary>
    public bool Busy => _dragging;

    /// <summary>True while any animation is still running.</summary>
    public bool Animating => _rotation.Running || _radius.Running;

    public bool RotationAnimating => _rotation.Running;

    public Quaternion Rotation => _rotation.Value;
    public double Radius => _radius.Value;

    /// <summary>Current (elevation, azimuth) in degrees.</summary>
    public (double Elevation, double Azimuth) Euler => QuaternionMath.ToEulerDegrees(Rotation);

    /// <summary>Camera position on the orbit sphere (Y up). The camera always looks at the origin.</summary>
    public Vector3 Position
    {
        get
        {
            var (elevation, azimuth) = Euler;
            var theta = (elevation + 90) * Math.PI / 180;
            var phi = azimuth * Math.PI / 180;
            var r = Radius;
            return new Vector3(
                (float)(r * Math.Sin(phi) * Math.Sin(theta)),
                (float)(r * Math.Cos(theta)),
                (float)(r * Math.Cos(phi) * Math.Sin(theta)));
        }
    }

    /// <summary>Jump straight to the "closed" pose (face of <paramref name="faceIndex"/> filling the screen), no animation.</summary>
    public void SnapClosed(CubeGeometry cube, int faceIndex)
    {
        _rotation.Set(QuaternionMath.FromEulerDegrees(0, cube.FaceAzimuth(faceIndex)));
        _radius.Set(cube.CloseRadius(VerticalFieldOfView));
    }

    /// <summary>Animate out to the open pose, looking down at <paramref name="faceIndex"/>.</summary>
    public void AnimateOpen(CubeGeometry cube, int faceIndex, double distanceFactor, double now)
    {
        SetRotation(QuaternionMath.FromEulerDegrees(OpenElevation, cube.FaceAzimuth(faceIndex)), now);
        _radius.AnimateTo(cube.DistantRadius(VerticalFieldOfView, distanceFactor), now);
    }

    /// <summary>Animate back in so <paramref name="faceIndex"/> fills the screen.</summary>
    public void AnimateClosed(CubeGeometry cube, int faceIndex, double now)
    {
        SetRotation(QuaternionMath.FromEulerDegrees(0, cube.FaceAzimuth(faceIndex)), now);
        _radius.AnimateTo(cube.CloseRadius(VerticalFieldOfView), now);
    }

    public void RotateLeft(CubeGeometry cube, double now) => RotateTo(cube.LeftTarget(Euler.Azimuth), now);

    public void RotateRight(CubeGeometry cube, double now) => RotateTo(cube.RightTarget(Euler.Azimuth), now);

    private void RotateTo(double targetAzimuth, double now)
    {
        if (_rotation.Running)
        {
            return;
        }

        var delta = targetAzimuth - Euler.Azimuth;
        SetRotation(QuaternionMath.FromEulerDegrees(0, delta) * Rotation, now);
    }

    private void SetRotation(Quaternion target, double now)
    {
        if (_dragging)
        {
            _rotation.Set(target);
        }
        else
        {
            _rotation.AnimateTo(target, now);
        }
    }

    /// <summary>Mouse wheel. <paramref name="angleDeltaY"/> is in eighths of a degree (120 per notch).</summary>
    public void Wheel(double angleDeltaY, bool inverted, double now)
    {
        var delta = (inverted ? -1 : 1) * angleDeltaY * 0.01;
        var current = Radius;
        _radius.AnimateTo(current + current * 0.1 * delta, now);
    }

    public void BeginDrag(Vector2 pos)
    {
        _rotation.Stop();
        _currentPos = pos;
        _lastPos = pos;
        _dragging = true;
    }

    public void DragTo(Vector2 pos) => _currentPos = pos;

    public void EndDrag() => _dragging = false;

    /// <summary>Advance one frame: apply pointer movement (while dragging) and step animations.</summary>
    /// <param name="dt">Seconds since the previous frame.</param>
    /// <param name="now">Current time in milliseconds.</param>
    public void Tick(double dt, double now)
    {
        if (_dragging)
        {
            var (elevation, azimuth) = Euler;
            var pixelDelta = _lastPos - _currentPos;
            _lastPos = _currentPos;

            var azimuthDelta = pixelDelta.X * DragSpeed * dt;
            if (InvertX) azimuthDelta = -azimuthDelta;
            azimuth = (azimuth + azimuthDelta) % 360;

            var elevationDelta = pixelDelta.Y * DragSpeed * dt;
            if (InvertY) elevationDelta = -elevationDelta;
            elevation = Math.Clamp(elevation + elevationDelta, MinElevation, MaxElevation);

            _rotation.Set(QuaternionMath.FromEulerDegrees(elevation, azimuth));
        }

        _rotation.Update(now);
        _radius.Update(now);
    }
}
