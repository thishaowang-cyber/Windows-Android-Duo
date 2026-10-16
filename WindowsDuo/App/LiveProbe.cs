using WindowsDuo.Core;
using WindowsDuo.Verify;
using System.Threading;

namespace WindowsDuo.App;

// --live-shot harness: check both capture backends, then render the real
// desktop through the glass pipeline and write PNGs.
public static class LiveProbe
{
    public static int Run(string outDir, int angleDeg, bool keystone)
    {
        Directory.CreateDirectory(outDir);

        // 1) what the desktop actually contains, per backend. The GDI grab here is
        // the production path (persistent DIB, BGRA, shader swizzle).
        var size = System.Windows.Forms.SystemInformation.VirtualScreen;
        using var cap = new GdiScreenCapture();
        if (!cap.Ensure(size.Width, size.Height)) { Console.WriteLine("GDI capture init failed"); return 1; }
        if (!cap.Grab()) { Console.WriteLine("GDI grab failed"); return 1; }

        int gw = cap.Width, gh = cap.Height;
        var rawRgba = new byte[gw * gh * 4];
        System.Runtime.InteropServices.Marshal.Copy(cap.Bits, rawRgba, 0, rawRgba.Length);
        for (int i = 0; i < rawRgba.Length; i += 4)
            (rawRgba[i], rawRgba[i + 2]) = (rawRgba[i + 2], rawRgba[i]);   // BGRA -> RGBA for the PNG only

        Console.WriteLine($"GDI (DIB): {gw}x{gh} {Describe(rawRgba, gw, gh)}");
        var small = Downscale(rawRgba, gw, gh, Math.Min(960, gw));
        PngWriter.WriteRgba(Path.Combine(outDir, "gdi_frame.png"), small.w, small.h, small.px);

        using var renderer = new GlassRenderer
        {
            Model = keystone ? GlassModel.Keystone : GlassModel.EyePoint,
        };

        using (var duplicator = new DesktopDuplicator(renderer.Device, renderer.Context))
        {
            if (!duplicator.Ready)
            {
                Console.WriteLine($"DXGI: not ready ({duplicator.LastError})");
            }
            else
            {
                for (int i = 0; i < 25 && duplicator.Frames == 0; i++) { duplicator.TryCapture(80); Thread.Sleep(50); }
                if (duplicator.TryReadbackRgba(out var dx, out int dw, out int dh))
                    Console.WriteLine($"DXGI: {dw}x{dh} {Describe(dx, dw, dh)}");
                else
                    Console.WriteLine($"DXGI: no frame ({duplicator.LastError})");
            }
        }

        // 2) render the glass effect over the real desktop content, through the
        // same upload path live mode uses (unmanaged BGRA pointer + swizzle)
        renderer.SetSourcePointer(cap.Bits, gw, gh, mips: true);
        int w = small.w;
        int h = small.h;
        float tilt = Mapping.Tilt(angleDeg, 120, 1.0);

        OffscreenShot.Capture(renderer, w, h, 0f, 0.09f, 2.09f, Path.Combine(outDir, "live_raw.png"));
        OffscreenShot.Capture(renderer, w, h, tilt, 0.09f, 2.09f, Path.Combine(outDir, $"live_tilt{(int)tilt:00}.png"));

        // Channel check: at tilt 0 the shader is (nearly) the identity, so its
        // output must match the raw capture. A swapped red/blue would flip the
        // sign of the mean (R - B), which no amount of eyeballing reliably shows.
        var (shot, _, _) = OffscreenShot.CaptureRgba(renderer, w, h, 0f, 0.09f, 2.09f);
        Console.WriteLine($"mean(R-B) raw={MeanRB(small.px):+0.0;-0.0}  rendered={MeanRB(shot):+0.0;-0.0}   (must have the same sign)");

        Console.WriteLine($"rendered {w}x{h} at tilt {tilt:0} -> live_tilt{(int)tilt:00}.png");
        return 0;
    }

    private static string Describe(byte[] px, int w, int h)
    {
        long sum = 0;
        int n = 0, distinct = 0;
        uint first = 0;
        for (int y = 0; y < h; y += 53)
            for (int x = 0; x < w; x += 47)
            {
                int i = (y * w + x) * 4;
                uint v = (uint)((px[i] << 16) | (px[i + 1] << 8) | px[i + 2]);
                if (n == 0) first = v; else if (v != first) distinct++;
                sum += px[i] + px[i + 1] + px[i + 2];
                n++;
            }
        return $"mean={(double)sum / (n * 3):0.0} distinct={distinct}/{n}";
    }

    private static double MeanRB(byte[] rgba)
    {
        long sum = 0;
        int n = 0;
        for (int i = 0; i < rgba.Length; i += 4) { sum += rgba[i] - rgba[i + 2]; n++; }
        return n == 0 ? 0 : (double)sum / n;
    }

    private static (byte[] px, int w, int h) Downscale(byte[] src, int sw, int sh, int dw)
    {
        int dh = (int)((long)dw * sh / sw);
        var dst = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
        {
            int sy = y * sh / dh;
            for (int x = 0; x < dw; x++)
            {
                int sx = x * sw / dw;
                int si = (sy * sw + sx) * 4, di = (y * dw + x) * 4;
                dst[di] = src[si]; dst[di + 1] = src[si + 1]; dst[di + 2] = src[si + 2]; dst[di + 3] = 255;
            }
        }
        return (dst, dw, dh);
    }
}
