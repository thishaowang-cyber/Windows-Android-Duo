using System.Net;
using System.Net.Sockets;
using System.Text;
using WindowsDuo.Core;

namespace WindowsDuo.App;

// --monitor: run the real phone link and print what arrives.
//   --monitor --host <phone-ip>            watch a real phone
//   --monitor --loopback                   start a local server and check the client
public static class AngleMonitor
{
    public static int Run(string? host, int port, int seconds, bool loopback)
    {
        TcpListener? server = null;
        if (loopback)
        {
            server = new TcpListener(IPAddress.Loopback, port);
            server.Start();
            host = "127.0.0.1";
            _ = Task.Run(async () =>
            {
                try
                {
                    using var client = await server.AcceptTcpClientAsync();
                    using var writer = new StreamWriter(client.GetStream(), Encoding.ASCII) { AutoFlush = true };
                    var until = DateTime.UtcNow.AddSeconds(seconds + 5);
                    while (DateTime.UtcNow < until)
                    {
                        await writer.WriteLineAsync("angle=137.50");
                        await Task.Delay(33);
                    }
                }
                catch { /* test harness only */ }
            });
        }

        using var link = new PhoneAngleLink { Host = host ?? "", Port = port, UseManual = false };
        link.Start();
        Console.WriteLine($"link -> {host ?? "(no host)"}:{port}");

        var end = DateTime.UtcNow.AddSeconds(seconds);
        bool got = false;
        while (DateTime.UtcNow < end)
        {
            if (link.IsAvailable)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] angle={link.MeasuredAngle,7:0.00}  samples={link.Samples}");
                got = true;
            }
            else
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {link.Status}");
            }
            Thread.Sleep(400);
        }

        if (loopback)
            Console.WriteLine(got && Math.Abs(link.MeasuredAngle - 137.5) < 0.5 ? "link-test: PASS" : "link-test: FAIL");
        if (server is not null) server.Stop();
        return got ? 0 : 1;
    }
}
