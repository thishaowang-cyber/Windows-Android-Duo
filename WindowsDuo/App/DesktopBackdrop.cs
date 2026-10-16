using System.Runtime.InteropServices;
using WindowsDuo.Core;

namespace WindowsDuo.App;

// Feeds the page's glass the desktop that is behind the window.
//
// The window is marked WDA_EXCLUDEFROMCAPTURE, so a screen grab of its own
// rectangle returns what is behind it rather than the app itself. That is the
// only way to get at those pixels: DWM composites the desktop under the window
// but never lets an application read the result back, and without a readback
// there is nothing to refract.
//
// Grabbing happens off the UI thread and covers only the window's rectangle,
// which is a fraction of a full screen. The frame is then handed to the UI
// thread, which does the upload and the drawing: the D3D context is not
// thread-safe, and keeping every use of it on one thread is cheaper than
// guarding it. Two buffers alternate so the capture never writes the memory the
// UI thread is still uploading from.
internal sealed class DesktopBackdrop : IDisposable
{
    private const int WdaExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, int affinity);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    private readonly IntPtr _hwnd;
    private readonly Action _repaint;
    private readonly GdiScreenCapture[] _buffers = { new(), new() };
    private readonly object _gate = new();
    private GdiScreenCapture? _ready;
    private GdiScreenCapture? _busy;
    private int _write;
    private Thread? _thread;
    private volatile bool _stop;
    // While the window is still, the desktop behind it rarely changes and the page
    // costs a repaint of every transparent control on it, so the idle rate is
    // deliberately low. Moving the window raises it: that is when the frame has to
    // follow.
    private int _idleMs = 160;
    private int _frames;

    public DesktopBackdrop(IntPtr hwnd, Action repaint)
    {
        _hwnd = hwnd;
        _repaint = repaint;
    }

    public int Frames => _frames;

    /// Whether the self-exclusion is in force. Without it the capture returns the
    /// app's own window, which would feed the glass a picture of itself.
    public bool ExclusionOk { get; private set; }

    public bool Start()
    {
        ExclusionOk = SetWindowDisplayAffinity(_hwnd, WdaExcludeFromCapture);
        if (_thread is not null) return ExclusionOk;

        _thread = new Thread(Loop) { IsBackground = true, Name = "desktop-backdrop" };
        _thread.Start();
        return ExclusionOk;
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(800);
        _thread = null;
        foreach (var b in _buffers) b.Dispose();
        PageGlass.ClearDesktop();
    }

    /// Claims the newest captured frame for the UI thread. Must be paired with
    /// Release once its pixels have been uploaded.
    public bool TryTake(out GdiScreenCapture capture)
    {
        lock (_gate)
        {
            if (_ready is null)
            {
                capture = null!;
                return false;
            }
            capture = _ready;
            _busy = _ready;
            _ready = null;
            return true;
        }
    }

    public void Release(GdiScreenCapture capture)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_busy, capture)) _busy = null;
        }
    }

    private void Loop()
    {
        try
        {
            Run();
        }
        catch (Exception ex)
        {
            // A dead capture thread is invisible from the outside: the page simply
            // stops updating, and stops being repainted at all once the window is
            // resized. Leave a trace of it.
            PageGlass.Note($"capture: thread died: {ex}");
            throw;
        }
    }

    private void Run()
    {
        var previous = default(Rect);
        var previousOrigin = default(Point);

        while (!_stop)
        {
            if (!GetClientRect(_hwnd, out var rect) || rect.Right - rect.Left < 8)
            {
                Thread.Sleep(200);
                continue;
            }
            var origin = new Point();
            ClientToScreen(_hwnd, ref origin);

            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            bool moved = w != previous.Right - previous.Left
                      || h != previous.Bottom - previous.Top
                      || origin.X != previousOrigin.X
                      || origin.Y != previousOrigin.Y;
            previous = rect;
            previousOrigin = origin;

            GdiScreenCapture target;
            lock (_gate)
            {
                target = _buffers[_write];
                if (ReferenceEquals(target, _ready) || ReferenceEquals(target, _busy))
                {
                    target = null!;
                }
            }
            if (target is null)
            {
                Thread.Sleep(8);
                continue;
            }

            if (!target.Ensure(w, h) || !target.GrabRegion(origin.X, origin.Y))
            {
                Thread.Sleep(200);
                continue;
            }

            lock (_gate)
            {
                _ready = target;
                _write ^= 1;
            }
            _frames++;
            _repaint();

            // While the window is being moved the frame has to follow it; when it
            // is still, the desktop behind it rarely changes.
            Thread.Sleep(moved ? 8 : _idleMs);
        }
    }

    public void Dispose() => Stop();
}
