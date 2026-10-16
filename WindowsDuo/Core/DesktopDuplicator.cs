using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WindowsDuo.Core;

// Live desktop frames through the DXGI Desktop Duplication API. Frames land in
// our own BGRA texture with a mip chain, ready to be sampled by the glass
// shader. Note: exclusive-fullscreen games and DRM-protected surfaces are the
// cases where this returns nothing — same limitation the original documents for
// ScreenCaptureKit.
public sealed class DesktopDuplicator : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private IDXGIOutputDuplication? _dupl;
    private ID3D11Texture2D? _tex;
    private ID3D11ShaderResourceView? _srv;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public ID3D11ShaderResourceView? Srv => _srv;
    public bool Ready => _dupl is not null && _srv is not null;
    public string? LastError { get; private set; }
    public long Frames { get; private set; }

    public DesktopDuplicator(ID3D11Device device, ID3D11DeviceContext ctx)
    {
        _device = device;
        _ctx = ctx;
        try { Init(); }
        catch (Exception ex) { LastError = $"桌面复制初始化失败：{ex.Message}"; }
    }

    private void Init()
    {
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();

        if (adapter.EnumOutputs(0, out var output).Failure)
            throw new InvalidOperationException("找不到显示器输出");

        using (output)
        {
            var rect = output.Description.DesktopCoordinates;
            Width = rect.Right - rect.Left;
            Height = rect.Bottom - rect.Top;
            using var output1 = output.QueryInterface<IDXGIOutput1>();
            _dupl = output1.DuplicateOutput(_device);
        }

        int mips = 1 + (int)MathF.Floor(MathF.Log2(Math.Max(Width, Height)));
        _tex = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = (uint)mips,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        });
        _srv = _device.CreateShaderResourceView(_tex);
    }

    // Pulls whatever frame is available and refreshes our texture + mips.
    // Returns false on timeout (a static desktop simply produces no new frame).
    public bool TryCapture(int timeoutMs = 60)
    {
        if (_dupl is null || _tex is null || _srv is null) return false;

        IDXGIResource? resource = null;
        bool acquired = false;
        try
        {
            var result = _dupl.AcquireNextFrame((uint)timeoutMs, out _, out resource);
            acquired = result.Success;
            if (!acquired) return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }

        try
        {
            using var frame = resource!.QueryInterface<ID3D11Texture2D>();
            _ctx.CopyResource(_tex, frame);
            _ctx.GenerateMips(_srv);
            Frames++;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"取帧失败：{ex.Message}";
            return false;
        }
        finally
        {
            resource?.Dispose();
            if (acquired) { try { _dupl.ReleaseFrame(); } catch { /* already released */ } }
        }
    }

    // Debug/verification: read the captured frame back on the CPU as RGBA8.
    public bool TryReadbackRgba(out byte[] rgba, out int w, out int h)
    {
        rgba = Array.Empty<byte>();
        w = Width;
        h = Height;
        if (_tex is null) return false;

        using var stage = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });

        _ctx.CopyResource(stage, _tex);
        _ctx.Flush();

        var px = new byte[Width * Height * 4];
        var m = _ctx.Map(stage, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            for (int y = 0; y < Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    IntPtr.Add(m.DataPointer, (int)((long)y * m.RowPitch)), px, y * Width * 4, Width * 4);
        }
        finally { _ctx.Unmap(stage, 0); }

        for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);
        rgba = px;
        return true;
    }

    public void Dispose()
    {
        _srv?.Dispose();
        _tex?.Dispose();
        _dupl?.Dispose();
    }
}
