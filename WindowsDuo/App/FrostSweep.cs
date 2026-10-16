using WindowsDuo.Core;
using WindowsDuo.Verify;

namespace WindowsDuo.App;

// --frost-sweep: render the same frame at several (tap count, mip bias) pairs and
// montage an identical crop of each, so the cause of the blocky frost can be
// seen rather than argued about. The mip chain is box-filtered, so a large
// radius that lands on a coarse level shows the mip grid as square patches.
public static class FrostSweep
{
    // Balanced pairs: the mip texel must be about as coarse as the gap between
    // neighbouring taps, i.e. k ~ 1/sqrt(N). Sweeping k down without raising N
    // just exposes the tap lattice instead of the blur.
    private static readonly (float Taps, float LodK, string Label)[] Combos =
    {
        (24f, 0.22f, "current  24 taps, k=0.22"),
        (32f, 0.177f, "         32 taps, k=0.177"),
        (48f, 0.144f, "         48 taps, k=0.144"),
        (64f, 0.125f, "         64 taps, k=0.125"),
        (96f, 0.102f, "         96 taps, k=0.102"),
        (128f, 0.088f, "        128 taps, k=0.088"),
    };

    private const int Cols = 3;
    private const int CropW = 700, CropH = 380;

    // Render at the live overlay's size, not at the source's: there the source is
    // minified to ~69% while the frost radius is capped in screen pixels, so the
    // mip it lands on is then magnified several times — which is the condition
    // under which a box-filtered mip chain shows up as square patches.
    private const int RenderW = 1770, RenderH = 1020;

    public static int Run(string outDir, float tilt, bool desktop, bool metric, bool full, float bleed)
    {
        Directory.CreateDirectory(outDir);

        // Default to the synthetic pattern: it is deterministic and has bright,
        // hard-edged content, where a blocky blur is actually visible. The dark
        // desktop hides it. --metric swaps in a checkerboard whose residual
        // high-frequency energy says how smooth the blur really is.
        byte[] source;
        int sw, sh;
        if (metric)
        {
            sw = 2560; sh = 1600;
            source = TestPattern.Checkerboard(sw, sh, 2);
        }
        else if (desktop)
        {
            using var cap = new GdiScreenCapture();
            var size = System.Windows.Forms.SystemInformation.VirtualScreen;
            sw = size.Width; sh = size.Height;
            cap.Ensure(sw, sh);
            if (!cap.Grab()) { Console.WriteLine("desktop grab failed"); return 1; }
            source = new byte[sw * sh * 4];
            System.Runtime.InteropServices.Marshal.Copy(cap.Bits, source, 0, source.Length);
            for (int i = 0; i < source.Length; i += 4)
                (source[i], source[i + 2]) = (source[i + 2], source[i]);   // BGRA -> RGBA for upload
        }
        else
        {
            sw = 2560; sh = 1600;
            source = TestPattern.Desktop(sw, sh);
        }
        Console.WriteLine($"source {sw}x{sh} ({(metric ? "checkerboard" : desktop ? "desktop" : "pattern")}), tilt {tilt}");

        using var renderer = new GlassRenderer { BleedK = bleed };
        renderer.SetSource(source, sw, sh, mips: true);

        int rw = Math.Min(sw, RenderW);
        int rh = (int)((long)rw * sh / sw);
        int cropX = Math.Max(0, (rw - CropW) / 2), cropY = Math.Max(0, rh / 4);

        var panels = new List<(byte[] px, string label)>();
        foreach (var (taps, lodK, label) in Combos)
        {
            renderer.Taps = taps;
            renderer.LodK = lodK;
            var (px, w, h) = OffscreenShot.CaptureRgba(renderer, rw, rh, tilt, 0.09f, 2.09f);
            var crop = Crop(px, w, h, cropX, cropY, CropW, CropH);
            panels.Add((crop, label));
            Console.WriteLine($"rendered {label}   residual HF = {HighFrequency(crop, CropW, CropH):0.000}");
        }

        var montage = Grid(panels, Cols);
        var path = Path.Combine(outDir, "frost-sweep.png");
        PngWriter.WriteRgba(path, montage.w, montage.h, montage.px);
        Console.WriteLine($"montage ({Cols} cols): {path}");
        Console.WriteLine("cell order: " + string.Join(" | ", panels.Select(p => p.label.Trim())));

        // Optional full frame at the default settings, to judge the edge bleed.
        if (full)
        {
            renderer.Taps = 24f;
            renderer.LodK = 0.22f;
            var (px, w, h) = OffscreenShot.CaptureRgba(renderer, rw, rh, tilt, 0.09f, 2.09f);
            var fullPath = Path.Combine(outDir, "frost-full.png");
            PngWriter.WriteRgba(fullPath, w, h, px);
            Console.WriteLine($"full frame: {fullPath}");
        }
        return 0;
    }

    // Mean |pixel - 4-neighbour average| on luminance. A correct large blur drives
    // this to ~0; a tap lattice or mip grid leaves visible structure behind.
    private static double HighFrequency(byte[] px, int w, int h)
    {
        double sum = 0; long n = 0;
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                double c = Lum(px, w, x, y);
                double b = (Lum(px, w, x - 1, y) + Lum(px, w, x + 1, y) + Lum(px, w, x, y - 1) + Lum(px, w, x, y + 1)) * 0.25;
                sum += Math.Abs(c - b); n++;
            }
        return n == 0 ? 0 : sum / n;
    }

    private static double Lum(byte[] px, int w, int x, int y)
    {
        int i = (y * w + x) * 4;
        return px[i] * 0.2126 + px[i + 1] * 0.7152 + px[i + 2] * 0.0722;
    }

    private static byte[] Crop(byte[] src, int sw, int sh, int x, int y, int w, int h)
    {
        var dst = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
        {
            int sy = Math.Clamp(y + row, 0, sh - 1);
            int sx = Math.Clamp(x, 0, Math.Max(0, sw - w));
            Buffer.BlockCopy(src, (sy * sw + sx) * 4, dst, row * w * 4, w * 4);
        }
        return dst;
    }

    private static (byte[] px, int w, int h) Grid(List<(byte[] px, string label)> panels, int cols)
    {
        int rows = (panels.Count + cols - 1) / cols;
        int w = cols * CropW, h = rows * CropH;
        var dst = new byte[w * h * 4];
        for (int i = 0; i < panels.Count; i++)
        {
            int cx = (i % cols) * CropW, cy = (i / cols) * CropH;
            for (int row = 0; row < CropH; row++)
                Buffer.BlockCopy(panels[i].px, row * CropW * 4, dst, ((cy + row) * w + cx) * 4, CropW * 4);
        }
        return (dst, w, h);
    }
}
