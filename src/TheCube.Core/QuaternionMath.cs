using System.Numerics;

namespace TheCube.Core;

/// <summary>
/// Euler angle helpers using the Y-X-Z convention (yaw about Y applied last, roll about Z first):
/// R = Ry(azimuth) · Rx(elevation) · Rz(roll). Angles are in degrees.
/// </summary>
public static class QuaternionMath
{
    private const double Deg = 180.0 / Math.PI;

    public static Quaternion FromEulerDegrees(double elevation, double azimuth, double roll = 0) =>
        Quaternion.CreateFromYawPitchRoll((float)(azimuth / Deg), (float)(elevation / Deg), (float)(roll / Deg));

    /// <summary>Returns (elevation, azimuth) in degrees; azimuth is in (-180, 180].</summary>
    public static (double Elevation, double Azimuth) ToEulerDegrees(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        double x = q.X, y = q.Y, z = q.Z, w = q.W;
        var sinPitch = Math.Clamp(2 * (w * x - y * z), -1, 1);
        var elevation = Math.Asin(sinPitch) * Deg;
        var azimuth = Math.Atan2(2 * (x * z + w * y), 1 - 2 * (x * x + y * y)) * Deg;
        return (elevation, azimuth);
    }
}
