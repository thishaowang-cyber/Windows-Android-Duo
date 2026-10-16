using System.Runtime.InteropServices;

namespace WindowsDuo.Core;

// The eye distance is a measured quantity, not a taste knob: it is the viewing
// distance divided by the screen's physical height. The phone derives it the
// same way from its reported ydpi; here it comes from the panel's physical
// height in millimetres. A larger value means a viewer farther away, hence less
// narrowing — see the keystone/eye-point shaders, where it appears as `uEye`
// expressed in screen heights.
public static class DisplayMetrics
{
    /// Assumed viewing distance. Deliberately the same constant as the phone
    /// app so both ends of the project mean the same thing by "the effect".
    public const float ViewingDistanceMetres = 0.45f;

    /// Used only when the OS will not report a believable panel height.
    public const float FallbackEye = 2.4f;

    // A panel height outside this range is a driver artefact, not a screen.
    private const int MinHeightMm = 80, MaxHeightMm = 600;

    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    private const int HorzSize = 4;   // GetDeviceCaps: horizontal size in millimetres
    private const int VertSize = 6;   // GetDeviceCaps: vertical size in millimetres

    public readonly record struct EyeDistance(
        float Eye, int WidthMm, int HeightMm, float DiagonalInches, bool Measured, string Source);

    public static EyeDistance Derive()
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            int mmH = GetDeviceCaps(dc, VertSize);
            int mmW = GetDeviceCaps(dc, HorzSize);
            if (mmH is >= MinHeightMm and <= MaxHeightMm)
            {
                double diagIn = Math.Sqrt((double)mmW * mmW + (double)mmH * mmH) / 25.4;
                return new EyeDistance(
                    ViewingDistanceMetres / (mmH / 1000f), mmW, mmH, (float)diagIn, true, "面板物理高度");
            }
            return new EyeDistance(FallbackEye, mmW, mmH, 0, false, $"系统报告的 {mmH} mm 不可信");
        }
        catch (Exception ex)
        {
            return new EyeDistance(FallbackEye, 0, 0, 0, false, $"读取失败：{ex.Message}");
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }
}
