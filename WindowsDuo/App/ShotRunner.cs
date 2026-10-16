using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WindowsDuo.App;

// --shot harness: opens the real window in a known state, waits for a frame,
// grabs the window rectangle and writes a PNG. Exists so the UI can be checked
// without a human at the keyboard.
public static class ShotRunner
{
    public static int Run(string imagePath, string outPath, int manualAngle, bool keystone)
    {
        var form = new MainForm();
        form.PrepareShot(imagePath, manualAngle, keystone);

        var timer = new System.Windows.Forms.Timer { Interval = 1400 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            form.FreezeForShot();
            Application.DoEvents();
            Thread.Sleep(400);
            try
            {
                var bounds = form.Bounds;
                using var bmp = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                    g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
                var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                bmp.Save(outPath, ImageFormat.Png);
                Console.WriteLine($"shot: {outPath}");

                // Second capture through the control tree's own paint path. If the
                // two disagree, the artefact is in window composition (layered
                // clipping regions, DWM) rather than in the layout or painting.
                using var drawn = new System.Drawing.Bitmap(form.ClientSize.Width, form.ClientSize.Height);
                form.DrawToBitmap(drawn, new System.Drawing.Rectangle(System.Drawing.Point.Empty, form.ClientSize));
                drawn.Save(Path.ChangeExtension(outPath, null) + "_drawn.png", ImageFormat.Png);
                Console.WriteLine($"drawn: {Path.ChangeExtension(outPath, null)}_drawn.png");
                Console.WriteLine($"diag: {form.PanelDiagnostics}");
            }
            catch (Exception ex) { Console.WriteLine($"shot failed: {ex.Message}"); }
            finally { form.Close(); }
        };
        form.Shown += (_, _) => timer.Start();
        Application.Run(form);
        return 0;
    }

    // --lights-probe: renders the window controls on their own, on a host whose
    // background is a colour they must never be covering. Counts how many pixels
    // of the control's rectangle come back as the chrome they used to fill
    // themselves with, which is what showed up as a black plate over the desktop.
    public static int LightsProbe(string outPath)
    {
        using var form = new Form
        {
            ClientSize = new Size(400, 140),
            BackColor = Color.Magenta,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(0, 0),
            ShowInTaskbar = false,
        };
        var lights = new TrafficLights { Location = new System.Drawing.Point(40, 40) };
        form.Controls.Add(lights);
        form.Show();
        Application.DoEvents();
        lights.ApplyDpi();
        Application.DoEvents();

        using var bmp = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bmp, new Rectangle(System.Drawing.Point.Empty, form.ClientSize));
        var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(outPath, ImageFormat.Png);

        int chrome = 0, magenta = 0, coloured = 0, total = 0;
        var rect = lights.Bounds;
        for (int y = rect.Top; y < rect.Bottom; y++)
        for (int x = rect.Left; x < rect.Right; x++)
        {
            var c = bmp.GetPixel(x, y);
            total++;
            if (Math.Abs(c.R - 33) + Math.Abs(c.G - 31) + Math.Abs(c.B - 40) <= 6) chrome++;
            if (c.R > 200 && c.G < 80 && c.B > 200) magenta++;
            int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
            if (max - min > 80) coloured++;
        }
        Console.WriteLine($"lights {rect.Width}x{rect.Height}: chromeFill={chrome}/{total} hostShowingThrough={magenta} " +
                          $"coloured={coloured}");
        Console.WriteLine(chrome == 0
            ? "no fill of its own: the dots sit on what is behind them"
            : "FAIL: the control is still painting its own rectangle");
        Console.WriteLine($"image: {Path.GetFullPath(outPath)}");
        form.Close();
        return chrome == 0 ? 0 : 1;
    }

    /// Every self-painted control has to survive the trip to the screen, and
    /// DrawToBitmap is not the same code path as what the user sees. Counting
    /// pure-white glyph pixels per control rectangle says which of the two a
    /// missing piece of UI is: never drawn, or drawn but not composited.
    public static int PaintProbe(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var form = new MainForm();
        form.Shown += (_, _) =>
        {
            var t = new System.Windows.Forms.Timer { Interval = 1600 };
            t.Tick += (_, _) =>
            {
                t.Stop();
                RunPaintProbe(form, outDir);
                form.Close();
            };
            t.Start();
        };
        Application.Run(form);
        return 0;
    }

    private static void RunPaintProbe(MainForm form, string outDir)
    {
        using var whole = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(whole, new Rectangle(System.Drawing.Point.Empty, form.ClientSize));
        var image = Path.Combine(outDir, "paint-probe.png");
        whole.Save(image, ImageFormat.Png);

        foreach (var type in new[] { typeof(HeaderText), typeof(TrafficLights), typeof(GlassButton),
                                     typeof(GlassSlider), typeof(GlassCheck) })
        {
            var control = Find(form, type);
            if (control is null) { Console.WriteLine($"{type.Name}: not found"); continue; }

            using var alone = new Bitmap(Math.Max(1, control.Width), Math.Max(1, control.Height));
            control.DrawToBitmap(alone, new Rectangle(System.Drawing.Point.Empty, alone.Size));

            var at = RelativeTo(form, control);
            int onScreen = CountWhite(whole, at, control.Size);
            Console.WriteLine($"{type.Name,-13} bounds={control.Bounds} at={at} white alone={CountWhite(alone, System.Drawing.Point.Empty, alone.Size)} " +
                              $"in window={onScreen}");
        }
        Console.WriteLine($"image: {image}");
    }

    /// The client-area point the control's top-left lands on, however deep it is
    /// nested - `Location` alone is parent-relative.
    private static System.Drawing.Point RelativeTo(Control form, Control control)
    {
        var screen = control.PointToScreen(System.Drawing.Point.Empty);
        var origin = form.PointToScreen(System.Drawing.Point.Empty);
        return new System.Drawing.Point(screen.X - origin.X, screen.Y - origin.Y);
    }

    private static Control? Find(Control parent, Type type)
    {
        foreach (Control c in parent.Controls)
        {
            if (c.GetType() == type) return c;
            var hit = Find(c, type);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// WinForms does not clip a child to its parent, so a control that is wider
    /// than its row is not cut off - it draws over its neighbours and past the
    /// edge of the column, which reads as "the right hand side is missing".
    private static int ReportOverflow(Control parent, int depth)
    {
        int bad = 0;
        foreach (Control c in parent.Controls)
        {
            if (c.Left < 0 || c.Top < 0 ||
                c.Right > parent.ClientSize.Width + 1 || c.Bottom > parent.ClientSize.Height + 1)
            {
                bad++;
                string text = c.Text.Length > 18 ? c.Text[..18] : c.Text;
                Console.WriteLine($"  overflow: {c.GetType().Name} '{text}' {c.Bounds} " +
                                  $"in {parent.GetType().Name} {parent.ClientSize}");
            }
            bad += ReportOverflow(c, depth + 1);
        }
        return bad;
    }

    private static int CountWhite(Bitmap bmp, System.Drawing.Point at, Size size)
    {
        int n = 0;
        for (int y = at.Y; y < Math.Min(at.Y + size.Height, bmp.Height); y++)
        for (int x = at.X; x < Math.Min(at.X + size.Width, bmp.Width); x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.R >= 250 && c.G >= 250 && c.B >= 250) n++;
        }
        return n;
    }

    // --page-probe: run the real window for a few seconds with the desktop
    // backdrop live, then report what the page's glass is actually showing. The
    // page's clear middle should be the captured desktop pixel for pixel; it is
    // compared against both the capture and the generated field, so "the page is
    // transparent" is a measurement rather than a claim.
    public static int PageProbe(int seconds, string outDir, bool maximize, bool shrink)
    {
        Directory.CreateDirectory(outDir);
        var form = new MainForm();
        form.Shown += (_, _) =>
        {
            if (shrink)
            {
                // The narrowest window the user can get to is where content runs
                // out of its container, so measure it rather than eyeball it.
                var early = new System.Windows.Forms.Timer { Interval = Math.Max(1, seconds / 3) * 1000 };
                early.Tick += (_, _) =>
                {
                    early.Stop();
                    Console.WriteLine($"shrinking to {form.MinimumSize}");
                    form.ClientSize = form.MinimumSize;
                    Application.DoEvents();
                    int bad = ReportOverflow(form, 0);
                    Console.WriteLine(bad == 0 ? "layout: nothing spills out of its container"
                                               : $"layout: {bad} controls spill out of their container");
                };
                early.Start();
            }

            if (maximize)
            {
                // Sample against the desktop at several delays after maximising:
                // a transient (the capture has not caught up with the new size)
                // and a permanent failure look the same in a single reading.
                var mid = new System.Windows.Forms.Timer { Interval = Math.Max(1, seconds / 3) * 1000 };
                mid.Tick += (_, _) =>
                {
                    mid.Stop();
                    Console.WriteLine($"maximising {form.ClientSize}");
                    ShowWindow(form.Handle, SwMaximize);
                    foreach (var delay in new[] { 80, 250, 700, 1800 })
                    {
                        var wait = new System.Windows.Forms.Timer { Interval = delay };
                        wait.Tick += (_, _) => { wait.Stop(); wait.Dispose(); SampleMatch(form, $"+{delay}ms"); };
                        wait.Start();
                    }
                };
                mid.Start();
            }

            var timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, seconds) * 1000 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                ReportBackdrop(form, outDir);
                form.Close();
            };
            timer.Start();
        };
        Application.Run(form);
        return 0;
    }

    private const int SwMaximize = 3;

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);

    private static void ReportBackdrop(MainForm form, string outDir)
    {
        Console.WriteLine($"backdrop: exclusionOk={form.BackdropExclusionOk} frames={form.BackdropFrames} " +
                          $"desktopLive={WindowsDuo.App.PageGlass.DesktopLive} client={form.ClientSize}");

        // Ground truth: an independent grab of the same screen rectangle. Because
        // the window excludes itself from capture, this returns the desktop that
        // is behind it, not the app.
        var size = form.ClientSize;
        var screen = form.PointToScreen(System.Drawing.Point.Empty);
        using var grab = new WindowsDuo.Core.GdiScreenCapture();
        if (!grab.Ensure(size.Width, size.Height) || !grab.GrabRegion(screen.X, screen.Y))
        {
            Console.WriteLine("backdrop: independent grab failed");
            return;
        }

        int w = grab.Width, h = grab.Height;
        var rgba = new byte[w * h * 4];
        var row = new byte[w * 4];
        for (int y = 0; y < h; y++)
        {
            Marshal.Copy(grab.Bits + y * grab.RowPitch, row, 0, row.Length);
            var words = MemoryMarshal.Cast<byte, uint>(row.AsSpan());
            for (int x = 0; x < w; x++)
            {
                uint c = words[x];
                words[x] = (c & 0xFF00FF00u) | ((c & 0x00FF0000u) >> 16) | ((c & 0x000000FFu) << 16);
            }
            Array.Copy(row, 0, rgba, y * row.Length, row.Length);
        }
        Verify.PngWriter.WriteRgba(Path.Combine(outDir, "capture.png"), w, h, rgba);
        Console.WriteLine($"capture.png {w}x{h} (what is behind the window)");

        {
            using var drawn = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            form.DrawToBitmap(drawn, new Rectangle(System.Drawing.Point.Empty, form.ClientSize));
            drawn.Save(Path.Combine(outDir, "page.png"), ImageFormat.Png);

            var field = WindowsDuo.App.Theme.Backdrop(form.ClientSize);
            long hit = 0, miss = 0, sumCapture = 0, sumField = 0;
            int samples = 0, maxCapture = 0;
            for (int y = 8; y < Math.Min(h, drawn.Height) && y < 100; y += 6)
            for (int x = 120; x < Math.Min(w, drawn.Width) - 120; x += 6)
            {
                var page = drawn.GetPixel(x, y);
                var cap = GetPixel(rgba, w, x, y);
                var fld = field.GetPixel(
                    Math.Min(x, field.Width - 1), Math.Min(y, field.Height - 1));

                int dc = Math.Abs(page.R - cap.r) + Math.Abs(page.G - cap.g) + Math.Abs(page.B - cap.b);
                int df = Math.Abs(page.R - fld.R) + Math.Abs(page.G - fld.G) + Math.Abs(page.B - fld.B);
                sumCapture += dc;
                sumField += df;
                maxCapture = Math.Max(maxCapture, dc);
                if (dc <= 6) hit++; else miss++;
                samples++;
            }
            Console.WriteLine($"page vs captured desktop: mean={sumCapture / (double)samples:0.0} max={maxCapture} " +
                              $"within 2/255={hit * 100.0 / samples:0.0}%");
            Console.WriteLine($"page vs generated field:  mean={sumField / (double)samples:0.0}");
            Console.WriteLine(miss == 0
                ? "page is transparent: it shows the desktop behind it"
                : $"NOT transparent: {miss} of {samples} samples differ from the desktop");
        }

        // What one captured frame costs the UI thread, split into the two halves
        // that matter: blitting the finished page, and drawing the glass again
        // because the desktop under it moved. Children are not invalidated, which
        // is what the capture path does too.
        const int frames = 10;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        WindowsDuo.App.PageGlass.InvalidateSurfaces();
        form.Update();
        sw.Restart();
        for (int i = 0; i < frames; i++)
        {
            WindowsDuo.App.PageGlass.InvalidateSurfaces();
            form.Update();
        }
        double cached = sw.ElapsedMilliseconds / (double)frames;

        sw.Restart();
        for (int i = 0; i < frames; i++)
        {
            WindowsDuo.App.PageGlass.InvalidateSlabs();
            WindowsDuo.App.PageGlass.InvalidateSurfaces();
            form.Update();
        }
        double redrawn = sw.ElapsedMilliseconds / (double)frames;
        Console.WriteLine($"paint: {cached:0.0} ms cached, {redrawn:0.0} ms with the glass redrawn");
        Console.WriteLine($"glass redraw costs {redrawn - cached:0.0} ms per captured frame");
    }

    // A cheap read on whether the page is still showing the desktop: grab the
    // same rectangle independently and diff a sparse grid against what the page
    // has drawn.
    private static void SampleMatch(MainForm form, string tag)
    {
        var size = form.ClientSize;
        var at = form.PointToScreen(System.Drawing.Point.Empty);
        using var grab = new WindowsDuo.Core.GdiScreenCapture();
        if (!grab.Ensure(size.Width, size.Height) || !grab.GrabRegion(at.X, at.Y))
        {
            Console.WriteLine($"{tag}: grab failed");
            return;
        }
        var cap = new byte[grab.Width * grab.Height * 4];
        var row = new byte[grab.Width * 4];
        for (int y = 0; y < grab.Height; y++)
        {
            Marshal.Copy(grab.Bits + y * grab.RowPitch, row, 0, row.Length);
            Array.Copy(row, 0, cap, y * row.Length, row.Length);
        }

        using var drawn = new Bitmap(size.Width, size.Height);
        form.DrawToBitmap(drawn, new Rectangle(System.Drawing.Point.Empty, size));

        // Only the band above the block and the cards: the glass bands and the
        // block are meant to differ from the desktop, so sampling them would
        // measure the effect rather than the transparency.
        long sum = 0;
        int samples = 0, close = 0;
        for (int y = 8; y < Math.Min(grab.Height, size.Height) && y < 100; y += 5)
        for (int x = 120; x < Math.Min(grab.Width, size.Width) - 120; x += 5)
        {
            var page = drawn.GetPixel(x, y);
            int i = (y * grab.Width + x) * 4;
            int d = Math.Abs(page.R - cap[i + 2]) + Math.Abs(page.G - cap[i + 1]) + Math.Abs(page.B - cap[i]);
            sum += d;
            if (d <= 6) close++;
            samples++;
        }
        Console.WriteLine($"{tag}: client={size} frames={form.BackdropFrames} desktopLive={WindowsDuo.App.PageGlass.DesktopLive} " +
                          $"match={close * 100.0 / Math.Max(1, samples):0.0}% mean={sum / (double)Math.Max(1, samples):0.0}");
    }

    private static (int r, int g, int b) GetPixel(byte[] rgba, int w, int x, int y)
    {
        int i = (y * w + x) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2]);
    }

    // --live-test: bring up live mode for a few seconds and report state. The
    // screen grab taken at the end should NOT contain the overlay, which is
    // exactly what proves the self-exclusion is working.
    public static int LiveTest(int seconds, string? outPath, bool preview)
    {
        var form = new MainForm();
        form.Shown += (_, _) =>
        {
            form.Live.Start(preview: preview);

            var timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, seconds) * 1000 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Console.WriteLine($"running={form.Live.Running} overlayVisible={form.Live.OverlayVisible} " +
                                  $"setupWindowVisible={form.Visible} exclusionOk={form.Live.CaptureExclusionOk}");
                Console.WriteLine($"status: {form.Live.Status}");
                Console.WriteLine($"displayed fps: {form.Live.DisplayedFps:0.0}");
                Console.WriteLine(form.Visible || form.Live.OverlayVisible
                    ? "visibility: OK (something is on screen)"
                    : "visibility: FAIL (app would look closed)");

                if (outPath is not null)
                {
                    try
                    {
                        var (px, w, h) = Core.ScreenGrabber.Capture();
                        Verify.PngWriter.WriteRgba(outPath, w, h, px);
                        Console.WriteLine($"screen grab (should contain no glass): {outPath} {w}x{h}");
                    }
                    catch (Exception ex) { Console.WriteLine($"grab failed: {ex.Message}"); }
                }

                form.Live.Stop();
                form.Close();
            };
            timer.Start();
        };
        Application.Run(form);
        return 0;
    }
}
