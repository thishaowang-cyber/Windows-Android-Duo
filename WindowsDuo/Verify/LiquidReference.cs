using System.Numerics;

namespace WindowsDuo.Verify;

// Plain-CPU mirror of the page-glass slab in Shaders/Glass.hlsl. Evaluated at
// pixel centres and diffed against PSLiquidProbe, so the bevel refraction and
// the rim sheen are checked numerically rather than by eye.
public static class LiquidReference
{
    public static float SdRoundedRect(Vector2 p, Vector2 halfSize, float r)
    {
        Vector2 q = new(MathF.Abs(p.X) - halfSize.X + r, MathF.Abs(p.Y) - halfSize.Y + r);
        return MathF.Min(MathF.Max(q.X, q.Y), 0f)
             + new Vector2(MathF.Max(q.X, 0f), MathF.Max(q.Y, 0f)).Length()
             - r;
    }

    /// Chamfer cross-section: 0 at the outer rim, 1 where the slab turns flat.
    public static float SurfaceHeight(float t)
    {
        float s = 1f - t;
        return MathF.Pow(1f - s * s * s * s, 0.25f);
    }

    /// Outward normal, in closed form. A finite-difference gradient of the
    /// distance field is zero along its medial axis, which would normalise to
    /// NaN across the flat middle of the slab.
    public static Vector2 SlabNormal(Vector2 p, Vector2 halfSize, float r)
    {
        Vector2 d = new(MathF.Abs(p.X) - halfSize.X + r, MathF.Abs(p.Y) - halfSize.Y + r);
        Vector2 s = new(p.X < 0f ? -1f : 1f, p.Y < 0f ? -1f : 1f);
        Vector2 n = d.X > 0f && d.Y > 0f
            ? Vector2.Normalize(d)
            : (d.X > d.Y ? new Vector2(1f, 0f) : new Vector2(0f, 1f));
        return n * s;
    }

    /// (distInside, disp, sheen, alpha) — the probe's encoding, divided by the
    /// same 100*scale the shader uses.
    public static (float dist, float disp, float sheen, float alpha) Probe(
        Vector2 screenPx, Vector2 screenSize, Vector2 blockCenter, Vector2 blockHalf,
        float radius, float bezelWidth, float thickness, float ior,
        Vector2 light, float unit)
    {
        Vector2 p = screenPx - blockCenter;
        float sd = SdRoundedRect(p, blockHalf, radius);

        float bezel = MathF.Min(bezelWidth, MathF.Min(radius, MathF.Min(blockHalf.X, blockHalf.Y)) - 1f);
        float dist = -sd;
        float t = Math.Clamp(dist / bezel, 0f, 1f);

        float h = SurfaceHeight(t);
        const float dt = 0.001f;
        float dh = (SurfaceHeight(MathF.Min(t + dt, 1f)) - h) / dt;
        float slope = MathF.Atan(dh * (thickness / bezel));
        float sinR = Math.Clamp(MathF.Sin(slope) / MathF.Max(ior, 1f), -1f, 1f);
        float thetaR = MathF.Asin(sinR);
        float disp = h * thickness * (MathF.Tan(slope) - MathF.Tan(thetaR));

        Vector2 grad = SlabNormal(p, blockHalf, radius);

        float rimDot = MathF.Abs(Vector2.Dot(grad, light));
        float rimFalloff = 1f - Smoothstep(0f, bezel * 0.4f, dist);
        float sheen = MathF.Pow(rimDot * rimFalloff, 1.5f);
        float alpha = Smoothstep(0f, 1.5f * unit, dist);

        return (dist / (100f * unit), disp / (100f * unit), sheen, alpha);
    }

    public static float Smoothstep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
