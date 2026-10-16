using System.Runtime.InteropServices;
using WindowsDuo.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace WindowsDuo.Verify;

// Renders the current source through the glass pipeline into an off-screen
// target and writes a PNG. Used to check the live-capture path without needing
// the overlay window on screen.
public static class OffscreenShot
{
    public static void Capture(GlassRenderer renderer, int w, int h, float tilt, float frost, float eye, string path)
    {
        var (px, rw, rh) = CaptureRgba(renderer, w, h, tilt, frost, eye);
        PngWriter.WriteRgba(path, rw, rh, px);
    }

    public static (byte[] px, int w, int h) CaptureRgba(GlassRenderer renderer, int w, int h, float tilt, float frost, float eye)
    {
        var device = renderer.Device;
        var ctx = renderer.Context;

        using var tex = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });
        using var rtv = device.CreateRenderTargetView(tex);

        using var stage = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });

        renderer.Draw(rtv, w, h, tilt, frost, eye);
        ctx.CopyResource(stage, tex);
        ctx.Flush();

        var px = new byte[w * h * 4];
        var m = ctx.Map(stage, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(IntPtr.Add(m.DataPointer, (int)((long)y * m.RowPitch)), px, y * w * 4, w * 4);
        }
        finally { ctx.Unmap(stage, 0); }

        return (px, w, h);
    }
}
