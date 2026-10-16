using System.Runtime.InteropServices;

namespace WindowsDuo.App;

// Fullscreen borderless overlay that never takes focus and never receives mouse
// input, so the desktop underneath stays usable. Its own pixels are excluded
// from screen capture, which is what stops the duplication feed from filming the
// overlay it is feeding.
public sealed class OverlayWindow : Form
{
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    public bool CaptureExclusionOk { get; private set; }
    public string? LastError { get; private set; }

    public OverlayWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = System.Drawing.Color.Black;
        Bounds = Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            CaptureExclusionOk = SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
            if (!CaptureExclusionOk)
                LastError = $"SetWindowDisplayAffinity 失败（错误 {Marshal.GetLastWin32Error()}）";
        }
        catch (Exception ex) { LastError = ex.Message; }
    }

    // TopMost alone does not reliably leave WS_EX_TOPMOST on the handle, and an
    // overlay that is not topmost gets covered by ordinary windows.
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && IsHandleCreated)
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
