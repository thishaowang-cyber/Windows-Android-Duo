using System.Numerics;
using System.Runtime.InteropServices;

namespace WindowsDuo.Core;

// Matches `cbuffer Params` in Shaders/Glass.hlsl (48 bytes, 16-byte aligned).
[StructLayout(LayoutKind.Sequential)]
public struct GlassParams
{
    public Vector2 Size;
    public float Angle;
    public float Frost;
    public float Eye;
    public float Aspect;
    public float Swizzle;    // >0.5 for a BGRA source (live desktop capture)
    public float Taps;       // frost disk sample count
    public float LodK;       // mip bias for the frost
    public float BleedK;     // edge glow reach, fraction of the drawable height
    public float GlowGain;   // edge glow strength
    public float Pad;

    public const int SizeInBytes = 48;
}
