using WindowsDuo.Core;

namespace WindowsDuo.App;

// --dup-live: decide whether Desktop Duplication actually delivers the desktop
// on this machine. The earlier probe sampled the very first acquired frame,
// which after DuplicateOutput is frequently an empty one, so "all black" may
// have been a measurement artefact rather than a platform limit.
//
// The test puts a colour-cycling window on screen and reads the duplicated
// pixel at its centre: if the pixel follows the window, duplication is live.
public static class DupLive
{
    private static readonly System.Drawing.Rectangle Spot = new(120, 120, 300, 300);

    public static int Run(int seconds)
    {
        using var renderer = new GlassRenderer();
        using var duplicator = new DesktopDuplicator(renderer.Device, renderer.Context);
        if (!duplicator.Ready)
        {
            Console.WriteLine($"duplication unavailable: {duplicator.LastError}");
            return 1;
        }
        Console.WriteLine($"duplication {duplicator.Width}x{duplicator.Height}, sampling pixel at {Spot.Left + 10},{Spot.Top + 10}");

        var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = Spot,
            TopMost = true,
            BackColor = System.Drawing.Color.Red,
        };

        bool flip = false;
        int tick = 0;
        int frames = 0;
        string lastSeen = "?";

        var poll = new System.Windows.Forms.Timer { Interval = 250 };
        poll.Tick += (_, _) =>
        {
            tick++;
            // the colour changes slightly faster than we poll, so frames exist
            if (tick % 2 == 0)
            {
                flip = !flip;
                form.BackColor = flip ? System.Drawing.Color.Lime : System.Drawing.Color.Red;
            }

            if (duplicator.TryCapture(0)) frames++;

            if (tick % 4 == 0 && duplicator.TryReadbackRgba(out var px, out int w, out _))
            {
                int x = Spot.Left + 10, y = Spot.Top + 10;
                int i = (y * w + x) * 4;
                var c = System.Drawing.Color.FromArgb(px[i], px[i + 1], px[i + 2]);
                lastSeen = $"({c.R},{c.G},{c.B})";
                Console.WriteLine($"[{tick,3}] window={Describe(form.BackColor)}  duplicated={lastSeen}  frames={frames}");
            }
        };

        var stop = new System.Windows.Forms.Timer { Interval = Math.Max(2, seconds) * 1000 };
        stop.Tick += (_, _) => { stop.Stop(); poll.Stop(); form.Close(); };

        form.Shown += (_, _) => { poll.Start(); stop.Start(); };
        Application.Run(form);

        Console.WriteLine($"frames acquired: {frames}");
        Console.WriteLine(frames > 0
            ? "verdict: Desktop Duplication DOES deliver frames on this machine"
            : "verdict: Desktop Duplication produces no frames here");
        Console.WriteLine($"last duplicated pixel: {lastSeen}  (window was {Describe(form.BackColor)})");
        return 0;
    }

    private static string Describe(System.Drawing.Color c) => $"({c.R},{c.G},{c.B})";
}
