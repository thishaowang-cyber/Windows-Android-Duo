namespace WindowsDuo.Verify;

// Procedural "desktop-like" source image so the verification pass needs no file
// input and any warp / keystone / overflow is obvious at a glance.
public static class TestPattern
{
    public static byte[] Desktop(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float u = (float)x / w, v = (float)y / h;

                float r = 0.06f + 0.10f * v + 0.05f * u;
                float g = 0.08f + 0.12f * v;
                float b = 0.16f + 0.22f * v;

                if (x % 64 == 0 || y % 64 == 0) { r += 0.05f; g += 0.06f; b += 0.07f; }

                if (In(u, v, 0.06f, 0.10f, 0.42f, 0.62f))
                {
                    r = 0.10f; g = 0.11f; b = 0.14f;
                    if (v <= 0.122f) { r = 0.20f; g = 0.20f; b = 0.24f; }          // title bar
                    else if (v <= 0.132f) { r = 0.15f; g = 0.30f; b = 0.55f; }     // accent line
                }
                if (In(u, v, 0.50f, 0.28f, 0.94f, 0.80f))
                {
                    r = 0.13f; g = 0.14f; b = 0.18f;
                    if (v <= 0.302f) { r = 0.20f; g = 0.20f; b = 0.24f; }
                    else if (v <= 0.312f) { r = 0.15f; g = 0.30f; b = 0.55f; }
                }

                float dx = u - 0.72f, dy = v - 0.30f;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist < 0.075f)
                {
                    float k = 1f - dist / 0.075f;
                    r = Lerp(r, 1.00f, k); g = Lerp(g, 0.78f, k); b = Lerp(b, 0.30f, k);
                }

                if (v > 0.94f) { r = 0.10f; g = 0.11f; b = 0.14f; }                 // taskbar
                if (v > 0.94f && v < 0.955f) { r = 0.30f; g = 0.60f; b = 1.00f; }   // taskbar accent

                int i = (y * w + x) * 4;
                px[i + 0] = To8(r);
                px[i + 1] = To8(g);
                px[i + 2] = To8(b);
                px[i + 3] = 255;
            }
        }
        return px;
    }

    /// High-frequency checkerboard: after a correct large blur it should come out
    /// almost flat, so the residual high-frequency energy measures how smooth the
    /// frost actually is.
    public static byte[] Checkerboard(int w, int h, int cell)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool on = ((x / cell) + (y / cell)) % 2 == 0;
                byte v = on ? (byte)235 : (byte)20;
                int i = (y * w + x) * 4;
                px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255;
            }
        return px;
    }

    private static bool In(float u, float v, float x0, float y0, float x1, float y1)
        => u >= x0 && u <= x1 && v >= y0 && v <= y1;

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static byte To8(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
}
