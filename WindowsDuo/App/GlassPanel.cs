using WindowsDuo.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WindowsDuo.App;

// A WinForms control that presents the glass effect through a DXGI swap chain
// bound to its own HWND.
public sealed class GlassPanel : Control
{
    private readonly GlassRenderer _renderer;
    private readonly System.Windows.Forms.Timer _timer;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _rtv;
    private int _bufW, _bufH;
    private bool _dirty = true;
    private float _tilt, _frost = 0.09f, _eye = 2.4f;
    private Region? _clip;

    /// Rounds the canvas so it matches the glass frame around it. 0 keeps it
    /// rectangular.
    public int CornerRadius
    {
        get => _cornerRadius;
        set { _cornerRadius = value; ApplyClip(); }
    }

    private int _cornerRadius = 26;

    public string? LastError { get; private set; }
    public int RenderCalls { get; private set; }
    public int PresentCalls { get; private set; }
    public bool SwapChainReady => _swapChain is not null && _rtv is not null;

    public GlassPanel(GlassRenderer renderer)
    {
        _renderer = renderer;
        BackColor = System.Drawing.Color.Black;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.UserPaint, true);
        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += (_, _) => { if (_dirty) { _dirty = false; Render(); } };
    }

    public void SetParams(float tilt, float frost, float eye)
    {
        _tilt = tilt; _frost = frost; _eye = eye; _dirty = true;
    }

    public void Start() => _timer.Start();

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TryCreateSwapChain();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyClip();
        TryResize();
        _dirty = true;
    }

    private void ApplyClip()
    {
        if (Width <= 0 || Height <= 0 || _cornerRadius <= 0) return;
        int radius = Theme.Scaled(_cornerRadius, Theme.ScaleFor(this));
        using var path = Theme.Rounded(new Rectangle(0, 0, Width, Height), radius);
        var region = new Region(path);
        var old = _clip;
        _clip = region;
        Region = region;
        old?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        _dirty = false;
        Render();
    }

    private void TryCreateSwapChain()
    {
        if (_swapChain is not null || !IsHandleCreated) return;
        try
        {
            using var dxgiDevice = _renderer.Device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();

            var desc = new SwapChainDescription1
            {
                Width = (uint)Math.Max(1, ClientSize.Width),
                Height = (uint)Math.Max(1, ClientSize.Height),
                Format = Format.R8G8B8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Vortice.DXGI.Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.None, // flip model rejects DXGI_SCALING_STRETCH
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.None,
            };
            _swapChain = factory.CreateSwapChainForHwnd(_renderer.Device, Handle, desc);
            CreateRtv();
        }
        catch (Exception ex) { LastError = $"交换链创建失败：{ex.Message}"; }
    }

    private void CreateRtv()
    {
        _rtv?.Dispose();
        using var buffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _renderer.Device.CreateRenderTargetView(buffer);
        _bufW = ClientSize.Width;
        _bufH = ClientSize.Height;
    }

    private void TryResize()
    {
        if (_swapChain is null || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        if (ClientSize.Width == _bufW && ClientSize.Height == _bufH) return;
        try
        {
            _rtv?.Dispose();
            _rtv = null;
            _swapChain.ResizeBuffers(0, (uint)ClientSize.Width, (uint)ClientSize.Height, Format.Unknown, SwapChainFlags.None);
            CreateRtv();
        }
        catch (Exception ex) { LastError = $"交换链缩放失败：{ex.Message}"; }
    }

    private void Render()
    {
        if (_swapChain is null || !_renderer.HasSource) return;
        // The controls can be laid out before the handle settles, so keep the
        // back buffer in step rather than trusting OnSizeChanged alone. A flip
        // model with Scaling.None will not stretch a stale buffer: it would draw
        // the frame into one corner of the control.
        TryResize();
        if (_rtv is null) return;
        RenderCalls++;
        try
        {
            _renderer.Draw(_rtv, ClientSize.Width, ClientSize.Height, _tilt, _frost, _eye);
            _swapChain.Present(1, PresentFlags.None);
            PresentCalls++;
        }
        catch (Exception ex) { LastError = $"渲染失败：{ex.Message}"; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _rtv?.Dispose();
            _swapChain?.Dispose();
        }
        base.Dispose(disposing);
    }
}
