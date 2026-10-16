using System.Drawing.Drawing2D;

namespace WindowsDuo.App;

// The hero surface: the page's glass slab, with the effect canvas as an opaque
// child drawn on top of it.
//
// The slab is rendered once into a layer keyed to this control's own size, DPI
// and position inside the window, then blitted. The padding is the band of glass
// that stays visible around the canvas, and it is deliberately wider than the
// slab's chamfer so the refraction ring is not hidden under the canvas.
public sealed class HeroPanel : Panel
{
    private float _scale = 1f;

    public int Radius { get; private set; } = PageGlass.BlockRadius;

    public HeroPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint, true);
        BackColor = Theme.ChromeSolid;
        Padding = new Padding(Theme.Scaled(64, 1f));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDpi();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyDpi();
    }

    private void ApplyDpi()
    {
        float s = Theme.ScaleFor(this);
        if (Math.Abs(s - _scale) < 0.01f) return;
        _scale = s;
        Radius = Theme.Scaled(PageGlass.BlockRadius, s);
        Padding = new Padding(Theme.Scaled(64, s));
        Invalidate(true);
    }

    // This control is where the block is, not what draws it: the block is part of
    // the page, so the page's glass is blitted here and cropped, and this only has
    // to hold the canvas child in place and give it room.
    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.PaintBackdropFor(this, e.Graphics);
    }
}

// A group of controls. There is no card: the group has no surface of its own, so
// the page's glass shows through and only the text and the controls are drawn on
// it. It exists for the layout - one scrollable column of groups.
public sealed class GlassCard : Panel
{
    public GlassCard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint
               | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Padding = new Padding(16, 14, 16, 14);
        Font = Theme.FontUi;
    }
}

// Whatever is written on the glass has to let it show through. WinForms only
// allows a transparent BackColor on a control that opts in, and the opt-in is
// protected, so these exist to opt in.
public sealed class GlassLabel : Label
{
    public GlassLabel()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }
}

/// A hint that really wraps. A Label with a transparent background breaks the line
/// and then never draws the rest of it, which is exactly the case here: the
/// sentence lost its second half in the narrowest column.
public sealed class GlassHint : Control
{
    public GlassHint()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint
               | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = Theme.FontSmall;
        ForeColor = Theme.TextFaint;
        TabStop = false;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.PaintBackdropFor(this, e.Graphics);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
            TextFormatFlags.WordBreak | TextFormatFlags.Top | TextFormatFlags.NoPadding);
    }
}

public sealed class GlassRow : Panel
{
    public GlassRow()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }
}

public sealed class GlassCheck : CheckBox
{
    public GlassCheck()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }
}

// Pill button. The primary action takes the reference's accent pill
// (`.switch-btn`: var(--accent) with a soft violet glow); the rest are slightly
// lighter chrome chips.
// A pill drawn over the glass.
//
// Derived from Control rather than Button on purpose: ButtonBase does painting of
// its own outside OnPaint - a flat border drawn along the control's rectangular
// bounds, plus hover/pressed fills - and around a rounded pill inside a square
// control that showed up as a frame around the button.
public sealed class GlassButton : Control
{
    private bool _hover, _down;

    public bool Prominent { get; set; }

    public GlassButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint
               | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = Theme.FontUi;
        Height = 36;
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _down = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _down = true;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool click = _down && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location);
        _down = false;
        Invalidate();
        if (click) OnClick(EventArgs.Empty);
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.Rounded(r, Height / 2);

        // The accent ring is drawn inside the pill: a glow outside it is clipped by
        // the control's own rectangle, which left a smear of cut-off arcs at the
        // edges rather than a halo.
        if (Prominent && Enabled)
        {
            using var inner = Theme.Rounded(Rectangle.Inflate(r, -2, -2), (Height - 4) / 2);
            using var pen = new Pen(Color.FromArgb(150, Theme.Accent), 2f);
            e.Graphics.DrawPath(pen, inner);
        }

        // Taken from the field, then deepened: the field's own colours are pale, and
        // white text on them does not hold up.
        var stops = Theme.SlabColours();
        int alpha = Enabled ? 255 : 150;
        Color A(int i) => Deepen(stops[i], alpha);
        using (var fill = new LinearGradientBrush(r, A(0), A(2), 20f))
        {
            fill.InterpolationColors = new ColorBlend(3)
            {
                Colors = new[] { A(0), A(1), A(2) },
                Positions = new[] { 0f, 0.5f, 1f },
            };
            e.Graphics.FillPath(fill, path);
        }

        int rim = _down ? 150 : _hover ? 120 : 90;
        using (var pen = new Pen(Color.FromArgb(rim, 255, 255, 255), 1f))
            e.Graphics.DrawPath(pen, path);

        Color text = Theme.Text;
        if (!Enabled) text = Color.FromArgb(140, text);
        TextRenderer.DrawText(e.Graphics, Text, Font, r, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private const float Depth = 0.62f;

    private static Color Deepen(Color c, int alpha) => Color.FromArgb(
        alpha, (int)(c.R * Depth), (int)(c.G * Depth), (int)(c.B * Depth));
}

// Slider drawn by hand: the stock TrackBar refuses a transparent background and
// would punch an opaque rectangle through the panel.
public sealed class GlassSlider : Control
{
    private const int Pad = 9;
    private const int Head = 18;   // room for the caption and the value above the track
    private int _min, _max = 100, _value;
    private bool _dragging;

    public event EventHandler? ValueChanged;

    /// The slider draws its own caption and value: a row made of a panel and two
    /// labels costs four controls to repaint every time the glass behind it moves,
    /// and this one costs one.
    public string Caption { get; set; } = "";
    public Func<int, string> Format { get; set; } = v => v.ToString();

    public GlassSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint
               | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Height = 52;
        Cursor = Cursors.Hand;
    }

    public int Minimum { get => _min; set { _min = value; Invalidate(); } }

    public int Maximum { get => _max; set { _max = value; Invalidate(); } }

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, _min, _max);
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _dragging = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e) => _dragging = false;

    protected override void OnMouseCaptureChanged(EventArgs e) => _dragging = false;

    private void SetFromX(int x)
    {
        float span = Math.Max(1, Width - 2 * Pad);
        float t = Math.Clamp((x - Pad) / span, 0f, 1f);
        Value = _min + (int)Math.Round(t * (_max - _min));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        TextRenderer.DrawText(e.Graphics, Caption, Theme.FontUi,
            new System.Drawing.Point(0, 0), Theme.TextDim, TextFormatFlags.NoPadding);
        string value = Format(_value);
        var valueSize = TextRenderer.MeasureText(e.Graphics, value, Theme.FontUi);
        TextRenderer.DrawText(e.Graphics, value, Theme.FontUi,
            new System.Drawing.Point(Math.Max(0, Width - valueSize.Width), 0), Theme.Text,
            TextFormatFlags.NoPadding);

        int mid = Head + (Height - Head) / 2;
        int left = Pad, right = Width - Pad;
        float t = _max > _min ? (_value - _min) / (float)(_max - _min) : 0f;
        int thumbX = (int)(left + t * (right - left));

        using (var track = new Pen(Color.FromArgb(46, 255, 255, 255), 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            e.Graphics.DrawLine(track, left, mid, right, mid);
        if (thumbX > left)
            using (var fill = new Pen(Theme.Accent, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                e.Graphics.DrawLine(fill, left, mid, thumbX, mid);

        const int r = 7;
        var thumb = new Rectangle(thumbX - r, mid - r, r * 2, r * 2);
        using (var fill = new SolidBrush(Color.FromArgb(248, 249, 253)))
            e.Graphics.FillEllipse(fill, thumb);
        using (var pen = new Pen(Color.FromArgb(70, 0, 0, 0), 1f))
            e.Graphics.DrawEllipse(pen, thumb);
    }
}

// Opaque containers that reproduce the form's backdrop in their own rectangle,
// for the few containers that sit directly on the window.
public static class Backdrop
{
    public static void Paint(Control control, PaintEventArgs e) => Theme.PaintBackdropFor(control, e.Graphics);
}

public sealed class BackdropPanel : Panel
{
    public BackdropPanel() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    protected override void OnPaintBackground(PaintEventArgs e) => Backdrop.Paint(this, e);
}

public sealed class BackdropTable : TableLayoutPanel
{
    public BackdropTable() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    protected override void OnPaintBackground(PaintEventArgs e) => Backdrop.Paint(this, e);
}

public sealed class BackdropFlow : FlowLayoutPanel
{
    public BackdropFlow() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    protected override void OnPaintBackground(PaintEventArgs e) => Backdrop.Paint(this, e);
}
