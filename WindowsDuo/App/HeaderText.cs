namespace WindowsDuo.App;

// The header text lives in its own control rather than in the form's OnPaint:
// with the window backed by the DWM glass, the caption band the app paints into
// is overpainted, so anything drawn there disappears. Child controls paint above
// that layer. It fills nothing, so the glass stays visible behind the text.
public sealed class HeaderText : Control
{
    public HeaderText()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint
               | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float s = Theme.ScaleFor(this);
        TextRenderer.DrawText(e.Graphics, "Windows Duo", Theme.FontTitle,
            new System.Drawing.Point(0, Theme.Scaled(6, s)), Theme.Text);
        TextRenderer.DrawText(e.Graphics, "随屏幕开合展开的悬浮玻璃桌面效果", Theme.FontSmall,
            new System.Drawing.Point(Theme.Scaled(2, s), Theme.Scaled(40, s)), Theme.TextDim);
    }
}
