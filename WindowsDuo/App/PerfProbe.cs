using System.Diagnostics;
using WindowsDuo.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WindowsDuo.App;

// --perf: time each stage of the live path so the bottleneck is measured rather
// than guessed. The phone link (30 Hz) is not expected to be the limit for a
// slow physical motion like a lid.
public static class PerfProbe
{
    public static int Run(int frames)
    {
        using var renderer = new GlassRenderer();
        var size = System.Windows.Forms.SystemInformation.VirtualScreen;

        // 1a) the old path: managed bitmap + per-pixel channel swap
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < Math.Max(4, frames / 4); i++) ScreenGrabber.Capture();
        double oldMs = sw.Elapsed.TotalMilliseconds / Math.Max(4, frames / 4);
        Console.WriteLine($"old grab (managed+swap) : {oldMs,7:0.00} ms/frame");

        // 1b) the live path: grab straight into a persistent DIB, at several
        // scales, to find what is needed to hold 50 fps with headroom.
        double grabMs = 0;
        foreach (var (scale, mode, label) in new[]
                 {
                     (1.00f, GdiScreenCapture.StretchHalftone, "1.00x halftone"),
                     (0.75f, GdiScreenCapture.StretchHalftone, "0.75x halftone"),
                     (0.75f, GdiScreenCapture.StretchColorOnColor, "0.75x coloroncolor"),
                     (0.50f, GdiScreenCapture.StretchHalftone, "0.50x halftone"),
                     (0.50f, GdiScreenCapture.StretchColorOnColor, "0.50x coloroncolor"),
                 })
        {
            using var c = new GdiScreenCapture();
            if (!c.Ensure(size.Width, size.Height, scale, mode)) { Console.WriteLine($"{label}: init failed"); continue; }
            c.Grab();
            sw.Restart();
            for (int i = 0; i < frames; i++) c.Grab();
            double ms = sw.Elapsed.TotalMilliseconds / frames;
            if (scale >= 1f && mode == GdiScreenCapture.StretchHalftone) grabMs = ms;
            Console.WriteLine($"grab {label,-22} {c.Width}x{c.Height} : {ms,7:0.00} ms/frame -> {1000 / ms,5:0.0} fps max");
        }

        // 2) upload to the GPU + regenerate mips, straight from unmanaged memory
        using var cap = new GdiScreenCapture();
        if (!cap.Ensure(size.Width, size.Height)) { Console.WriteLine("DIB init failed"); return 1; }
        cap.Grab();

        // 2) upload to the GPU + regenerate mips, straight from unmanaged memory
        renderer.SetSourcePointer(cap.Bits, cap.Width, cap.Height, mips: true);
        sw.Restart();
        for (int i = 0; i < frames; i++) renderer.SetSourcePointer(cap.Bits, cap.Width, cap.Height, mips: true);
        double upMs = sw.Elapsed.TotalMilliseconds / frames;
        Console.WriteLine($"upload+mips              : {upMs,7:0.00} ms/frame");

        // 3) draw the effect off-screen at the window's size
        int rw = 1770, rh = 1020;
        using var rt = renderer.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)rw, Height = (uint)rh, MipLevels = 1, ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });
        using var rtv = renderer.Device.CreateRenderTargetView(rt);
        renderer.Draw(rtv, rw, rh, 35f, 0.09f, 2.09f);
        renderer.Context.Flush();
        sw.Restart();
        for (int i = 0; i < frames; i++)
        {
            renderer.Draw(rtv, rw, rh, 35f, 0.09f, 2.09f);
            renderer.Context.Flush();
        }
        double drawMs = sw.Elapsed.TotalMilliseconds / frames;
        Console.WriteLine($"draw {rw}x{rh}            : {drawMs,7:0.00} ms/frame");

        // On the live path the grab runs on its own thread; only upload+draw sit
        // on the UI thread.
        double uiMs = upMs + drawMs;
        double fps = 1000 / Math.Max(grabMs, uiMs);
        Console.WriteLine("---");
        Console.WriteLine($"capture thread : {grabMs,7:0.00} ms/frame -> {1000 / grabMs:0.0} fps");
        Console.WriteLine($"UI thread      : {uiMs,7:0.00} ms/frame (upload {upMs:0.00} + draw {drawMs:0.00})");
        Console.WriteLine($"live ceiling   : {fps:0.0} fps  (was {1000 / oldMs:0.0} fps end-to-end on the UI thread)");
        return 0;
    }
}
