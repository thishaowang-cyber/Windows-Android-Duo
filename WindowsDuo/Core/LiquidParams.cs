using System.Numerics;
using System.Runtime.InteropServices;

namespace WindowsDuo.Core;

// Matches `cbuffer Liquid` in Shaders/Glass.hlsl (560 bytes, 16-byte aligned).
//
// Slab 0 is the window's own pane and covers the whole bitmap. Every other slab
// floats on it: the block first, then the cards. They are drawn together because
// the page is rendered once for the whole window and cropped per control - a slab
// drawn on its own would cost the frame a second render and a second read-back,
// and that is most of what a frame's budget goes on.
//
// All lengths are device pixels.
[StructLayout(LayoutKind.Sequential)]
public struct LiquidParams
{
    public const int MaxSlabs = 8;

    public Vector2 ScreenSize;    // backdrop size
    public Vector2 ScreenOrigin;  // this bitmap's top-left, in backdrop pixels
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxSlabs)] public Vector4[] Block;  // centre.xy, half size.zw
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxSlabs)] public Vector4[] Shape;  // radius, bezel, thickness, IOR
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxSlabs)] public Vector4[] Look;   // blur, specular, tint strength, shadow
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxSlabs)] public Vector4[] Tint;   // tint colour, rgb
    public Vector2 Light;         // light direction, normalised
    public Vector2 BitmapSize;    // size of the bitmap being drawn
    public Vector4 Meta;          // x = device pixel scale, y = backdrop is BGRA, w = slab count

    public const int SizeInBytes = 560;

    /// The marshaller walks the slab arrays, so they have to exist before the
    /// struct is handed to D3D. Use this instead of `new LiquidParams()`.
    public static LiquidParams Create() => new()
    {
        Block = new Vector4[MaxSlabs],
        Shape = new Vector4[MaxSlabs],
        Look = new Vector4[MaxSlabs],
        Tint = new Vector4[MaxSlabs],
    };

    /// Fills one slab slot. `count` is the number of slabs already placed.
    public void SetSlab(int index, Vector4 block, Vector4 shape, Vector4 look, Vector4 tint)
    {
        Block[index] = block;
        Shape[index] = shape;
        Look[index] = look;
        Tint[index] = tint;
    }
}
