using System.Runtime.InteropServices;
using WindowsDuo.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace WindowsDuo.Verify;

// Headless verification of the HLSL port.
//
//  1. geometry probe  - the shader emits the projected point and gap directly;
//                       the CPU recomputes the same formula and the two are
//                       compared per pixel (no texture sampling in the way).
//  2. visual renders  - the full composite is rendered at several angles for
//                       both projection models and written out as PNGs.
public static class HeadlessVerify
{
    private const int W = 960, H = 600;          // render target
    private const int SW = 1920, SH = 1200;      // source pattern
    private const float Frost = 0.09f;
    private const float Eye = 2.4f;

    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var report = new List<string>();
        void Log(string s) { Console.WriteLine(s); report.Add(s); }

        Log("Windows Duo headless verify");
        Log($"render target {W}x{H}, source {SW}x{SH}, frost={Frost}, eye={Eye}");

        var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 },
            out ID3D11Device? device, out _, out ID3D11DeviceContext? ctx);
        if (device is null || ctx is null)
        {
            Log($"FAIL: D3D11CreateDevice -> {result}");
            return 1;
        }
        using var _d = device;
        using var _c = ctx;

        string shaderSource = File.ReadAllText(ShaderPath());
        var src = Compiler.Compile(shaderSource, "VSMain", "Glass.hlsl", "vs_5_0");
        using var vs = device.CreateVertexShader(src.Span);

        ID3D11PixelShader MakePs(string entry) =>
            device.CreatePixelShader(Compiler.Compile(shaderSource, entry, "Glass.hlsl", "ps_5_0").Span);
        using var psEye = MakePs("PSMain");
        using var psKey = MakePs("PSMainKeystone");
        using var psGeomEye = MakePs("PSGeometry");
        using var psGeomKey = MakePs("PSGeometryKeystone");
        Log("shaders compiled (vs_5_0 / ps_5_0)");

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
            ByteWidth = GlassParams.SizeInBytes,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None,
        });

        // Source pattern with a full mip chain (the frost disk samples mips).
        var pattern = TestPattern.Desktop(SW, SH);
        using var srcTex = CreateTextureWithMips(device, ctx, SW, SH, pattern);
        using var srcSrv = device.CreateShaderResourceView(srcTex);

        // Float target for the geometry probe (exact readback).
        using var geomTex = CreateRt(device, Format.R32G32B32A32_Float);
        using var geomRtv = device.CreateRenderTargetView(geomTex);
        using var geomStage = CreateStaging(device, Format.R32G32B32A32_Float);

        // RGBA8 target for the visual renders.
        using var visTex = CreateRt(device, Format.R8G8B8A8_UNorm);
        using var visRtv = device.CreateRenderTargetView(visTex);
        using var visStage = CreateStaging(device, Format.R8G8B8A8_UNorm);

        ctx.RSSetViewport(new Viewport(0, 0, W, H));
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(vs);
        ctx.VSSetConstantBuffer(0, cb);
        ctx.PSSetConstantBuffer(0, cb);
        ctx.PSSetSampler(0, sampler);

        float[] angles = { 0f, 20f, 40f, 60f, 80f };

        // --- 1. geometry probe vs CPU oracle --------------------------------
        Log("");
        Log("geometry probe (shader vs CPU reference, pixel centres):");
        bool geomOk = true;
        foreach (var a in angles)
        {
            var (maxEye, meanEye) = CompareGeometry(ctx, cb, psGeomEye, geomTex, geomRtv, geomStage, a, CpuReference.EyePoint);
            var (maxKey, meanKey) = CompareGeometry(ctx, cb, psGeomKey, geomTex, geomRtv, geomStage, a, CpuReference.Keystone);
            bool ok = maxEye < 1e-3f && maxKey < 1e-3f;
            geomOk &= ok;
            Log($"  angle {a,5:0}deg  eye-point max={maxEye:E3} mean={meanEye:E3} | keystone max={maxKey:E3} mean={meanKey:E3}  {(ok ? "OK" : "MISMATCH")}");
        }
        Log(geomOk ? "geometry probe: PASS" : "geometry probe: FAIL");

        // --- 2. visual renders ----------------------------------------------
        Log("");
        Log("visual renders:");
        var srcSmall = Downscale(pattern, SW, SH, W, H);
        PngWriter.WriteRgba(Path.Combine(outDir, "source.png"), W, H, srcSmall);
        Log("  source.png");

        foreach (var a in angles)
        {
            var eye = RenderVisual(ctx, cb, psEye, visTex, visRtv, visStage, srcSrv, a);
            var key = RenderVisual(ctx, cb, psKey, visTex, visRtv, visStage, srcSrv, a);
            var tag = $"a{(int)a:00}";

            PngWriter.WriteRgba(Path.Combine(outDir, $"eyepoint_{tag}.png"), W, H, eye);
            PngWriter.WriteRgba(Path.Combine(outDir, $"keystone_{tag}.png"), W, H, key);
            PngWriter.WriteRgba(Path.Combine(outDir, $"compare_{tag}.png"), W * 3, H, Montage(new[] { srcSmall, eye, key }));
            Log($"  {tag}: eyepoint_{tag}.png  keystone_{tag}.png  compare_{tag}.png");
        }

        File.WriteAllLines(Path.Combine(outDir, "report.txt"), report);
        Log($"report: {Path.Combine(outDir, "report.txt")}");
        return geomOk ? 0 : 1;
    }

    // --- helpers -------------------------------------------------------------

    private static string ShaderPath()
    {
        var p = Path.Combine(AppContext.BaseDirectory, "Shaders", "Glass.hlsl");
        if (!File.Exists(p)) p = Path.GetFullPath(Path.Combine("Shaders", "Glass.hlsl"));
        return p;
    }

    private static ID3D11Texture2D CreateTextureWithMips(ID3D11Device device, ID3D11DeviceContext ctx, int w, int h, byte[] rgba)
    {
        int mipCount = 1 + (int)MathF.Floor(MathF.Log2(Math.Max(w, h)));
        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = (uint)mipCount,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        };

        var tex = device.CreateTexture2D(desc);
        ctx.UpdateSubresource(rgba, tex, 0, (uint)(w * 4), 0);
        using var srv = device.CreateShaderResourceView(tex);
        ctx.GenerateMips(srv);
        return tex;
    }

    private static ID3D11Texture2D CreateRt(ID3D11Device device, Format fmt) => device.CreateTexture2D(new Texture2DDescription
    {
        Width = W,
        Height = H,
        MipLevels = 1,
        ArraySize = 1,
        Format = fmt,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.RenderTarget,
        CPUAccessFlags = CpuAccessFlags.None,
        MiscFlags = ResourceOptionFlags.None,
    });

    private static ID3D11Texture2D CreateStaging(ID3D11Device device, Format fmt) => device.CreateTexture2D(new Texture2DDescription
    {
        Width = W,
        Height = H,
        MipLevels = 1,
        ArraySize = 1,
        Format = fmt,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Staging,
        BindFlags = BindFlags.None,
        CPUAccessFlags = CpuAccessFlags.Read,
        MiscFlags = ResourceOptionFlags.None,
    });

    private static void SetParams(ID3D11DeviceContext ctx, ID3D11Buffer cb, float angle, float frost, float eye, float aspect)
    {
        var p = new GlassParams { Size = new System.Numerics.Vector2(W, H), Angle = angle, Frost = frost, Eye = eye, Aspect = aspect };
        var m = ctx.Map(cb, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.StructureToPtr(p, m.DataPointer, false);
        ctx.Unmap(cb, 0);
    }

    private static void DrawFullscreen(ID3D11DeviceContext ctx, ID3D11PixelShader ps, ID3D11RenderTargetView rtv)
    {
        ctx.OMSetRenderTargets(rtv);
        ctx.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 1));
        ctx.PSSetShader(ps);
        ctx.Draw(3, 0);
    }

    private static (float max, float mean) CompareGeometry(ID3D11DeviceContext ctx, ID3D11Buffer cb,
        ID3D11PixelShader ps, ID3D11Texture2D tex, ID3D11RenderTargetView rtv, ID3D11Texture2D stage,
        float angle, Func<float, float, float, float, float, float, (float x, float y, float b)> oracle)
    {
        SetParams(ctx, cb, angle, Frost, Eye, (float)SW / SH);
        DrawFullscreen(ctx, ps, rtv);
        ctx.CopyResource(stage, tex);
        ctx.Flush();

        var m = ctx.Map(stage, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        float max = 0, sum = 0;
        try
        {
            for (int y = 0; y < H; y++)
            {
                var row = Offset(m.DataPointer, (long)y * m.RowPitch);
                for (int x = 0; x < W; x++)
                {
                    float gx = ReadF(Offset(row, x * 16));
                    float gy = ReadF(Offset(row, x * 16 + 4));
                    float gb = ReadF(Offset(row, x * 16 + 8));
                    var (ex, ey, eb) = oracle(x + 0.5f, y + 0.5f, W, H, angle, Eye);
                    max = MathF.Max(max, MathF.Max(MathF.Abs(gx - ex), MathF.Max(MathF.Abs(gy - ey), MathF.Abs(gb - eb))));
                    sum += MathF.Abs(gx - ex) + MathF.Abs(gy - ey) + MathF.Abs(gb - eb);
                }
            }
        }
        finally { ctx.Unmap(stage, 0); }
        return (max, sum / (W * H * 3));
    }

    private static unsafe float ReadF(IntPtr p) => *(float*)p;

    private static IntPtr Offset(IntPtr p, long offset) => IntPtr.Add(p, (int)offset);

    private static byte[] RenderVisual(ID3D11DeviceContext ctx, ID3D11Buffer cb,
        ID3D11PixelShader ps, ID3D11Texture2D tex, ID3D11RenderTargetView rtv, ID3D11Texture2D stage,
        ID3D11ShaderResourceView srcSrv, float angle)
    {
        SetParams(ctx, cb, angle, Frost, Eye, (float)SW / SH);
        ctx.PSSetShaderResource(0, srcSrv);
        DrawFullscreen(ctx, ps, rtv);
        ctx.CopyResource(stage, tex);
        ctx.Flush();
        return ReadRgba8(ctx, stage);
    }

    private static byte[] ReadRgba8(ID3D11DeviceContext ctx, ID3D11Texture2D stage)
    {
        var outPx = new byte[W * H * 4];
        var m = ctx.Map(stage, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            for (int y = 0; y < H; y++)
            {
                var row = Offset(m.DataPointer, (long)y * m.RowPitch);
                Marshal.Copy(row, outPx, y * W * 4, W * 4);
            }
        }
        finally { ctx.Unmap(stage, 0); }
        return outPx;
    }

    private static byte[] Downscale(byte[] src, int sw, int sh, int dw, int dh)
    {
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
        return dst;
    }

    private static byte[] Montage(IReadOnlyList<byte[]> panels)
    {
        int pw = W, ph = H, n = panels.Count;
        var dst = new byte[pw * n * ph * 4];
        for (int p = 0; p < n; p++)
            for (int y = 0; y < ph; y++)
                Buffer.BlockCopy(panels[p], y * pw * 4, dst, (y * pw * n + p * pw) * 4, pw * 4);
        return dst;
    }
}
