using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace WindowsDuo.Core;

// Hinge angle from the phone over the LAN.
//
// The PC is the TCP client and the phone runs the server, because outbound
// connections are allowed by default while inbound ones are not: this needs no
// firewall rule on the PC at all, and the return traffic on an established
// connection is permitted statefully.
//
// The phone shows its own Wi-Fi address on screen; it goes in the settings box.
// The connection reconnects on its own, so unplugging the phone or roaming
// between APs recovers without intervention.
public sealed class PhoneAngleLink : IDisposable
{
    private const int ReconnectDelayMs = 1000;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _pump;

    private double _measured = 120;
    private double _smoothed = 120;
    private DateTime _lastSample = DateTime.MinValue;
    private DateTime _lastSmooth = DateTime.UtcNow;

    public string Host { get; set; } = "";
    public int Port { get; set; } = 45680;

    public double ManualAngle { get; set; } = 120;
    public bool UseManual { get; set; } = true;

    public string Status { get; private set; } = "未连接";
    public long Samples { get; private set; }

    public bool IsAvailable
    {
        get { lock (_gate) return (DateTime.UtcNow - _lastSample).TotalSeconds < 0.5; }
    }

    public double MeasuredAngle
    {
        get { lock (_gate) return _smoothed; }
    }

    public double Angle => UseManual || !IsAvailable ? ManualAngle : MeasuredAngle;

    public void Start()
    {
        if (_pump is not null) return;
        _cts = new CancellationTokenSource();
        _pump = Task.Run(() => Pump(_cts.Token));
    }

    private async Task Pump(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(Host))
            {
                Status = "请填写手机地址（手机上会显示）";
                if (!await Delay(token)) break;
                continue;
            }

            try
            {
                Status = $"正在连接 {Host}:{Port}…";
                using var client = new TcpClient();
                await client.ConnectAsync(Host, Port, token);
                using var stream = client.GetStream();
                Status = $"已连接 {Host}:{Port}";
                using var reader = new StreamReader(stream, Encoding.ASCII);

                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(token);
                    if (line is null) break; // phone closed the socket
                    if (TryParse(line, out double value) && value is >= 0 and <= 180)
                    {
                        lock (_gate)
                        {
                            // Same 45 ms time constant as the original renderer.
                            var now = DateTime.UtcNow;
                            double dt = Math.Clamp((now - _lastSmooth).TotalSeconds, 1.0 / 240, 0.2);
                            _smoothed += (value - _smoothed) * (1 - Math.Exp(-dt / 0.045));
                            _measured = value;
                            _lastSample = now;
                            _lastSmooth = now;
                        }
                        Samples++;
                        Status = $"手机角度 {value:0.0}°（{Host}:{Port}）";
                    }
                }
                Status = "手机已断开，正在重连…";
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Status = $"连接失败：{ex.Message}"; }

            if (!await Delay(token)) break;
        }
    }

    private static async Task<bool> Delay(CancellationToken token)
    {
        try { await Task.Delay(ReconnectDelayMs, token); return true; }
        catch (OperationCanceledException) { return false; }
    }

    // Wire format is deliberately lenient: any single float in the payload wins,
    // so "angle=123.4", "123.4" and "a:123.4" all parse.
    private static bool TryParse(string line, out double value)
    {
        value = 0;
        foreach (var token in line.Split(new[] { ' ', ',', ';', '=', ':', '\t' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _pump?.Wait(300); } catch { /* shutting down */ }
        _cts?.Dispose();
        _pump = null;
    }
}
