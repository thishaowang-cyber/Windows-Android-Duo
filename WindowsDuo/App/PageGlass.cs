using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using WindowsDuo.Core;

namespace WindowsDuo.App;

// The page's glass, drawn in D3D and handed back as a bitmap for the controls to
// blit.
//
// A slab is a rounded rectangle with a chamfer running around the inside of its
// rim. The chamfer's slope bends the view of the backdrop behind it (Snell) and
// catches the light, so the distortion along the edge and the rim highlight are
// the same surface seen two ways. See PSLiquidGlass in Shaders/Glass.hlsl.
//
// WinForms has no backdrop-filter, so a slab is rendered offscreen against the
// backdrop it sits on - which is what keeps its edges lined up with the backdrop
// around them instead of showing a seam. The backdrop is either the generated
// colour field or, once DesktopBackdrop starts feeding it, the desktop behind the
// window.
//
// One slot per surface is kept and drawn into again, rather than a bitmap per
// frame: the live backdrop changes on every captured frame, and allocating a few
// megabytes each time would put the whole page in the large object heap.
internal static class PageGlass
{
    /// A slab's proportions, at 96 dpi. The bezel is the chamfer, i.e. how far
    /// the distortion reaches inwards, and the tint is what the glass is coloured
    /// towards where it covers the backdrop.
    public readonly record struct Style(
        float Bezel, float Thickness, float Ior, float Blur, float Specular, float Tint, float Shadow, Vector3 TintColour);

    private static readonly Vector3 White = new(1f, 1f, 1f);

    /// The block that floats on the page: clear glass, barely tinted.
    public static readonly Style Block = new(40f, 40f, 2.6f, 2.5f, 0.22f, 0.04f, 0.5f, White);

    /// The block is a page-scale one, far larger than the reference's demo box,
    /// so its radius is its own constant rather than the chrome one.
    public const int BlockRadius = 90;

    /// The window itself: a narrow, clear rim around the whole page, so what is
    /// seen through it is the desktop rather than a picture of our own.
    public static readonly Style Page = new(16f, 22f, 2.2f, 0f, 0.5f, 0f, 0f, White);

    private sealed class Slot
    {
        public Bitmap? Bitmap;
        public byte[] Pixels = Array.Empty<byte>();
        public int W, H, Frame, Scale, Layout;
    }

    // The block is drawn inside the page, so one bitmap covers both.
    private static readonly Slot PageSlot = new();
    private static GlassRenderer? _renderer;
    private static Control? _blockControl;
    private static Size _uploaded;
    private static bool _desktop;
    private static int _frame;

    /// True once desktop frames are arriving, i.e. the page is showing the real
    /// desktop rather than the generated field.
    public static bool DesktopLive => _desktop;

    /// Bumped for every backdrop change, so a control that caches its own layer
    /// can tell that what is behind it moved.
    public static int Frame => _frame;

    /// Forces every slab to be drawn again, for timing a frame.
    public static void InvalidateSlabs() => _frame++;

    /// Diagnostics. A window that ends up black has to be diagnosed from the
    /// decisions the page took rather than from a screenshot, because a window
    /// that reads the desktop behind it cannot itself be captured.
    ///
    /// The capture runs on its own thread, so the log is written through one
    /// guarded writer: two threads appending with File.AppendAllText collide and
    /// the loser loses the whole log.
    private static StreamWriter? _traceWriter;
    private static readonly object TraceGate = new();

    internal static bool Tracing => _traceWriter is not null;

    private static string? _dumpDirectory;

    public static void StartTrace(string path)
    {
        lock (TraceGate)
        {
            try
            {
                _traceWriter?.Dispose();
                _traceWriter = new StreamWriter(path, append: false) { AutoFlush = true };
                _traceWriter.WriteLine($"trace {DateTime.Now:HH:mm:ss.fff}");
                _dumpDirectory = Path.GetDirectoryName(path);
                _dumped = 0;
            }
            catch { _traceWriter = null; }
        }
    }

    private static int _dumped;

    // Dumps the backdrop the capture produced and the page that was drawn from it,
    // once each. The point of the log is that neither can be screenshotted, so the
    // images have to come from inside the process.
    private static void Dump(string name, byte[] rgba, int w, int h)
    {
        var dir = _dumpDirectory;
        if (dir is null || _dumped >= 2) return;
        try
        {
            var path = Path.Combine(dir, name);
            Verify.PngWriter.WriteRgba(path, w, h, rgba);
            Note($"dump: {path} {w}x{h}");
        }
        catch (Exception ex) { Note($"dump failed: {ex.Message}"); }
    }

    internal static void Note(string message)
    {
        if (!Tracing) return;
        lock (TraceGate)
        {
            try { _traceWriter?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}"); }
            catch { _traceWriter = null; }
        }
    }

    private static int _notedWidth, _frameNotes, _renders;
    private static readonly HashSet<string> NotedControls = new();

    // Frame notes are throttled: frames arrive ~10 times a second and the log only
    // needs to show that they are arriving and at what size.
    internal static void NoteFrame(int w, int h)
    {
        if (!Tracing) return;
        if (w != _notedWidth) { _notedWidth = w; Note($"capture: frames now {w}x{h}"); return; }
        if (++_frameNotes % 50 == 0) Note($"capture: {_frameNotes} frames at {w}x{h}");
    }

    private static void NoteControl(Control control, string what)
    {
        if (!Tracing) return;
        if (NotedControls.Add(control.GetType().Name))
            Note($"paint: {control.GetType().Name} {what}");
    }

    /// One desktop frame, BGRA, straight out of the capture buffer.
    public static void SetDesktopFrame(IntPtr bgra, int w, int h)
    {
        if (w < 8 || h < 8) return;
        _renderer ??= new GlassRenderer();
        _renderer.SetBackdropPointer(bgra, w, h);
        if (_uploaded != new Size(w, h)) Note($"backdrop: now {w}x{h} (was {_uploaded})");
        _uploaded = new Size(w, h);
        _desktop = true;
        _frame++;
        NoteFrame(w, h);

        if (Tracing && _dumped == 0)
        {
            _dumped = 1;
            // The capture is BGRA; the writer wants RGBA.
            var rgba = new byte[w * h * 4];
            Marshal.Copy(bgra, rgba, 0, rgba.Length);
            var words = MemoryMarshal.Cast<byte, uint>(rgba.AsSpan());
            for (int i = 0; i < words.Length; i++)
            {
                uint c = words[i];
                words[i] = (c & 0xFF00FF00u) | ((c & 0x00FF0000u) >> 16) | ((c & 0x000000FFu) << 16);
            }
            Dump("trace-backdrop.png", rgba, w, h);
        }
    }

    /// Drops back to the generated field, for when the capture is unavailable.
    public static void ClearDesktop()
    {
        if (!_desktop) return;
        _desktop = false;
        _uploaded = Size.Empty;
        _frame++;
    }

    /// The window's own corner radius, which is also the page slab's.
    public static int PageRadius(float scale) => Theme.Scaled(9, scale);

    /// Controls whose background is the page. Every captured frame has to reach
    /// them: a container that painted while the backdrop was not ready keeps that
    /// result until something invalidates it again, and a container is not
    /// invalidated by its parent being repainted. That is how a band of the window
    /// stays black for good.
    private static Control[] _surfaces = Array.Empty<Control>();

    public static void SetPageSurfaces(params Control[] controls) => _surfaces = controls;

    public static void InvalidateSurfaces()
    {
        foreach (var c in _surfaces)
            if (c is { IsDisposed: false, IsHandleCreated: true })
                c.Invalidate();
    }

    /// The control that draws the block, i.e. the block's geometry on the page.
    /// Setting it once is all the renderer needs; the block is drawn as part of
    /// the page, so no control has to know how it is made.
    public static void SetBlockControl(Control? control) => _blockControl = control;

    /// Blits what `control` covers of the page: the window's glass, the block on
    /// it and the desktop seen through both. One page is rendered for the whole
    /// client and cropped per control, because a page cut per control would draw
    /// its rim once per control instead of once around the window.
    public static void Paint(Control control, Graphics g, float scale)
    {
        var form = control.FindForm();
        if (form is null) return;
        var area = new Rectangle(Point.Empty, form.ClientSize);
        if (area.Width < 8 || area.Height < 8) return;

        var crop = form.PointToClient(control.PointToScreen(Point.Empty));
        int scaleKey = (int)Math.Round(scale * 100);

        // Only the part of this control that is actually on screen is drawn. A
        // transparent child inside a card asks the card for its background, and
        // without this each of the forty-odd labels and sliders would have the
        // whole card blitted for it.
        var visible = Rectangle.Intersect(control.ClientRectangle, Rectangle.Round(g.VisibleClipBounds));
        if (visible.Width <= 0 || visible.Height <= 0) return;
        // Everything the slabs' geometry depends on: if any of it moved, the page
        // has to be drawn again. The cards resize themselves to their content, so
        // a card growing taller changes this too.
        int layout = 17;
        var block = _blockControl is null ? Rectangle.Empty : RectOf(form, _blockControl);
        layout = layout * 31 + block.GetHashCode();

        bool geometry = PageSlot.Bitmap is null
            || PageSlot.W != area.Width || PageSlot.H != area.Height
            || PageSlot.Scale != scaleKey || PageSlot.Layout != layout;
        bool frame = PageSlot.Frame != _frame;

        // Who asks for the page, with what rectangle, and whether they get a blit
        // or a fresh draw. One line per control type: this is what shows which
        // area of the window is being painted and which is going unpainted.
        NoteControl(control, $"area={area.Size} crop={crop} redraw={geometry || frame} desktop={_desktop}");

        if (!geometry && !frame)
        {
            Blit(g, PageSlot.Bitmap!, Offset(crop, visible.Location), visible.Size, visible.Location);
            return;
        }

        if (!UploadBackdrop(form.ClientSize))
        {
            // The capture has not caught up with the new size yet. Painting
            // nothing would leave the freshly grown parts of the window black, so
            // the last page is stretched over the new area - or, before the first
            // page exists at all, the generated field stands in.
            Note($"paint: backdrop not ready (uploaded={_uploaded} client={form.ClientSize} havePage={PageSlot.Bitmap is not null})");
            if (PageSlot.Bitmap is not null)
            {
                var mode = g.CompositingMode;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(PageSlot.Bitmap,
                    new Rectangle(visible.X - crop.X, visible.Y - crop.Y, visible.Width, visible.Height),
                    new Rectangle(visible.X, visible.Y, visible.Width, visible.Height),
                    GraphicsUnit.Pixel);
                g.CompositingMode = mode;
            }
            else
                Theme.PaintGeneratedFor(control, g);
            return;
        }

        if (PageSlot.Bitmap is null || PageSlot.W != area.Width || PageSlot.H != area.Height)
        {
            PageSlot.Bitmap?.Dispose();
            PageSlot.Bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
            PageSlot.Pixels = new byte[area.Width * area.Height * 4];
            PageSlot.W = area.Width; PageSlot.H = area.Height;
        }

        var p = LiquidParams.Create();
        p.ScreenSize = new Vector2(area.Width, area.Height);
        p.ScreenOrigin = Vector2.Zero;
        p.Light = Vector2.Normalize(new Vector2(0.5f, -0.7f));
        p.BitmapSize = new Vector2(area.Width, area.Height);

        int count = 0;
        Place(ref p, count++, area, Page, PageRadius(scale), scale);
        if (!block.IsEmpty)
            Place(ref p, count++, block, Block, Theme.Scaled(BlockRadius, scale), scale);

        p.Meta = new Vector4(scale, 0, 0, count);
        _renderer!.RenderSlab(PageSlot.Pixels, area.Width, area.Height, p, false);
        if (Tracing && ++_renders % 25 == 0) Note($"render: {_renders} page renders");
        if (Tracing && _dumped == 1) { _dumped = 2; Dump("trace-page.png", PageSlot.Pixels, area.Width, area.Height); }
        CopyInto(PageSlot.Bitmap!, PageSlot.Pixels, area.Width, area.Height);
        PageSlot.Scale = scaleKey;
        PageSlot.Layout = layout;
        PageSlot.Frame = _frame;
        Blit(g, PageSlot.Bitmap!, Offset(crop, visible.Location), visible.Size, visible.Location);
    }

    /// Draws the part of the page `control` covers. The source rectangle matters:
    /// blitting the whole bitmap and letting the clip sort it out still walks the
    /// whole bitmap, several times per frame once every container asks for it.
    private static Point Offset(Point a, Point b) => new(a.X + b.X, a.Y + b.Y);

    private static void Blit(Graphics g, Bitmap page, Point crop, Size size, Point at)
    {
        var src = new Rectangle(crop, size);
        var dst = new Rectangle(at, size);
        if (src.Right > page.Width || src.Bottom > page.Height) return;

        // SourceCopy, not the default SourceOver: the page is opaque, and blending
        // several million pixels per control per frame is most of what a frame
        // used to cost.
        var mode = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(page, dst, src, GraphicsUnit.Pixel);
        g.CompositingMode = mode;
    }

    /// Where a control sits in the window, which is the space the slabs are in.
    private static Rectangle RectOf(Form form, Control control)
    {
        if (!control.IsHandleCreated || control.IsDisposed) return Rectangle.Empty;
        return new Rectangle(form.PointToClient(control.PointToScreen(Point.Empty)), control.ClientSize);
    }

    private static void Place(ref LiquidParams p, int index, Rectangle r, Style style, int radius, float scale)
    {
        p.SetSlab(index,
            new Vector4(r.X + r.Width * 0.5f, r.Y + r.Height * 0.5f, r.Width * 0.5f, r.Height * 0.5f),
            new Vector4(radius, Theme.Scaled((int)style.Bezel, scale),
                Theme.Scaled((int)style.Thickness, scale), style.Ior),
            new Vector4(Theme.Scaled((int)style.Blur, scale), style.Specular, style.Tint, style.Shadow),
            new Vector4(style.TintColour, 0f));
    }

    private static bool UploadBackdrop(Size backdrop)
    {
        _renderer ??= new GlassRenderer();
        if (_uploaded == backdrop) return true;

        if (_desktop)
        {
            // The capture owns the texture; a mismatch means it has not caught up
            // with a resize yet, and the previous frame is still the best we have.
            return false;
        }

        var bytes = Theme.BackdropRgba(backdrop);
        if (bytes.Length == 0) return false;
        _renderer.SetBackdrop(bytes, backdrop.Width, backdrop.Height);
        _uploaded = backdrop;
        return true;
    }

    // RGBA out of the render target into a top-down BGRX GDI bitmap.
    private static void CopyInto(Bitmap bmp, byte[] rgba, int w, int h)
    {
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                Array.Copy(rgba, y * row.Length, row, 0, row.Length);
                var words = MemoryMarshal.Cast<byte, uint>(row.AsSpan());
                for (int x = 0; x < w; x++)
                {
                    uint c = words[x];
                    words[x] = (c & 0xFF00FF00u) | ((c & 0x00FF0000u) >> 16) | ((c & 0x000000FFu) << 16);
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
