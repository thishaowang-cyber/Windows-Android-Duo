using System.Drawing.Drawing2D;

namespace WindowsDuo.App;

// Round window controls in the top-right corner. The shape borrows the macOS
// coloured-dot look, but the three actions stay the Windows ones — minimise,
// maximise/restore, close — and the glyphs are always visible rather than only
// on hover.
public sealed class TrafficLights : Control
{
    private const int DotSize = 19;   // at 96 dpi
    private const int DotGap = 10;

    private int _hoverDot = -1;

    public event Action? CloseRequested;
    public event Action? MinimizeRequested;
    public event Action? MaximizeRequested;

    /// Drives the middle glyph: a square when restored, overlapping squares when
    /// maximised.
    public bool IsMaximized { get; set; }

    public TrafficLights()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint, true);
        BackColor = Theme.ChromeSolid;
        Cursor = Cursors.Hand;
        Size = new Size(90, 26);
        TabStop = false;
    }

    private int Dot => Theme.Scaled(DotSize, Theme.ScaleFor(this));
    private int Gap => Theme.Scaled(DotGap, Theme.ScaleFor(this));

    /// Called after the form has finished its own DPI scaling pass, which would
    /// otherwise scale the bounds a second time and make the dots oversized.
    public void ApplyDpi()
    {
        int d = Dot, g = Gap;
        Size = new Size(3 * d + 2 * g, d + 6);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // The dots sit on the page, not on a plate of their own: whatever is
        // behind this control has to be drawn first, or its own fill shows up as
        // a dark rectangle over the desktop.
        Theme.PaintBackdropFor(this, e.Graphics);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int d = Dot, g = Gap;
        int y = Math.Max(0, (Height - d) / 2);

        var colours = new[]
        {
            Color.FromArgb(255, 189, 46),  // minimise
            Color.FromArgb(39, 201, 63),   // maximise / restore
            Color.FromArgb(255, 95, 87),   // close
        };

        for (int i = 0; i < 3; i++)
        {
            var rect = new Rectangle(i * (d + g), y, d, d);
            using (var fill = new SolidBrush(_hoverDot == i ? Lighten(colours[i]) : colours[i]))
                e.Graphics.FillEllipse(fill, rect);
            using (var rim = new Pen(Color.FromArgb(46, 0, 0, 0), 1f))
                e.Graphics.DrawEllipse(rim, rect);
            DrawGlyph(e.Graphics, rect, i, colours[i]);
        }
    }

    private void DrawGlyph(Graphics g, Rectangle dot, int index, Color colour)
    {
        float s = dot.Width;
        float cx = dot.Left + s / 2f, cy = dot.Top + s / 2f;
        float arm = s * 0.24f;
        float stroke = Math.Max(1.4f, s * 0.085f);
        using var ink = new Pen(Color.FromArgb(150, 0, 0, 0), stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        switch (index)
        {
            case 0: // minimise
                g.DrawLine(ink, cx - arm, cy, cx + arm, cy);
                break;

            case 1: // maximise, or restore when already maximised
                if (IsMaximized)
                {
                    var back = new RectangleF(cx - arm, cy - arm * 0.75f, arm * 1.7f, arm * 1.7f);
                    var front = new RectangleF(cx - arm * 0.75f, cy - arm * 0.15f, arm * 1.7f, arm * 1.7f);
                    g.DrawRectangle(ink, back.X, back.Y, back.Width, back.Height);
                    // The front square is punched out of the dot, so it has to be
                    // filled with the dot's own colour — not a fixed one.
                    using (var bg = new SolidBrush(colour))
                        g.FillRectangle(bg, front);
                    g.DrawRectangle(ink, front.X, front.Y, front.Width, front.Height);
                }
                else
                {
                    var box = new RectangleF(cx - arm * 0.85f, cy - arm * 0.85f, arm * 1.7f, arm * 1.7f);
                    g.DrawRectangle(ink, box.X, box.Y, box.Width, box.Height);
                }
                break;

            default: // close
                g.DrawLine(ink, cx - arm * 0.9f, cy - arm * 0.9f, cx + arm * 0.9f, cy + arm * 0.9f);
                g.DrawLine(ink, cx + arm * 0.9f, cy - arm * 0.9f, cx - arm * 0.9f, cy + arm * 0.9f);
                break;
        }
    }

    private static Color Lighten(Color c) =>
        Color.FromArgb(c.A, Math.Min(255, c.R + 24), Math.Min(255, c.G + 24), Math.Min(255, c.B + 24));

    private int DotAt(Point p)
    {
        int d = Dot, g = Gap;
        if (p.Y < 0 || p.Y > Height) return -1;
        for (int i = 0; i < 3; i++)
        {
            int x = i * (d + g);
            if (p.X >= x && p.X <= x + d) return i;
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int dot = DotAt(e.Location);
        if (dot != _hoverDot)
        {
            _hoverDot = dot;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoverDot = -1;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        switch (DotAt(e.Location))
        {
            case 0: MinimizeRequested?.Invoke(); break;
            case 1: MaximizeRequested?.Invoke(); break;
            case 2: CloseRequested?.Invoke(); break;
        }
    }
}
