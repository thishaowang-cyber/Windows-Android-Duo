using System.Runtime.InteropServices;

namespace WindowsDuo.Core;

// Fast GDI screen capture: one persistent DIB section that BitBlt writes into
// directly, so a frame costs exactly one BitBlt and no managed allocations.
//
// The dominant cost of GDI capture is the GPU -> system-memory readback of the
// whole framebuffer (~38 ms at 2560x1600 on this machine), which is inherent to
// the API; everything else here is arranged so that no further copying happens
// on top of it. Pixels are BGRA, matching the desktop format — the shader
// swizzles, so the CPU never touches individual pixels.
public sealed class GdiScreenCapture : IDisposable
{
    private const int SrcCopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(
        IntPtr hDc, ref BitmapInfo bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(
        IntPtr dst, int x, int y, int w, int h, IntPtr src, int srcX, int srcY, int rop);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(
        IntPtr dst, int x, int y, int w, int h, IntPtr src, int srcX, int srcY, int srcW, int srcH, int rop);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    // STRETCH_HALFTONE averages source pixels when shrinking (good quality);
    // STRETCH_COLORONCOLOR just drops pixels (cheapest).
    public const int StretchHalftone = 4;
    public const int StretchColorOnColor = 3;

    private IntPtr _screenDc, _memDc, _dib, _previousDib;

    /// BGRA pixel buffer, top-down (first row is the top row of the screen).
    public IntPtr Bits { get; private set; }
    /// Size of the captured buffer — smaller than the screen when Scale < 1.
    public int Width { get; private set; }
    public int Height { get; private set; }
    /// Screen size being read from.
    public int ScreenWidth { get; private set; }
    public int ScreenHeight { get; private set; }
    public float Scale { get; private set; } = 1f;
    public bool Ready => _dib != IntPtr.Zero;
    public int RowPitch => Width * 4;

    public bool Ensure(int screenW, int screenH, float scale = 1f, int stretchMode = StretchHalftone)
    {
        int w = Math.Max(1, (int)MathF.Round(screenW * scale));
        int h = Math.Max(1, (int)MathF.Round(screenH * scale));
        if (Ready && w == Width && h == Height) { Scale = scale; return true; }
        Release();

        _screenDc = GetDC(IntPtr.Zero);
        if (_screenDc == IntPtr.Zero) return false;
        _memDc = CreateCompatibleDC(_screenDc);
        if (_memDc == IntPtr.Zero) { Release(); return false; }

        var bmi = new BitmapInfo();
        bmi.Header.Size = Marshal.SizeOf<BitmapInfoHeader>();
        bmi.Header.Width = w;
        bmi.Header.Height = -h;      // negative height = top-down rows
        bmi.Header.Planes = 1;
        bmi.Header.BitCount = 32;
        bmi.Header.Compression = 0;  // BI_RGB

        _dib = CreateDIBSection(_memDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero || bits == IntPtr.Zero) { Release(); return false; }

        _previousDib = SelectObject(_memDc, _dib);
        SetStretchBltMode(_memDc, stretchMode);
        Bits = bits;
        Width = w;
        Height = h;
        ScreenWidth = screenW;
        ScreenHeight = screenH;
        Scale = scale;
        return true;
    }

    public bool Grab()
    {
        if (!Ready) return false;
        if (Width == ScreenWidth && Height == ScreenHeight)
            return BitBlt(_memDc, 0, 0, Width, Height, _screenDc, 0, 0, SrcCopy);
        return StretchBlt(_memDc, 0, 0, Width, Height, _screenDc, 0, 0, ScreenWidth, ScreenHeight, SrcCopy);
    }

    /// Grabs just one region of the screen into the buffer, which is then sized
    /// to the region. The readback dominates the cost, so capturing a window-sized
    /// rectangle costs a fraction of a full screen.
    public bool GrabRegion(int screenX, int screenY)
    {
        if (!Ready) return false;
        return BitBlt(_memDc, 0, 0, Width, Height, _screenDc, screenX, screenY, SrcCopy);
    }

    private void Release()
    {
        if (_memDc != IntPtr.Zero && _previousDib != IntPtr.Zero)
            SelectObject(_memDc, _previousDib);
        _previousDib = IntPtr.Zero;
        if (_dib != IntPtr.Zero) { DeleteObject(_dib); _dib = IntPtr.Zero; }
        if (_memDc != IntPtr.Zero) { DeleteDC(_memDc); _memDc = IntPtr.Zero; }
        if (_screenDc != IntPtr.Zero) { ReleaseDC(IntPtr.Zero, _screenDc); _screenDc = IntPtr.Zero; }
        Bits = IntPtr.Zero;
        Width = Height = 0;
    }

    public void Dispose() => Release();
}
