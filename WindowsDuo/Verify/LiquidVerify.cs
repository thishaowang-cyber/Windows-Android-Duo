using System.Numerics;
using System.Runtime.InteropServices;
using WindowsDuo.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace WindowsDuo.Verify;

// Headless check of the page-glass slab.
//
//  1. probe  - PSLiquidProbe emits distance-to-rim, refraction offset, rim sheen
//              and coverage; the CPU recomputes the same geometry and the two
//              are diffed per pixel.
//  2. render - the full slab is drawn over a checkerboard and written out, so
//              the refraction can also be seen bending the pattern.
public static class LiquidVerify
{
    private const int W = 720, H = 480;

    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var report = new List<string>();
        void Log(string s) { Console.WriteLine(s); report.Add(s); }

        Log("Windows Duo page-glass verify");
        Log($"target {W}x{H}");

        var r = D3D11.D3D11CreateDevice(null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 },
            out ID3D11Device? device, out _, out ID3D11DeviceContext? ctx);
        if (device is null || ctx is null)
        {
            Log($"FAIL: D3D11CreateDevice -> {r}");
            return 1;
        }
        using var _d = device;
        using var _c = ctx;

        string source = File.ReadAllText(ShaderPath());
        using var vs = device.CreateVertexShader(Compiler.Compile(source, "VSMain", "Glass.hlsl", "vs_5_0").Span);
        using var psColor = device.CreatePixelShader(Compiler.Compile(source, "PSLiquidGlass", "Glass.hlsl", "ps_5_0").Span);
        using var psProbe = device.CreatePixelShader(Compiler.Compile(source, "PSLiquidProbe", "Glass.hlsl", "ps_5_0").Span);
        Log("shaders compiled");

        using var sampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });
        using var cb = device.CreateBuffer(new BufferDescription
        {
            ByteWidth = LiquidParams.SizeInBytes,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None,
        });

        var pattern = TestPattern.Checkerboard(W, H, 24);
        using var bgTex = CreateTexture(device, ctx, W, H, pattern);
        using var bgSrv = device.CreateShaderResourceView(bgTex);

        using var probeTex = CreateRt(device, Format.R32G32B32A32_Float);
        using var probeRtv = device.CreateRenderTargetView(probeTex);
        using var probeStage = CreateStaging(device, Format.R32G32B32A32_Float);
        using var colorTex = CreateRt(device, Format.R8G8B8A8_UNorm);
        using var colorRtv = device.CreateRenderTargetView(colorTex);
        using var colorStage = CreateStaging(device, Format.R8G8B8A8_UNorm);

        ctx.RSSetViewport(new Viewport(0, 0, W, H));
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(vs);
        ctx.PSSetSampler(0, sampler);
        ctx.PSSetConstantBuffer(1, cb);
        ctx.PSSetShaderResource(1, bgSrv);

        // Two slabs: the reference site's own numbers, and a window-sized one.
        var cases = new (string name, LiquidParams p)[]
        {
            ("site", Make(W, H, blockW: 300, blockH: 200, radius: 60, bezel: 60, thickness: 50,
                          ior: 3.0f, blur: 1.5f, spec: 0.55f, tint: 0.08f, shadow: 0.5f, unit: 1f)),
            ("window", Make(W, H, blockW: 660, blockH: 420, radius: 48, bezel: 60, thickness: 40,
                            ior: 2.0f, blur: 3f, spec: 0.35f, tint: 0.06f, shadow: 0.45f, unit: 1.5f)),
        };

        Log("");
        Log("geometry probe (shader vs CPU reference, pixel centres):");
        bool ok = true;
        foreach (var (name, p) in cases)
        {
            var (maxD, maxDisp, maxS, maxA, diag) = CompareProbe(ctx, cb, psProbe, probeTex, probeRtv, probeStage, p);
            bool pass = maxD < 2e-3f && maxDisp < 2e-3f && maxS < 2e-3f && maxA < 2e-3f;
            ok &= pass;
            Log($"  {name,-7} max dist={maxD:E3} disp={maxDisp:E3} sheen={maxS:E3} alpha={maxA:E3}  {(pass ? "OK" : "MISMATCH")}");
            Log($"          {diag}");
        }
        Log(ok ? "geometry probe: PASS" : "geometry probe: FAIL");

        Log("");
        Log("renders:");
        foreach (var (name, p) in cases)
        {
            UploadParams(ctx, cb, p);
            ctx.OMSetRenderTargets(colorRtv);
            ctx.ClearRenderTargetView(colorRtv, new Color4(0, 0, 0, 0));
            ctx.PSSetShader(psColor);
            ctx.Draw(3, 0);
            var px = ReadBack(ctx, colorStage, colorTex, W, H, 4);
            PngWriter.WriteRgba(Path.Combine(outDir, $"slab_{name}.png"), W, H, px);
            Log($"  slab_{name}.png");
        }
        PngWriter.WriteRgba(Path.Combine(outDir, "slab_backdrop.png"), W, H, pattern);
        Log("  slab_backdrop.png");

        File.WriteAllLines(Path.Combine(outDir, "liquid-report.txt"), report);
        return ok ? 0 : 1;
    }

    private static LiquidParams Make(int w, int h, float blockW, float blockH, float radius, float bezel,
        float thickness, float ior, float blur, float spec, float tint, float shadow, float unit)
    {
        var p = LiquidParams.Create();
        p.ScreenSize = new Vector2(w, h);
        p.Light = Vector2.Normalize(new Vector2(0.5f, -0.7f));
        p.BitmapSize = new Vector2(w, h);
        p.SetSlab(0, new Vector4(w * 0.5f, h * 0.5f, blockW * 0.5f, blockH * 0.5f),
                     new Vector4(radius, bezel, thickness, ior),
                     new Vector4(blur, spec, tint, shadow),
                     new Vector4(1f, 1f, 1f, 0f));
        p.Meta = new Vector4(unit, 0, 0, 1);
        return p;
    }

    private static (float d, float disp, float sheen, float alpha, string diag) CompareProbe(
        ID3D11DeviceContext ctx, ID3D11Buffer cb, ID3D11PixelShader ps,
        ID3D11Texture2D tex, ID3D11RenderTargetView rtv, ID3D11Texture2D stage, LiquidParams p)
    {
        UploadParams(ctx, cb, p);
        ctx.OMSetRenderTargets(rtv);
        ctx.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 0));
        ctx.PSSetShader(ps);
        ctx.Draw(3, 0);
        var px = ReadBack(ctx, stage, tex, W, H, 16);

        float[] max = new float[4];
        int cpuNan = 0, gpuNan = 0;
        string first = "";
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            var gpu = new float[4];
            for (int c = 0; c < 4; c++)
                gpu[c] = BitConverter.ToSingle(px, (y * W + x) * 16 + c * 4);

            var cpu = LiquidReference.Probe(
                new Vector2(x + 0.5f, y + 0.5f), p.ScreenSize,
                new Vector2(p.Block[0].X, p.Block[0].Y), new Vector2(p.Block[0].Z, p.Block[0].W),
                p.Shape[0].X, p.Shape[0].Y, p.Shape[0].Z, p.Shape[0].W, p.Light, p.Meta.X);

            float[] want = { cpu.dist, cpu.disp, cpu.sheen, cpu.alpha };
            for (int c = 0; c < 4; c++)
            {
                if (float.IsNaN(gpu[c])) gpuNan++;
                if (float.IsNaN(want[c])) cpuNan++;
                if (float.IsNaN(gpu[c]) || float.IsNaN(want[c]))
                {
                    if (first.Length == 0)
                        first = $"first NaN at ({x},{y}) ch{c} gpu={gpu[c]} cpu={want[c]} dist={want[0]}";
                    continue;
                }
                max[c] = MathF.Max(max[c], MathF.Abs(gpu[c] - want[c]));
            }
        }
        string diag = (gpuNan + cpuNan) > 0 ? $"{first}; gpuNaN={gpuNan} cpuNaN={cpuNan}" : "no NaN";
        return (max[0], max[1], max[2], max[3], diag);
    }

    private static void UploadParams(ID3D11DeviceContext ctx, ID3D11Buffer cb, in LiquidParams p)
    {
        var m = ctx.Map(cb, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.StructureToPtr(p, m.DataPointer, false);
        ctx.Unmap(cb);
    }

    private static byte[] ReadBack(ID3D11DeviceContext ctx, ID3D11Texture2D stage, ID3D11Texture2D tex, int w, int h, int bpp)
    {
        ctx.CopyResource(stage, tex);
        var m = ctx.Map(stage, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var px = new byte[w * h * bpp];
        for (int y = 0; y < h; y++)
            Marshal.Copy(m.DataPointer + y * (int)m.RowPitch, px, y * w * bpp, w * bpp);
        ctx.Unmap(stage, 0);
        return px;
    }

    private static string ShaderPath()
    {
        var p = Path.Combine(AppContext.BaseDirectory, "Shaders", "Glass.hlsl");
        if (!File.Exists(p)) p = Path.GetFullPath(Path.Combine("Shaders", "Glass.hlsl"));
        return p;
    }

    private static ID3D11Texture2D CreateTexture(ID3D11Device device, ID3D11DeviceContext ctx, int w, int h, byte[] rgba)
    {
        var tex = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });
        ctx.UpdateSubresource(rgba, tex, 0, (uint)(w * 4), 0);
        return tex;
    }

    private static ID3D11Texture2D CreateRt(ID3D11Device device, Format fmt) => device.CreateTexture2D(new Texture2DDescription
    {
        Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = fmt,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget,
        CPUAccessFlags = CpuAccessFlags.None, MiscFlags = ResourceOptionFlags.None,
    });

    private static ID3D11Texture2D CreateStaging(ID3D11Device device, Format fmt) => device.CreateTexture2D(new Texture2DDescription
    {
        Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = fmt,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
        CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None,
    });
}
