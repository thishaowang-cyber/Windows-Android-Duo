using System.Drawing;
using System.Drawing.Imaging;

namespace WindowsDuo.Core;

// GDI screen grab. Slower than Desktop Duplication and CPU-bound, but it keeps
// working in the cases where duplication hands back a blank scanout.
public static class ScreenGrabber
{
    public static (byte[] rgba, int w, int h) Capture()
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);

        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, px, y * w * 4, w * 4);
            for (int i = 0; i < px.Length; i += 4)
                (px[i], px[i + 2]) = (px[i + 2], px[i]);
            return (px, w, h);
        }
        finally { bmp.UnlockBits(data); }
    }
}
