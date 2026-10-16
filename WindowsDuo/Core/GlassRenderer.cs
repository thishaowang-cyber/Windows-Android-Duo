using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace WindowsDuo.Core;

public enum GlassModel { EyePoint, Keystone }

// Owns the D3D11 device and the glass pipeline. One instance is shared by the
// preview window and the live overlay.
public sealed class GlassRenderer : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _psEye;
    private readonly ID3D11PixelShader _psKey;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11Buffer _cb;
    private readonly string _shaderSource;
    private ID3D11PixelShader? _downPs;

    // Per-level views over the source texture, so the pyramid can be built level
    // by level instead of by GenerateMips.
    private ID3D11RenderTargetView[] _mipRtvs = Array.Empty<ID3D11RenderTargetView>();
    private ID3D11ShaderResourceView[] _mipSrvs = Array.Empty<ID3D11ShaderResourceView>();
    private int _mipCount;

    private ID3D11Texture2D? _srcTex;
    private ID3D11ShaderResourceView? _srcSrv;
    private int _srcW, _srcH;
    private bool _srcMips;
    private bool _ownsSrv = true;

    // Page glass: the backdrop it refracts, the offscreen target it draws into
    // and the staging copy the CPU reads back.
    private ID3D11PixelShader? _psLiquid;
    private ID3D11PixelShader? _psLiquidProbe;
    private ID3D11Buffer? _cbLiquid;
    private ID3D11Texture2D? _bgTex;
    private ID3D11ShaderResourceView? _bgSrv;
    private int _bgW, _bgH;
    private bool _bgBgra;
    public bool BackdropIsBgra => _bgBgra;
    // Slab targets, keyed by size. The page and the block on it are different
    // sizes and both are redrawn whenever the backdrop moves, so their targets
    // have to be kept rather than re-created per call.
    private sealed class SlabTarget : IDisposable
    {
        public ID3D11Texture2D? Tex;
        public ID3D11RenderTargetView? Rtv;
        public ID3D11Texture2D? Stage;
        public long Used;

        public void Dispose()
        {
            Stage?.Dispose();
            Rtv?.Dispose();
            Tex?.Dispose();
        }
    }

    private readonly Dictionary<(int W, int H), SlabTarget> _slabTargets = new();
    private long _slabClock;

    public ID3D11Device Device => _device;
    public ID3D11DeviceContext Context => _ctx;
    public GlassModel Model { get; set; } = GlassModel.EyePoint;
    public bool SourceIsBgra { get; set; }

    /// Frost disk sample count. 24 is the original; more taps allow a finer mip
    /// and therefore a smoother blur instead of the mip grid showing through.
    public float Taps { get; set; } = 24f;

    /// Frost mip bias: lod = log2(max(1, radius * LodK)). Smaller reads a finer
    /// mip; paired with a tap count that keeps neighbouring taps within a texel.
    public float LodK { get; set; } = 0.22f;

    /// How far light bleeds past the stretched image's edges, as a fraction of
    /// the drawable height. 0 turns the bleed off and restores the flat backdrop.
    public float BleedK { get; set; } = 0.06f;

    /// Strength of that edge bleed.
    public float GlowGain { get; set; } = 1.0f;
    public int SourceWidth => _srcW;
    public int SourceHeight => _srcH;
    public bool HasSource => _srcSrv is not null;

    public GlassRenderer()
    {
        var r = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 }, out ID3D11Device device, out _, out ID3D11DeviceContext ctx);
        if (device is null || ctx is null) throw new InvalidOperationException($"D3D11CreateDevice failed: {r}");
        _device = device;
        _ctx = ctx;

        string source = File.ReadAllText(ShaderPath());
        _shaderSource = source;
        _vs = _device.CreateVertexShader(Compiler.Compile(source, "VSMain", "Glass.hlsl", "vs_5_0").Span);
        _psEye = _device.CreatePixelShader(Compiler.Compile(source, "PSMain", "Glass.hlsl", "ps_5_0").Span);
        _psKey = _device.CreatePixelShader(Compiler.Compile(source, "PSMainKeystone", "Glass.hlsl", "ps_5_0").Span);

        _sampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });

        _cb = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = GlassParams.SizeInBytes,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    private ID3D11PixelShader DownShader => _downPs ??=
        _device.CreatePixelShader(Compiler.Compile(_shaderSource, "PSDownsample", "Glass.hlsl", "ps_5_0").Span);

    private ID3D11PixelShader LiquidShader => _psLiquid ??=
        _device.CreatePixelShader(Compiler.Compile(_shaderSource, "PSLiquidGlass", "Glass.hlsl", "ps_5_0").Span);

    private ID3D11PixelShader LiquidProbeShader => _psLiquidProbe ??=
        _device.CreatePixelShader(Compiler.Compile(_shaderSource, "PSLiquidProbe", "Glass.hlsl", "ps_5_0").Span);

    private ID3D11Buffer LiquidBuffer => _cbLiquid ??= _device.CreateBuffer(new BufferDescription
    {
        ByteWidth = LiquidParams.SizeInBytes,
        Usage = ResourceUsage.Dynamic,
        BindFlags = BindFlags.ConstantBuffer,
        CPUAccessFlags = CpuAccessFlags.Write,
        MiscFlags = ResourceOptionFlags.None,
    });

    /// The picture the page glass refracts. Independent of the hinge source so
    /// the two can be alive at once.
    public void SetBackdrop(byte[] rgba, int w, int h)
    {
        if (_bgTex is null || _bgW != w || _bgH != h)
        {
            _bgSrv?.Dispose();
            _bgTex?.Dispose();
            _bgTex = _device.CreateTexture2D(new Texture2DDescription
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
            _bgSrv = _device.CreateShaderResourceView(_bgTex);
            _bgW = w; _bgH = h;
        }
        _ctx.UpdateSubresource(rgba, _bgTex, 0, (uint)(w * 4), 0);
        _bgBgra = false;
    }

    /// The backdrop straight out of unmanaged BGRA memory: one memcpy, no managed
    /// array and no per-pixel channel swap, the shader swizzles instead. Used by
    /// the live path, where a new frame arrives often enough for the copy to show.
    public void SetBackdropPointer(IntPtr bgra, int w, int h)
    {
        if (_bgTex is null || _bgW != w || _bgH != h) SetBackdrop(new byte[w * h * 4], w, h);
        _ctx.UpdateSubresource(_bgTex!, 0, null, bgra, (uint)(w * 4), 0);
        _bgBgra = true;
    }

    /// Draws one slab into `dst` (RGBA, w*h*4). Straight alpha: the area around
    /// the slab carries only its shadow.
    public void RenderSlab(byte[] dst, int w, int h, LiquidParams p, bool probe)
    {
        if (_bgSrv is null) return;
        p.Meta.Y = _bgBgra ? 1f : 0f;

        var target = GetSlabTarget(w, h);

        var cb = LiquidBuffer;
        var mapped = _ctx.Map(cb, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.StructureToPtr(p, mapped.DataPointer, false);
        _ctx.Unmap(cb);

        _ctx.OMSetRenderTargets(target.Rtv!);
        _ctx.ClearRenderTargetView(target.Rtv!, new Color4(0, 0, 0, 0));
        _ctx.RSSetViewport(new Viewport(0, 0, w, h));
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_vs);
        _ctx.PSSetShader(probe ? LiquidProbeShader : LiquidShader);
        _ctx.PSSetConstantBuffer(1, cb);
        _ctx.PSSetSampler(0, _sampler);
        _ctx.PSSetShaderResource(1, _bgSrv);
        _ctx.Draw(3, 0);

        _ctx.CopyResource(target.Stage!, target.Tex!);
        var read = _ctx.Map(target.Stage!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        for (int y = 0; y < h; y++)
            Marshal.Copy(read.DataPointer + y * (int)read.RowPitch, dst, y * w * 4, w * 4);
        _ctx.Unmap(target.Stage!, 0);
    }

    private SlabTarget GetSlabTarget(int w, int h)
    {
        if (_slabTargets.TryGetValue((w, h), out var cached))
        {
            cached.Used = ++_slabClock;
            return cached;
        }

        if (_slabTargets.Count >= 4)
        {
            var oldest = _slabTargets.OrderBy(kv => kv.Value.Used).First();
            oldest.Value.Dispose();
            _slabTargets.Remove(oldest.Key);
        }

        var target = new SlabTarget
        {
            Tex = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            }),
            Stage = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            }),
            Used = ++_slabClock,
        };
        target.Rtv = _device.CreateRenderTargetView(target.Tex);
        _slabTargets[(w, h)] = target;
        return target;
    }

    public void SetSource(byte[] rgba, int w, int h, bool mips)
    {
        EnsureSourceShape(w, h, mips);
        _ctx.UpdateSubresource(rgba, _srcTex!, 0, (uint)(w * 4), 0);
        if (mips) BuildPyramid();
        SourceIsBgra = false;
    }

    // Live capture: upload straight out of unmanaged BGRA memory. One memcpy,
    // no managed array, and no per-pixel channel swap — the shader swizzles.
    public void SetSourcePointer(IntPtr bgra, int w, int h, bool mips)
    {
        EnsureSourceShape(w, h, mips);
        _ctx.UpdateSubresource(_srcTex!, 0, null, bgra, (uint)(w * 4), 0);
        if (mips) BuildPyramid();
        SourceIsBgra = true;
    }

    // One 3x3 gaussian pass per level, reading the level above through a
    // single-level view. Cheap: the whole pyramid is ~1.33x the base texels.
    private void BuildPyramid()
    {
        if (_srcTex is null || _mipCount <= 1) return;

        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_vs);
        _ctx.VSSetConstantBuffer(0, _cb);
        _ctx.PSSetShader(DownShader);
        _ctx.PSSetConstantBuffer(0, _cb);
        _ctx.PSSetSampler(0, _sampler);

        for (int level = 1; level < _mipCount; level++)
        {
            uint lw = Math.Max(1u, (uint)_srcW >> level);
            uint lh = Math.Max(1u, (uint)_srcH >> level);
            _ctx.OMSetRenderTargets(_mipRtvs[level]);
            _ctx.RSSetViewport(new Viewport(0, 0, lw, lh));
            _ctx.PSSetShaderResource(0, _mipSrvs[level - 1]);
            _ctx.Draw(3, 0);
        }
    }

    private void EnsureSourceShape(int w, int h, bool mips)
    {
        if (_srcTex is not null && _srcW == w && _srcH == h && _srcMips == mips) return;

        if (_ownsSrv) _srcSrv?.Dispose();
        _srcTex?.Dispose();
        foreach (var v in _mipRtvs) v.Dispose();
        foreach (var v in _mipSrvs) v.Dispose();
        _mipRtvs = Array.Empty<ID3D11RenderTargetView>();
        _mipSrvs = Array.Empty<ID3D11ShaderResourceView>();

        int levels = mips ? 1 + (int)MathF.Floor(MathF.Log2(Math.Max(w, h))) : 1;
        _srcTex = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = (uint)levels,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | (mips ? BindFlags.RenderTarget : 0),
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });
        _srcSrv = _device.CreateShaderResourceView(_srcTex);
        _mipCount = levels;
        _mipRtvs = new ID3D11RenderTargetView[levels];
        _mipSrvs = new ID3D11ShaderResourceView[levels];
        if (mips)
        {
            for (int level = 0; level < levels; level++)
            {
                _mipRtvs[level] = _device.CreateRenderTargetView(_srcTex, new RenderTargetViewDescription
                {
                    Format = Format.R8G8B8A8_UNorm,
                    ViewDimension = RenderTargetViewDimension.Texture2D,
                    Texture2D = new Texture2DRenderTargetView { MipSlice = (uint)level },
                });
                _mipSrvs[level] = _device.CreateShaderResourceView(_srcTex, new ShaderResourceViewDescription
                {
                    Format = Format.R8G8B8A8_UNorm,
                    ViewDimension = ShaderResourceViewDimension.Texture2D,
                    Texture2D = new Texture2DShaderResourceView { MostDetailedMip = (uint)level, MipLevels = 1 },
                });
            }
        }
        _srcW = w; _srcH = h; _srcMips = mips;
        _ownsSrv = true;
    }

    // Live capture: the texture and its mips are owned by the caller.
    public void SetLiveSource(ID3D11ShaderResourceView srv, int w, int h)
    {
        if (_ownsSrv) { _srcSrv?.Dispose(); _srcTex?.Dispose(); }
        _srcSrv = srv;
        _srcTex = null;
        _srcW = w; _srcH = h; _srcMips = true;
        _ownsSrv = false;
        SourceIsBgra = true;
    }

    public void Draw(ID3D11RenderTargetView rtv, int w, int h, float tiltDeg, float frost, float eye)
    {
        if (_srcSrv is null) return;

        var p = new GlassParams
        {
            Size = new Vector2(w, h),
            Angle = tiltDeg,
            Frost = frost,
            Eye = eye,
            Aspect = (float)_srcW / _srcH,
            Swizzle = SourceIsBgra ? 1f : 0f,
            Taps = Taps,
            LodK = LodK,
            BleedK = BleedK,
            GlowGain = GlowGain,
        };

        var m = _ctx.Map(_cb, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.StructureToPtr(p, m.DataPointer, false);
        _ctx.Unmap(_cb, 0);

        _ctx.OMSetRenderTargets(rtv);
        _ctx.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 1));
        _ctx.RSSetViewport(new Viewport(0, 0, w, h));
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_vs);
        _ctx.VSSetConstantBuffer(0, _cb);
        _ctx.PSSetShader(Model == GlassModel.Keystone ? _psKey : _psEye);
        _ctx.PSSetConstantBuffer(0, _cb);
        _ctx.PSSetSampler(0, _sampler);
        _ctx.PSSetShaderResource(0, _srcSrv);
        _ctx.Draw(3, 0);
    }

    private static string ShaderPath()
    {
        var p = Path.Combine(AppContext.BaseDirectory, "Shaders", "Glass.hlsl");
        if (!File.Exists(p)) p = Path.GetFullPath(Path.Combine("Shaders", "Glass.hlsl"));
        return p;
    }

    public void Dispose()
    {
        if (_ownsSrv) _srcSrv?.Dispose();
        _srcTex?.Dispose();
        foreach (var t in _slabTargets.Values) t.Dispose();
        _slabTargets.Clear();
        _bgSrv?.Dispose();
        _bgTex?.Dispose();
        _cbLiquid?.Dispose();
        _psLiquidProbe?.Dispose();
        _psLiquid?.Dispose();
        _cb.Dispose();
        _sampler.Dispose();
        _psEye.Dispose();
        _psKey.Dispose();
        _vs.Dispose();
        _ctx.Dispose();
        _device.Dispose();
    }
}
