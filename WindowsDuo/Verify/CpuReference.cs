namespace WindowsDuo.Verify;

// Plain-CPU mirror of the two projections in Shaders/Glass.hlsl, evaluated at
// pixel centres. Used as the oracle for the geometry probes so the HLSL port is
// checked numerically instead of by eye.
public static class CpuReference
{
    // Returns (x / sizeX, y / sizeY, gap / sizeY) — the same encoding the
    // geometry probe pixel shaders emit.
    public static (float x, float y, float b) EyePoint(float px, float py, float sw, float sh, float angleDeg, float eyeParam)
    {
        float theta = angleDeg * MathF.PI / 180f;
        float d = sh - py;
        float gap = d * MathF.Sin(theta);
        float gx = px;
        float gy = sh - d * MathF.Cos(theta);
        float eye = sh * eyeParam;
        float cx = sw * 0.5f, cy = sh * 0.5f;
        float denom = MathF.Max(eye - gap, eye * 0.15f);
        float hx = cx + (gx - cx) * eye / denom;
        float hy = cy + (gy - cy) * eye / denom;
        return (hx / sw, hy / sh, gap / sh);
    }

    public static (float x, float y, float b) Keystone(float px, float py, float sw, float sh, float angleDeg, float eyeParam)
    {
        float theta = angleDeg * MathF.PI / 180f;
        float dNorm = (sh - py) / sh;
        float w = eyeParam / (eyeParam + dNorm * MathF.Sin(theta));
        float u = (px / sw - 0.5f) / w + 0.5f;
        float v = py / sh;
        float x = u * sw;
        float y = v * sh;
        float gap = sw * (1f - w) * 0.5f;
        return (x / sw, y / sh, gap / sh);
    }
}
