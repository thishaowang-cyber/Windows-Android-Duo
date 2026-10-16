// Glass.hlsl
//
// Independent reimplementation (mathematics only) of a hinge-angle-driven
// floating-glass perspective effect. Written from the mathematical description
// of eye -> hinged display -> fixed content plane; no third-party shader or
// application source was copied.
//
// Two projection models share everything else:
//   * eye-point   - the original model, projects the hinged display back onto
//                   a fixed content plane through the viewer's eye.
//   * keystone    - hinge-preserving horizontal keystone: the hinge edge keeps
//                   its position and full width, the far edge narrows, so the
//                   image can never leave the screen.
//
// Everything after the projection (frost disk, depth dimming, grazing sheen)
// is identical, differing only in what drives the frost radius ("gap").

cbuffer Params : register(b0)
{
    float2 uSize;       // drawable size in pixels
    float  uAngle;      // virtual tilt in degrees (0 = flush, 80 = max)
    float  uFrost;      // frost strength, 0 .. 0.18
    float  uEye;        // eye distance measured in screen heights
    float  uAspect;     // source image width / height
    float  uSwizzle;    // >0.5 when the source is BGRA (live desktop capture)
    float  uTaps;       // frost disk sample count (24 in the original)
    float  uLodK;       // mip bias: lod = log2(max(1, radius * uLodK))
    float  uBleedK;     // edge glow reach, as a fraction of the drawable height
    float  uGlowGain;   // edge glow strength
    float  uPad;
};

static const float  kGolden = 2.39996323;
static const float3 kBg     = float3(0.015, 0.018, 0.025);

// Defaults matching the original shader; overridable through the constant buffer.
static const float  kDefaultTaps = 24.0;
static const float  kDefaultLodK = 0.22;

struct VSOut
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

Texture2D    gSrc : register(t0);
SamplerState gSmp : register(s0);

// Pyramid downsample. Replaces GenerateMips, whose box filter has poor sidelobe
// rejection: repeated 2x2 box averaging produces a mip chain that, once the frost
// magnifies it, reads as a grid of small squares. A 3x3 gaussian per level makes
// the chain a gaussian pyramid, so the blur is smooth at every radius.
// Runs one level at a time, reading level n-1 through a single-level view.
float4 PSDownsample(VSOut i) : SV_Target
{
    uint w, h;
    gSrc.GetDimensions(w, h);
    float2 t = 1.0 / float2(w, h);
    float2 uv = i.uv;

    float3 c = gSrc.SampleLevel(gSmp, uv, 0).rgb * 4.0;
    c += gSrc.SampleLevel(gSmp, uv + float2(-1, -1) * t, 0).rgb;
    c += gSrc.SampleLevel(gSmp, uv + float2(0, -1) * t, 0).rgb * 2.0;
    c += gSrc.SampleLevel(gSmp, uv + float2(1, -1) * t, 0).rgb;
    c += gSrc.SampleLevel(gSmp, uv + float2(-1, 0) * t, 0).rgb * 2.0;
    c += gSrc.SampleLevel(gSmp, uv + float2(1, 0) * t, 0).rgb * 2.0;
    c += gSrc.SampleLevel(gSmp, uv + float2(-1, 1) * t, 0).rgb;
    c += gSrc.SampleLevel(gSmp, uv + float2(0, 1) * t, 0).rgb * 2.0;
    c += gSrc.SampleLevel(gSmp, uv + float2(1, 1) * t, 0).rgb;
    return float4(c / 16.0, 1.0);
}

// Fullscreen triangle from SV_VertexID; uv.y = 0 at the top edge.
VSOut VSMain(uint id : SV_VertexID)
{
    float2 uv = float2(float((id << 1) & 2), float(id & 2));
    VSOut o;
    o.pos = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
    o.uv  = uv;
    return o;
}

// Aspect-fill lookup so the source covers the whole drawable. `pt` is in pixels
// with y measured from the top.
//
// Outside the source the nearest edge colour is extended and faded with distance.
// That is what makes light appear to bleed past the stretched image's edges
// instead of the picture simply stopping at a hard, flat background.
float3 SampleScene(float2 pt, float lod)
{
    float  screenAspect = uSize.x / uSize.y;
    float2 fit = screenAspect > uAspect
        ? float2(1.0, screenAspect / uAspect)
        : float2(uAspect / screenAspect, 1.0);
    float2 uv = (pt / uSize - 0.5) / fit + 0.5;

    float3 c = gSrc.SampleLevel(gSmp, clamp(uv, 0.0, 1.0), lod).rgb;
    if (uSwizzle > 0.5) c = c.bgr;

    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
    {
        float glow = uSize.y * uBleedK;
        if (glow <= 0.0) return kBg;
        float2 outside = max(max(-uv, uv - 1.0), 0.0) * uSize;   // pixels past the edge
        return c * exp(-length(outside) / glow) * uGlowGain;
    }
    return c;
}

// Gaussian-weighted disk: taps are spread evenly over the radius but weighted by
// a Gaussian, so the blur reads as a real gaussian instead of the tap lattice or
// the box-filtered mip grid showing through.
float3 FrostDisk(float2 pt, float radius, float lod)
{
    float taps = max(uTaps, 1.0);
    float sigma = max(radius / 2.2, 0.35);
    float3 color = 0.0;
    float  weight = 0.0;
    for (int i = 0; i < 128; i++)
    {
        if (float(i) >= taps) break;
        float r = sqrt((float(i) + 0.5) / taps) * radius;
        float a = float(i) * kGolden;
        float w = exp(-(r * r) / (2.0 * sigma * sigma));
        color += w * SampleScene(pt + float2(cos(a), sin(a)) * r, lod);
        weight += w;
    }
    return color / max(weight, 1e-5);
}

// Grazing reflection band that follows the tilt.
float3 Sheen(float uvY, float theta)
{
    float t = (uvY - (0.25 + sin(theta) * 0.7)) / 0.19;
    return float3(0.78, 0.87, 1.0) * exp(-t * t) * abs(sin(theta)) * 0.055;
}

// ---------------------------------------------------------------------------
// Model A: eye-point projection (the original macOS model).
// ---------------------------------------------------------------------------
void ProjectEyePoint(float2 pixel, float theta, out float2 hit, out float gap)
{
    float  d     = uSize.y - pixel.y;                    // distance from bottom
    gap          = d * sin(theta);
    float2 glass = float2(pixel.x, uSize.y - d * cos(theta));
    float  eye   = uSize.y * uEye;
    float2 center = uSize * 0.5;
    hit = center + (glass - center) * eye / max(eye - gap, eye * 0.15);
}

// ---------------------------------------------------------------------------
// Model B: hinge-preserving horizontal keystone.
// ---------------------------------------------------------------------------
void ProjectKeystone(float2 pixel, float theta, out float2 pt, out float gap)
{
    float dNorm = (uSize.y - pixel.y) / uSize.y;         // 0 hinge edge, 1 far edge
    float w     = uEye / (uEye + dNorm * sin(theta));
    float2 uv   = float2((pixel.x / uSize.x - 0.5) / w + 0.5, pixel.y / uSize.y);
    pt  = uv * uSize;
    gap = uSize.x * (1.0 - w) * 0.5;
}

float4 Composite(float2 uv, float2 samplePoint, float gap, float theta)
{
    float radius = min(abs(gap) * uFrost, uSize.y * 0.055);
    float lod    = max(0.0, log2(max(1.0, radius * uLodK)));
    float3 color = FrostDisk(samplePoint, radius, lod);

    float depth = abs(gap) / uSize.y;
    color *= 1.0 - min(depth * 0.45, 0.35);
    color += Sheen(uv.y, theta);
    return float4(color, 1.0);
}

float4 PSMain(VSOut i) : SV_Target
{
    float  theta = radians(uAngle);
    float2 pixel = i.uv * uSize;
    float2 hit; float gap;
    ProjectEyePoint(pixel, theta, hit, gap);
    return Composite(i.uv, hit, gap, theta);
}

float4 PSMainKeystone(VSOut i) : SV_Target
{
    float  theta = radians(uAngle);
    float2 pixel = i.uv * uSize;
    float2 pt; float gap;
    ProjectKeystone(pixel, theta, pt, gap);
    return Composite(i.uv, pt, gap, theta);
}

// ---------------------------------------------------------------------------
// Geometry probes: encode the projected point and gap directly so the CPU can
// verify the port numerically with no texture sampling in the way.
// R = x / size.x, G = y / size.y, B = gap / size.y
// ---------------------------------------------------------------------------
float4 PSGeometry(VSOut i) : SV_Target
{
    float  theta = radians(uAngle);
    float2 pixel = i.uv * uSize;
    float2 hit; float gap;
    ProjectEyePoint(pixel, theta, hit, gap);
    return float4(hit.x / uSize.x, hit.y / uSize.y, gap / uSize.y, 1.0);
}

float4 PSGeometryKeystone(VSOut i) : SV_Target
{
    float  theta = radians(uAngle);
    float2 pixel = i.uv * uSize;
    float2 pt; float gap;
    ProjectKeystone(pixel, theta, pt, gap);
    return float4(pt.x / uSize.x, pt.y / uSize.y, gap / uSize.y, 1.0);
}

// ---------------------------------------------------------------------------
// Page glass: a bevelled rounded-rect slab that refracts the backdrop behind it.
//
// A slab is a rounded rectangle of radius Radius with a chamfer of width Bezel
// and height Thickness running around the inside of its rim. The chamfer's
// cross-section rises from the rim inward as SurfaceHeight(t), so its slope
// bends the view of whatever is behind the slab: the ray is refracted by Snell's
// law, walks the local slab thickness and lands laterally displaced, always
// toward the inside of the slab. Flat middle, bending rim - that is what makes
// the backdrop squeeze and stretch along the border.
//
// The rim sheen is the same surface seen against a light: brightest where the
// rim's normal faces the light. Because both the distortion and the sheen come
// from the same normal, they stay consistent with each other at any size.
// ---------------------------------------------------------------------------

#define MaxSlabs 8

cbuffer Liquid : register(b1)
{
    float2 LScreenSize;    // size of the backdrop, in pixels
    float2 LScreenOrigin;  // where this bitmap's top-left corner sits in it
    float4 LBlock[MaxSlabs];  // slab 0 is the page itself; the rest sit on it
    float4 LShape[MaxSlabs];  // Radius, Bezel, Thickness, IOR
    float4 LLook[MaxSlabs];   // Blur, Specular, Tint strength, Shadow
    float4 LTint[MaxSlabs];   // Tint colour, rgb
    float2 LLight;         // light direction, normalised
    float2 LBitmapSize;    // size of the bitmap being drawn
    float4 LMeta;          // x = device pixel scale, y = backdrop is BGRA, w = slab count
};

Texture2D gBg : register(t1);

static const float2 kDisk16[16] =
{
    float2(-0.94201, -0.39906), float2( 0.94558, -0.76890),
    float2(-0.09418, -0.92938), float2( 0.34495,  0.29387),
    float2(-0.91588, -0.45771), float2(-0.81544,  0.48568),
    float2(-0.38277, -0.56071), float2(-0.12675,  0.84686),
    float2( 0.89642,  0.41254), float2( 0.18150, -0.30020),
    float2(-0.01445, -0.16001), float2( 0.59614,  0.71118),
    float2( 0.49742, -0.47280), float2( 0.80685,  0.04588),
    float2(-0.32490, -0.03965), float2(-0.60975,  0.06566),
};

// Signed distance to a rounded rectangle: negative inside.
float SdRoundedRect(float2 p, float2 halfSize, float r)
{
    float2 q = abs(p) - halfSize + r;
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
}

// Chamfer cross-section: 0 at the outer rim, 1 where the slab becomes flat.
float SurfaceHeight(float t)
{
    float s = 1.0 - t;
    return pow(1.0 - s * s * s * s, 0.25);
}

// Outward surface normal of the rounded rectangle, analytically.
//
// A finite-difference gradient of the distance field reads exactly zero all
// along the field's medial axis - where the two nearest edges are equidistant -
// and normalising that zero vector turns the whole flat middle into NaN. The
// normal is known in closed form: radial inside a corner arc, otherwise straight
// out of the closer of the two edges.
float2 SlabNormal(float2 p, float2 halfSize, float r)
{
    float2 d = abs(p) - halfSize + r;
    float2 s = float2(p.x < 0.0 ? -1.0 : 1.0, p.y < 0.0 ? -1.0 : 1.0);
    float2 n = (d.x > 0.0 && d.y > 0.0)
        ? normalize(d)
        : (d.x > d.y ? float2(1.0, 0.0) : float2(0.0, 1.0));
    return n * s;
}

float3 SampleBackdrop(float2 pt)
{
    float3 c = gBg.SampleLevel(gSmp, clamp(pt / LScreenSize, 0.0, 1.0), 0).rgb;
    return LMeta.y > 0.5 ? c.bgr : c;
}

float3 BlurBackdrop(float2 pt, float radius)
{
    if (radius < 0.5) return SampleBackdrop(pt);
    float3 sum = 0.0;
    for (int i = 0; i < 16; i++)
        sum += SampleBackdrop(pt + kDisk16[i] * radius);
    return sum / 16.0;
}

// Shared geometry for the slab: distance to the rim, chamfer position, the
// refraction offset and the rim sheen. Both the colour pass and the probe use
// it, so the numbers the CPU checks are the ones that get drawn.
struct Slab
{
    float sd;        // signed distance, pixels (negative inside)
    float dist;      // distance inside the rim, pixels
    float bezel;     // chamfer width, pixels
    float disp;      // refraction offset, pixels
    float2 grad;     // outward surface normal, horizontal
    float sheen;     // rim highlight, unlit
    float alpha;     // coverage of the slab against the backdrop
    float3 base;     // backdrop seen through the slab
};

Slab EvaluateSlab(float2 screenPx, int index)
{
    Slab s;
    float2 halfSize = LBlock[index].zw;
    float2 p = screenPx - LBlock[index].xy;
    float  radius = LShape[index].x;
    float  unit = LMeta.x;

    s.sd = SdRoundedRect(p, halfSize, radius);

    s.bezel = min(LShape[index].y, min(radius, min(halfSize.x, halfSize.y)) - 1.0);
    s.dist = -s.sd;
    float t = clamp(s.dist / s.bezel, 0.0, 1.0);

    float h = SurfaceHeight(t);
    float dt = 0.001;
    float dh = (SurfaceHeight(min(t + dt, 1.0)) - h) / dt;

    // Real-world slope of the chamfer, then Snell through the local thickness.
    float slope = atan(dh * (LShape[index].z / s.bezel));
    float sinR = clamp(sin(slope) / max(LShape[index].w, 1.0), -1.0, 1.0);
    float thetaR = asin(sinR);
    s.disp = h * LShape[index].z * (tan(slope) - tan(thetaR));

    s.grad = SlabNormal(p, halfSize, radius);

    s.base = BlurBackdrop(screenPx - s.grad * s.disp, LLook[index].x * unit);

    float rimDot = abs(dot(s.grad, LLight));
    float rimFalloff = 1.0 - smoothstep(0.0, s.bezel * 0.4, s.dist);
    s.sheen = pow(rimDot * rimFalloff, 1.5);
    s.alpha = smoothstep(0.0, 1.5 * unit, s.dist);
    return s;
}

// Glass seen through the slab, at a pixel inside it.
float4 SlabColor(Slab s, int index)
{
    float unit = LMeta.x;
    float3 color = s.base;
    color += s.sheen * LLook[index].y;

    // The chamfer reads darker than the flat middle: it is seen edge-on.
    float innerShadow = 1.0 - smoothstep(0.0, s.bezel * 0.6, s.dist);
    color *= lerp(1.0, 0.7, innerShadow * 0.3);

    // Thin bright line just inside the rim.
    float innerRim = smoothstep(0.0, 2.0 * unit, s.dist)
                   * (1.0 - smoothstep(2.0 * unit, 5.0 * unit, s.dist));
    color += innerRim * 0.15 * LLook[index].y;

    color = lerp(color, LTint[index].rgb, LLook[index].z);
    return float4(color, s.alpha);
}

// Soft shadow a slab casts on whatever is behind it.
float3 SlabShadow(Slab s, int index, float3 behind)
{
    float unit = LMeta.x;
    float falloff = exp(-s.sd * s.sd / (800.0 * unit * unit));
    return behind * (1.0 - LLook[index].w * falloff * 0.6);
}

// Slab 0 is the window's own pane and covers the bitmap; the rest are the
// surfaces floating on it - the block and the cards. Each is drawn over what is
// below it, and its shadow falls on that.
float4 PSLiquidGlass(VSOut i) : SV_Target
{
    float2 screenPx = LScreenOrigin + i.uv * LBitmapSize;
    int slabs = (int)clamp(LMeta.w, 1.0, (float)MaxSlabs);

    Slab page = EvaluateSlab(screenPx, 0);
    float4 result = page.sd <= 0.0 ? SlabColor(page, 0) : float4(0.0, 0.0, 0.0, 0.0);

    float reach = 60.0 * LMeta.x;
    for (int s = 1; s < slabs; s++)
    {
        // A slab only matters near itself, and there are a dozen of them: skip the
        // ones this pixel is nowhere near rather than evaluating every one.
        float2 d = abs(screenPx - LBlock[s].xy) - LBlock[s].zw;
        if (d.x > reach || d.y > reach) continue;

        Slab slab = EvaluateSlab(screenPx, s);
        result = slab.sd <= 0.0
            ? SlabColor(slab, s)
            : float4(SlabShadow(slab, s, result.rgb), result.a);
    }
    return result;
}

// Probe: R = distance inside the rim, G = refraction offset, B = rim sheen,
// A = coverage. Lets the CPU diff the whole geometry against its own model.
float4 PSLiquidProbe(VSOut i) : SV_Target
{
    float2 screenPx = LScreenOrigin + i.uv * LBitmapSize;
    Slab s = EvaluateSlab(screenPx, 0);
    float unit = LMeta.x;
    return float4(s.dist / (100.0 * unit),
                  s.disp / (100.0 * unit),
                  s.sheen,
                  s.alpha);
}
