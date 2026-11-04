namespace TheCube.Core;

/// <summary>
/// Layout of the "cube": really a prism with one face per virtual desktop, faces sized like the
/// screen, arranged around the Y axis. Face i sits at azimuth i * AngleTick.
/// </summary>
public sealed class CubeGeometry
{
    /// <summary>The cube needs at least this many desktops; with fewer a placeholder is shown.</summary>
    public const int MinimumFaces = 3;

    public CubeGeometry(int faceCount, double faceWidth, double faceHeight, double faceDisplacement)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(faceCount, MinimumFaces);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(faceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(faceHeight);
        FaceCount = faceCount;
        FaceWidth = faceWidth;
        FaceHeight = faceHeight;
        FaceDisplacement = faceDisplacement;
    }

    public int FaceCount { get; }
    public double FaceWidth { get; }
    public double FaceHeight { get; }
    public double FaceDisplacement { get; }

    /// <summary>Degrees between neighbouring faces.</summary>
    public double AngleTick => 360.0 / FaceCount;

    /// <summary>Distance from the cube's axis to the centre of each face.</summary>
    public double FaceDistance => 0.5 * FaceWidth / Math.Tan(AngleTick * Math.PI / 360.0) + FaceDisplacement;

    public double FaceAzimuth(int index) => AngleTick * index;

    /// <summary>Index of the face nearest to the camera at the given azimuth (degrees, any range).</summary>
    public int FaceAt(double azimuth)
    {
        // Round half up (like JavaScript's Math.round), not banker's rounding.
        var index = (int)(Math.Floor(azimuth / AngleTick + 0.5) % FaceCount);
        return index < 0 ? index + FaceCount : index;
    }

    /// <summary>Centre of face <paramref name="index"/> in world space (Y up, face 0 on +Z, face 1 on +X).</summary>
    public (double X, double Y, double Z) FaceCenter(int index)
    {
        var a = FaceAzimuth(index) * Math.PI / 180.0;
        return (FaceDistance * Math.Sin(a), 0, FaceDistance * Math.Cos(a));
    }

    /// <summary>Camera distance at which the face exactly fills the view (cube "closed").</summary>
    public double CloseRadius(double verticalFovDegrees) =>
        FaceDistance + ScreenFillDistance(verticalFovDegrees);

    /// <summary>Camera distance when the cube is open.</summary>
    public double DistantRadius(double verticalFovDegrees, double distanceFactor) =>
        FaceDistance * distanceFactor + ScreenFillDistance(verticalFovDegrees);

    private double ScreenFillDistance(double fov) => 0.5 * FaceHeight / Math.Tan(0.5 * fov * Math.PI / 180.0);

    /// <summary>Azimuth to rotate to for the Left arrow: the previous face boundary, or one further if already (almost) there.</summary>
    public double LeftTarget(double azimuth)
    {
        var next = Math.Floor(azimuth / AngleTick) * AngleTick;
        if (Math.Abs(next - azimuth) < 0.05 * AngleTick)
        {
            next -= AngleTick;
        }

        return next;
    }

    /// <summary>Azimuth to rotate to for the Right arrow.</summary>
    public double RightTarget(double azimuth)
    {
        var next = Math.Ceiling(azimuth / AngleTick) * AngleTick;
        if (Math.Abs(next - azimuth) < 0.05 * AngleTick)
        {
            next += AngleTick;
        }

        return next;
    }
}
