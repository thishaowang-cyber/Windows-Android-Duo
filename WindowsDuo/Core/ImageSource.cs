using System.Drawing;
using System.Drawing.Imaging;

namespace WindowsDuo.Core;

public static class ImageSource
{
    /// Straight RGBA8 out of an in-memory bitmap (used for the built-in preview
    /// image, so the effect has something to show before a screenshot is loaded).
    public static (byte[] rgba, int w, int h) FromBitmap(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bgra = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * w * 4, w * 4);
            for (int i = 0; i < bgra.Length; i += 4)
                (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            return (bgra, w, h);
        }
        finally { bmp.UnlockBits(data); }
    }

    // Loads any image the GDI+ decoder understands and returns straight RGBA8.
    public static (byte[] rgba, int w, int h) Load(string path)
    {
        using var file = new Bitmap(path);
        int w = file.Width, h = file.Height;

        using var flat = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(flat))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImageUnscaled(file, 0, 0);
        }

        var data = flat.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bgra = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * w * 4, w * 4);

            // BGRA -> RGBA
            for (int i = 0; i < bgra.Length; i += 4)
                (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);

            return (bgra, w, h);
        }
        finally { flat.UnlockBits(data); }
    }
}
