using System.Runtime.InteropServices;
using WindowsDuo.App;
using WindowsDuo.Core;
using WindowsDuo.Verify;

internal static class Program
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);

    [STAThread]
    private static int Main(string[] args)
    {
        // GUI subsystem, so running the app normally shows no console window —
        // otherwise its black frame would land inside the captured desktop.
        // CLI modes borrow the parent console instead.
        if (args.Length > 0) AttachConsole(-1);

        // Applies the manifest DPI mode (PerMonitorV2) plus visual styles.
        ApplicationConfiguration.Initialize();

        if (args.Contains("--dup-scan"))
            return DupScan.Run();

        if (args.Contains("--live-test"))
        {
            int seconds = int.TryParse(Arg(args, "--seconds"), out var s) ? s : 3;
            return ShotRunner.LiveTest(seconds, Arg(args, "--out"), args.Contains("--preview"));
        }

        if (args.Contains("--monitor"))
        {
            int seconds = int.TryParse(Arg(args, "--seconds"), out var s) ? s : 20;
            int port = int.TryParse(Arg(args, "--port"), out var p) ? p : 45680;
            return AngleMonitor.Run(Arg(args, "--host"), port, seconds, args.Contains("--loopback"));
        }

        if (args.Contains("--dpi"))
        {
            var b = Screen.PrimaryScreen!.Bounds;
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            Console.WriteLine($"screen        : {b.Width}x{b.Height} at {b.X},{b.Y}, dpi {g.DpiX}");

            var eye = DisplayMetrics.Derive();
            Console.WriteLine($"panel         : {eye.WidthMm}x{eye.HeightMm} mm, {eye.DiagonalInches:0.0}\" (measured={eye.Measured}, {eye.Source})");
            Console.WriteLine($"eye distance  : {eye.Eye:0.000}  = {DisplayMetrics.ViewingDistanceMetres} m / {eye.HeightMm / 1000f:0.000} m");

            var form = new MainForm();
            form.Shown += (_, _) =>
            {
                var t = new System.Windows.Forms.Timer { Interval = 900 };
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    Console.WriteLine($"form bounds   : {form.Bounds}");
                    Console.WriteLine($"form client   : {form.ClientSize}, DeviceDpi {form.DeviceDpi}");
                    Console.WriteLine($"working area  : {Screen.FromControl(form).WorkingArea}");
                    Console.WriteLine(form.LayoutDump());
                    form.Close();
                };
                t.Start();
            };
            Application.Run(form);
            return 0;
        }

        if (args.Contains("--wgc-probe"))
        {
            int secs = int.TryParse(Arg(args, "--seconds"), out var ws) ? ws : 3;
            return WgcProbe.Run(secs, Arg(args, "--out"));
        }

        if (args.Contains("--dup-live"))
        {
            int secs = int.TryParse(Arg(args, "--seconds"), out var ls) ? ls : 4;
            return DupLive.Run(secs);
        }

        if (args.Contains("--perf"))
        {
            int frames = int.TryParse(Arg(args, "--frames"), out var n) ? n : 30;
            return PerfProbe.Run(frames);
        }

        if (args.Contains("--frost-sweep"))
        {
            string dir = Arg(args, "--out") ?? Path.GetFullPath("verify-out");
            float tilt = float.TryParse(Arg(args, "--tilt"), out var t) ? t : 40f;
            float bleed = float.TryParse(Arg(args, "--bleed"), out var bl) ? bl : 0.06f;
            return FrostSweep.Run(dir, tilt, args.Contains("--desktop"), args.Contains("--metric"), args.Contains("--full"), bleed);
        }

        if (args.Contains("--verify"))
            return HeadlessVerify.Run(Path.GetFullPath("verify-out"));

        if (args.Contains("--verify-liquid"))
            return LiquidVerify.Run(Path.GetFullPath("verify-liquid-out"));

        // A flag rather than a mode, so it can be combined with the probes.
        if (args.Contains("--trace"))
        {
            var path = Arg(args, "--trace") ?? Path.GetFullPath("page-trace.log");
            if (!Path.IsPathRooted(path)) path = Path.GetFullPath(path);
            WindowsDuo.App.PageGlass.StartTrace(path);
        }

        if (args.Contains("--lights-probe"))
            return ShotRunner.LightsProbe(Arg(args, "--out") ?? Path.GetFullPath("lights-probe.png"));

        if (args.Contains("--paint-probe"))
            return ShotRunner.PaintProbe(Arg(args, "--out") ?? Path.GetFullPath("paint-probe-out"));

        if (args.Contains("--page-probe"))
        {
            int secs = int.TryParse(Arg(args, "--seconds"), out var ps) ? ps : 3;
            return ShotRunner.PageProbe(secs, Arg(args, "--out") ?? Path.GetFullPath("page-probe-out"),
                args.Contains("--max"), args.Contains("--min"));
        }

        if (args.Contains("--shot"))
        {
            string image = Arg(args, "--in") ?? Path.GetFullPath(Path.Combine("verify-out", "source.png"));
            string outPath = Arg(args, "--out") ?? Path.GetFullPath(Path.Combine("verify-out", "ui.png"));
            int angle = int.TryParse(Arg(args, "--angle"), out var a) ? a : 60;
            return ShotRunner.Run(image, outPath, angle, args.Contains("--keystone"));
        }

        if (args.Contains("--live-shot"))
        {
            string dir = Arg(args, "--out") ?? Path.GetFullPath("verify-out");
            int angle = int.TryParse(Arg(args, "--angle"), out var a) ? a : 60;
            return LiveProbe.Run(dir, angle, args.Contains("--keystone"));
        }

        Application.Run(new MainForm());
        return 0;
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
