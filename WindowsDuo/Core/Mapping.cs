namespace WindowsDuo.Core;

// Angle -> tilt mapping, reproduced from the original's debug view:
//   tilt = 80 * (1 - (min(openAngle, angle) / openAngle) ^ (1/strength))
// so the effect is strongest when the lid is nearly closed and vanishes at the
// open angle.
public static class Mapping
{
    /// The angle at which the lid counts as fully open: the zero of the ramp, so
    /// this is the angle at which the effect disappears.
    public const double OpenAngle = 90;

    public static float Tilt(double angle, double openAngle, double strength)
    {
        if (openAngle <= 0) return 0;
        double a = Math.Clamp(angle, 0, openAngle);
        double k = Math.Pow(a / openAngle, 1.0 / Math.Max(strength, 0.01));
        return (float)(80.0 * (1.0 - k));
    }

    // Live/global mode uses no strength exponent, just the remaining fraction.
    public static float GlobalTilt(double angle, double openAngle)
        => (float)(Math.Max(0, 1.0 - angle / openAngle) * 80.0);
}
