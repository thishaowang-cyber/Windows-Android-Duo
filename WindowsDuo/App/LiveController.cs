using WindowsDuo.Core;

namespace WindowsDuo.App;

// Glue for live mode: captures the desktop, feeds it to the shared renderer and
// drives the overlay's tilt from the hinge angle.
//
// Capture has two backends. DXGI Desktop Duplication is the fast one, but on
// some machines (and all remote sessions) it hands back a blank scanout even
// though the desktop is live; we probe for that and fall back to a GDI grab,
// which is slower but sees what is actually on screen.
public sealed class LiveController : IDisposable
{
    private readonly GlassRenderer _renderer;
    private readonly PhoneAngleLink _angles;
    private readonly AppSettings _settings;

    private DesktopDuplicator? _duplicator;
    private OverlayWindow? _overlay;
    private GlassPanel? _panel;
    private System.Windows.Forms.Timer? _timer;
    private DateTime _previewUntil = DateTime.MinValue;
    private bool _useGdi;
    private string _backend = "";

    // GDI capture is expensive (~38 ms to pull 2560x1600 back from the GPU), so
    // it runs on its own thread with double buffering. Doing it on the UI thread
    // stalled the message loop for the whole capture and made the app feel stuck.
    private GdiScreenCapture? _front, _back;
    private Thread? _captureThread;
    private volatile bool _stopCapture;
    private readonly object _captureGate = new();
    private int _frameSeq, _uploadedSeq;
    private int _displayedFrames;
    private DateTime _fpsWindowStart = DateTime.UtcNow;

    /// Frames actually presented per second, measured over the live session.
    public double DisplayedFps { get; private set; }

    public Form? Owner { get; set; }

    /// Eye distance in screen heights, derived from the panel's physical size.
    public float Eye { get; set; } = DisplayMetrics.FallbackEye;

    /// Edge glow reach, as a fraction of the drawable height.
    public float BleedK { get; set; } = 0.06f;

    public bool Running { get; private set; }
    public bool OverlayVisible => _overlay?.Visible ?? false;
    public bool CaptureExclusionOk => _overlay?.CaptureExclusionOk ?? false;
    public string Status { get; private set; } = "未启动";
    public event Action? StateChanged;

    public LiveController(GlassRenderer renderer, PhoneAngleLink angles, AppSettings settings)
    {
        _renderer = renderer;
        _angles = angles;
        _settings = settings;
    }

    public void Start(bool preview = false)
    {
        if (Running) return;

        _duplicator?.Dispose();
        _duplicator = new DesktopDuplicator(_renderer.Device, _renderer.Context);
        _useGdi = !_duplicator.Ready || !ProbeDuplication();
        if (_useGdi) StartGdiCapture();

        _overlay = new OverlayWindow();
        _panel = new GlassPanel(_renderer) { Dock = DockStyle.Fill, CornerRadius = 0 };
        _overlay.Controls.Add(_panel);
        _overlay.Show();
        _panel.Start();

        string backend = _useGdi ? "GDI 抓屏回退" : "DXGI 桌面复制";
        _backend = backend;
        string exclusion = _overlay.CaptureExclusionOk
            ? ""
            : $" · 警告：自身窗口未从捕获排除（{_overlay.LastError}）";
        Status = $"实时桌面已启用（{backend}）{exclusion}";

        // The setup window is only hidden while the overlay actually covers the
        // screen — see Tick. Hiding it here would leave the user with nothing at
        // all whenever the effect happens to be zero.
        _previewUntil = preview ? DateTime.UtcNow.AddSeconds(8) : DateTime.MinValue;

        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        Running = true;
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        if (!Running) return;
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
        StopGdiCapture();

        _panel?.Dispose();
        _panel = null;
        _overlay?.Close();
        _overlay?.Dispose();
        _overlay = null;

        _duplicator?.Dispose();
        _duplicator = null;

        Running = false;
        Status = "已停止";
        Owner?.Show();
        Owner?.Activate();
        StateChanged?.Invoke();
    }

    public void Toggle() { if (Running) Stop(); else Start(); }

    // Duplication is considered unusable when it succeeds but the frame is a
    // single flat colour — the classic blank-scanout signature.
    private bool ProbeDuplication()
    {
        for (int i = 0; i < 25 && _duplicator!.Frames == 0; i++) _duplicator.TryCapture(80);
        if (_duplicator!.Frames == 0) return false;
        if (!_duplicator.TryReadbackRgba(out var px, out int w, out int h)) return false;

        uint first = 0;
        int n = 0;
        for (int y = 0; y < h; y += 53)
            for (int x = 0; x < w; x += 47)
            {
                int i = (y * w + x) * 4;
                uint v = (uint)((px[i] << 16) | (px[i + 1] << 8) | px[i + 2]);
                if (n == 0) first = v;
                else if (v != first) return true;
                n++;
            }
        return false;
    }

    private void Tick()
    {
        if (_panel is null || _overlay is null) return;

        if (_useGdi)
        {
            lock (_captureGate)
            {
                if (_frameSeq != _uploadedSeq && _front is { Ready: true })
                {
                    _renderer.SetSourcePointer(_front.Bits, _front.Width, _front.Height, mips: true);
                    _uploadedSeq = _frameSeq;
                    _displayedFrames++;
                }
            }
        }
        else
        {
            _duplicator!.TryCapture(0);
            if (_duplicator.Srv is not null && _renderer.SourceWidth != _duplicator.Width)
                _renderer.SetLiveSource(_duplicator.Srv, _duplicator.Width, _duplicator.Height);
        }

        bool previewing = DateTime.UtcNow < _previewUntil;
        double angle = _angles.Angle;
        float tilt = previewing ? 35f : Mapping.GlobalTilt(angle, Mapping.OpenAngle);

        _renderer.BleedK = BleedK;
        _panel.SetParams(tilt, _settings.Frost, Eye);

        // Hysteresis: hide the overlay once the effect has fully decayed.
        bool wantVisible = previewing || tilt > 0.5f;
        if (wantVisible && !_overlay.Visible) _overlay.Show();
        else if (!wantVisible && _overlay.Visible) _overlay.Hide();

        // Keep exactly one of the two windows on screen. Hiding the setup window
        // while the effect is present keeps it out of the captured desktop; if
        // the effect is zero (angle at or past the calibrated endpoint) the
        // setup window stays so the app never becomes invisible.
        if (Owner is not null)
        {
            if (wantVisible && Owner.Visible) Owner.Hide();
            else if (!wantVisible && !Owner.Visible) Owner.Show();
        }

        bool zero = !previewing && angle >= Mapping.OpenAngle;
        var since = DateTime.UtcNow - _fpsWindowStart;
        if (since.TotalSeconds >= 1) { DisplayedFps = _displayedFrames / since.TotalSeconds; _displayedFrames = 0; _fpsWindowStart = DateTime.UtcNow; }

        Status = zero
            ? $"实时中，但角度 {angle:0.0}° ≥ 展开终点 {Mapping.OpenAngle:0}°，效果恒为 0"
            : $"实时中（{_backend}）· {_renderer.SourceWidth}×{_renderer.SourceHeight} · " +
              $"角度 {angle:0.0}° · tilt {tilt:0.0}° · {DisplayedFps:0.0} fps · {(wantVisible ? "显示" : "隐藏")}";
    }

    private void StopGdiCapture()
    {
        _stopCapture = true;
        try { _captureThread?.Join(400); } catch { /* shutting down */ }
        _captureThread = null;
        _front?.Dispose(); _front = null;
        _back?.Dispose(); _back = null;
    }

    private void StartGdiCapture()
    {
        var size = System.Windows.Forms.SystemInformation.VirtualScreen;
        _front = new GdiScreenCapture();
        _back = new GdiScreenCapture();
        if (!_front.Ensure(size.Width, size.Height) || !_back.Ensure(size.Width, size.Height))
        {
            StopGdiCapture();
            Status = "GDI 抓屏初始化失败";
            return;
        }
        _stopCapture = false;
        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "hinge-capture" };
        _captureThread.Start();
    }

    // Own thread, double buffered: the grabber writes into the back buffer and
    // swaps it in under the lock, which is held only for the pointer exchange.
    private void CaptureLoop()
    {
        while (!_stopCapture)
        {
            var back = _back;
            if (back is null || !back.Ready || !back.Grab()) { Thread.Sleep(4); continue; }
            lock (_captureGate)
            {
                (_front, _back) = (back, _front);
                _frameSeq++;
            }
        }
    }

    public void Dispose() => Stop();
}
