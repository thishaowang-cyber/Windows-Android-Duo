using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WindowsDuo.App;

// Diagnostic: try Desktop Duplication on every adapter/output pair and report
// what the captured pixels actually contain. Hybrid laptops composite the
// desktop on one GPU while another still enumerates an output, so "duplication
// succeeded" is not the same as "duplication sees the desktop".
public static class DupScan
{
    public static int Run()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            var ad = adapter.Description1;
            Console.WriteLine($"adapter {a}: {ad.Description}");

            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                var od = output.Description;
                using (output)
                {
                    int w = od.DesktopCoordinates.Right - od.DesktopCoordinates.Left;
                    int h = od.DesktopCoordinates.Bottom - od.DesktopCoordinates.Top;
                    Console.Write($"  output {o}: {od.DeviceName} {w}x{h} -> ");

                    try
                    {
                        var r = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                            new[] { FeatureLevel.Level_11_0 }, out ID3D11Device? dev, out _, out ID3D11DeviceContext? ctx);
                        if (dev is null || ctx is null) { Console.WriteLine($"device failed: {r}"); continue; }

                        using (dev)
                        using (ctx)
                        using (var output1 = output.QueryInterface<IDXGIOutput1>())
                        {
                            try
                            {
                                using var dupl = output1.DuplicateOutput(dev);
                                Sample(dev, ctx, dupl, w, h);
                            }
                            catch (Exception ex) { Console.WriteLine($"DuplicateOutput: {ex.Message}"); }
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"error: {ex.Message}"); }
                }
            }
            adapter.Dispose();
        }
        return 0;
    }

    private static void Sample(ID3D11Device dev, ID3D11DeviceContext ctx, IDXGIOutputDuplication dupl, int w, int h)
    {
        // Nudge the desktop so a real frame is produced, then read it back.
        for (int attempt = 0; attempt < 30; attempt++)
        {
            var res = dupl.AcquireNextFrame(200, out _, out var resource);
            if (!res.Success) { Thread.Sleep(100); continue; }

            try
            {
                using var frame = resource.QueryInterface<ID3D11Texture2D>();
                using var stage = dev.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None,
                });
                ctx.CopyResource(stage, frame);
                ctx.Flush();

                var m = ctx.Map(stage, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                long rs = 0, gs = 0, bs = 0;
                int n = 0, distinct = 0;
                uint first = 0;
                try
                {
                    for (int y = 0; y < h; y += 37)
                    {
                        var row = IntPtr.Add(m.DataPointer, (int)((long)y * m.RowPitch));
                        var line = new byte[w * 4];
                        Marshal.Copy(row, line, 0, w * 4);
                        for (int x = 0; x < w; x += 41)
                        {
                            int i = x * 4;
                            uint px = (uint)(line[i] | (line[i + 1] << 8) | (line[i + 2] << 16));
                            if (n == 0) first = px;
                            else if (px != first) distinct++;
                            bs += line[i]; gs += line[i + 1]; rs += line[i + 2];
                            n++;
                        }
                    }
                }
                finally { ctx.Unmap(stage, 0); }

                Console.WriteLine($"OK mean=({rs / n},{gs / n},{bs / n}) distinctPx={distinct} (of {n})");
                return;
            }
            finally { resource.Dispose(); dupl.ReleaseFrame(); }
        }
        Console.WriteLine("no frame within timeout");
    }
}
