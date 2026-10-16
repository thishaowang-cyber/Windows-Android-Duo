using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WindowsDuo.App;

// Visual language taken from the reference implementation:
//
//   .glass        { border-radius: 60px; box-shadow: 0 4px 24px rgba(0,0,0,.18); }
//   .glass::before{ background-color: rgba(255,255,255,0.06); }
//   .glass::after { backdrop-filter: <blur + displacement>; }
//   body          { background: <a vivid photo>, cover; }
//
// The glass itself is not painted here any more - it is a real bevelled slab
// drawn by Shaders/Glass.hlsl (see PageGlass), because the rim highlight and the
// distortion near the edge both come from one surface normal and cannot be
// faked with stacked pen rings. What stays here is the picture the slab sits on:
// the backdrop, the chrome, and the text.
internal static class Theme
{
    public static readonly Color Accent = Color.FromArgb(139, 124, 247);      // #8b7cf7
    public static readonly Color Text = Color.FromArgb(255, 255, 255);
    public static readonly Color TextDim = Color.FromArgb(196, 198, 214);
    public static readonly Color TextFaint = Color.FromArgb(150, 153, 172);

    // The dark translucent chrome from the reference's control panel. Over the
    // vivid backdrop, rgba(22,22,26,.72) composites to about this, and a solid
    // colour lets every child stay opaque — which is what keeps the repaint
    // chain from ghosting sibling controls.
    public static readonly Color ChromeSolid = Color.FromArgb(33, 31, 40);
    public static readonly Color ChromeBorder = Color.FromArgb(22, 255, 255, 255);

    public const int GlassRadius = 60;

    private static readonly Font UiFont = new("Segoe UI Semibold", 9f);
    private static readonly Font UiFontSmall = new("Segoe UI", 8.5f);
    private static readonly Font UiFontBold = new("Segoe UI", 10f, FontStyle.Bold);
    private static readonly Font UiFontTitle = new("Segoe UI Semibold", 20f);

    public static Font FontUi => UiFont;
    public static Font FontSmall => UiFontSmall;
    public static Font FontBold => UiFontBold;
    public static Font FontTitle => UiFontTitle;

    private static Bitmap? _backdrop;

    /// Every geometry constant is expressed at 96 dpi and multiplied by this, so
    /// the glass looks identical on a 100% and a 150% display instead of its
    /// radii and rims shrinking relative to the window.
    public static float ScaleFor(Control c) => Math.Max(0.5f, c.DeviceDpi / 96f);

    public static int Scaled(int value, float scale) => Math.Max(1, (int)Math.Round(value * scale));

    public static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// The window backdrop. A vivid, photographic-feeling colour field — the
    /// glass has nothing to reveal against a flat dark fill.
    public static Bitmap Backdrop(Size size)
    {
        if (_backdrop is not null && _backdrop.Width == size.Width && _backdrop.Height == size.Height)
            return _backdrop;

        _backdrop?.Dispose();

        var bmp = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height));
        using (var g = Graphics.FromImage(bmp)) PaintField(g, bmp.Size);
        _backdrop = bmp;
        return _backdrop;
    }

    /// The colour field, at any size. Shared so that anything meant to look like a
    /// piece of it - the buttons - is painted from the same recipe rather than from
    /// colours copied out of it, which would drift the moment the field changed.
    private static void PaintField(Graphics g, Size size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var baseFill = new LinearGradientBrush(new Rectangle(0, 0, size.Width, size.Height),
                   Color.FromArgb(46, 26, 74), Color.FromArgb(16, 20, 56), 115f))
            g.FillRectangle(baseFill, 0, 0, size.Width, size.Height);

        Blob(g, size, 0.10f, 0.06f, 0.78f, Color.FromArgb(220, 96, 176, 247));
        Blob(g, size, 0.78f, 0.16f, 0.62f, Color.FromArgb(205, 224, 78, 150));
        Blob(g, size, 0.52f, 0.86f, 0.70f, Color.FromArgb(190, 74, 200, 214));
        Blob(g, size, 0.94f, 0.74f, 0.52f, Color.FromArgb(170, 246, 168, 92));
        Blob(g, size, 0.30f, 0.52f, 0.46f, Color.FromArgb(140, 126, 110, 246));
    }

    private static Color[]? _slabColours;

    /// Three stops read off the field, left to right, cached. A button painted with
    /// these looks like a slice of the picture the preview shows.
    public static Color[] SlabColours()
    {
        if (_slabColours is not null) return _slabColours;
        using var strip = new Bitmap(64, 16);
        using (var g = Graphics.FromImage(strip)) PaintField(g, strip.Size);
        _slabColours = new[] { strip.GetPixel(4, 8), strip.GetPixel(32, 8), strip.GetPixel(59, 8) };
        return _slabColours;
    }

    /// The backdrop as RGBA bytes, for the shader that refracts it.
    public static byte[] BackdropRgba(Size size)
    {
        var bmp = Backdrop(size);
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = bmp.Width * 4;
            var bytes = new byte[stride * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
            {
                int row = y * stride;
                Marshal.Copy(data.Scan0 + y * data.Stride, bytes, row, stride);
                for (int x = 0; x < stride; x += 4)
                    (bytes[row + x], bytes[row + x + 2]) = (bytes[row + x + 2], bytes[row + x]);
            }
            return bytes;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static void Blob(Graphics g, Size size, float cx, float cy, float scale, Color colour)
    {
        float radius = Math.Max(size.Width, size.Height) * scale;
        var bounds = new RectangleF(size.Width * cx - radius, size.Height * cy - radius, radius * 2, radius * 2);
        using var path = new GraphicsPath();
        path.AddEllipse(bounds);
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = colour,
            SurroundColors = new[] { Color.FromArgb(0, colour) },
        };
        g.FillPath(brush, path);
    }

    /// Paints what is behind `control`: the page's glass over the desktop, or the
    /// generated field while no desktop frame has arrived. Used by the containers
    /// that sit directly on the window, and to fill the few pixels outside a
    /// rounded surface — an approximated colour there left a dark halo around the
    /// big-radius panels that read as a black frame.
    public static void PaintBackdropFor(Control control, Graphics g)
    {
        var form = control.FindForm();
        if (form is null) return;

        // One path: the glass is drawn either way, over the desktop when the
        // capture is running and over the generated field when it is not. Falling
        // back to a flat field instead would mean no glass at all in that state,
        // which is what the shot harness renders.
        PageGlass.Paint(control, g, ScaleFor(control));
    }

    /// The generated field, cropped to `control`. This is the last resort before
    /// nothing is drawn at all: an unpainted area of a resized window stays black,
    /// and on a startup full of resizes that black is what the user ends up
    /// looking at.
    public static void PaintGeneratedFor(Control control, Graphics g)
    {
        var form = control.FindForm();
        if (form is null) return;
        var origin = form.PointToClient(control.PointToScreen(Point.Empty));
        var bmp = Backdrop(form.ClientSize);
        var state = g.Save();
        g.TranslateTransform(-origin.X, -origin.Y);
        g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
        g.Restore(state);
    }

}
