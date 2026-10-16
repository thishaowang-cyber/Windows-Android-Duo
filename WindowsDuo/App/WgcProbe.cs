using System.Runtime.InteropServices;
using WinRT;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace WindowsDuo.App;

// --wgc-probe: is Windows.Graphics.Capture usable here, and can the mandatory
// yellow capture border be turned off? Both answers decide whether the GPU
// capture path is worth wiring in.
public static class WgcProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr hstring);
    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point pt, uint flags);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, System.Text.StringBuilder? name);

    private const uint MonitorDefaultToPrimary = 1;

    // Package identity is what Windows.Graphics.Capture keys on, and a desktop
    // app only has it when launched through the package (not by double-clicking
    // the exe), so the probe reports it rather than assuming.
    public static string PackageIdentity()
    {
        int length = 0;
        int hr = GetCurrentPackageFullName(ref length, null);
        if (hr == 15700) return "(none — process is unpackaged)";
        var sb = new System.Text.StringBuilder(length);
        hr = GetCurrentPackageFullName(ref length, sb);
        return hr == 0 ? sb.ToString() : $"(query failed: {hr})";
    }

    private static void Request(GraphicsCaptureAccessKind kind, Action<string> Say)
    {
        try
        {
            var op = GraphicsCaptureAccess.RequestAccessAsync(kind);
            var task = op.AsTask();
            bool done = task.Wait(30000);
            Say($"RequestAccessAsync({kind}): completed={done} -> {(done ? task.Result.ToString() : "timed out (consent dialog still open?)")}");
        }
        catch (Exception ex) { Say($"RequestAccessAsync({kind}): {ex.GetType().Name}: {ex.Message}"); }
    }

    public static int Run(int seconds, string? outPath)
    {
        var log = new List<string>();
        void Say(string s) { Console.WriteLine(s); log.Add(s); }
        try { return RunCore(seconds, Say); }
        finally
        {
            if (outPath is not null)
            {
                try { File.WriteAllLines(outPath, log); } catch { /* diagnostics only */ }
            }
        }
    }

    private static int RunCore(int seconds, Action<string> Say)
    {
        Say($"package identity: {PackageIdentity()}");

        // This is the gate. The per-app consent for graphicsCaptureProgrammatic
        // defaults to Prompt, and until it is granted every capture-item creation
        // fails with E_ACCESSDENIED — including for this process's own window.
        // Requesting it is what puts the system consent dialog on screen.
        Request(GraphicsCaptureAccessKind.Programmatic, Say);

        // --- 1. capture item for the primary monitor
        IntPtr hmon = MonitorFromPoint(new Point { X = 0, Y = 0 }, MonitorDefaultToPrimary);
        Say($"monitor handle: 0x{hmon:X}");

        GraphicsCaptureItem item;
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(className, className.Length, out IntPtr hClassName);
        IntPtr factoryPtr;
        int hr;
        try
        {
            var interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            hr = RoGetActivationFactory(hClassName, ref interopIid, out factoryPtr);
        }
        finally { WindowsDeleteString(hClassName); }
        if (hr < 0 || factoryPtr == IntPtr.Zero) { Say($"RoGetActivationFactory failed: 0x{hr:X8}"); return 1; }

        var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
        Marshal.Release(factoryPtr);

        // Isolate the denial: same interop, same IID, but a window instead of a monitor.
        try
        {
            using var probeWindow = new Form { Width = 200, Height = 200, ShowInTaskbar = false };
            probeWindow.Show();
            var itemIid2 = typeof(GraphicsCaptureItem).GUID;
            IntPtr winItem = interop.CreateForWindow(probeWindow.Handle, ref itemIid2);
            Say($"CreateForWindow : OK (0x{winItem:X})");
            Marshal.Release(winItem);
            probeWindow.Close();
        }
        catch (Exception ex) { Say($"CreateForWindow : {ex.Message}"); }

        try
        {
            var itemIid = typeof(GraphicsCaptureItem).GUID;
            IntPtr itemPtr = interop.CreateForMonitor(hmon, ref itemIid);
            item = GraphicsCaptureItem.FromAbi(itemPtr);
            Marshal.Release(itemPtr);
        }
        catch (Exception ex) { Say($"CreateForMonitor failed: {ex.Message}"); return 1; }
        Say($"capture item   : {item.DisplayName}, {item.Size.Width}x{item.Size.Height}");

        // --- 2. our D3D11 device as a WinRT IDirect3DDevice
        var r = D3D11.D3D11CreateDevice(null, Vortice.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { Vortice.Direct3D.FeatureLevel.Level_11_0 }, out ID3D11Device device, out _, out ID3D11DeviceContext ctx);
        if (device is null) { Say("D3D11 device failed"); return 1; }

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr inspectable);        if (hr < 0 || inspectable == IntPtr.Zero) { Say($"CreateDirect3D11DeviceFromDXGIDevice failed: 0x{hr:X8}"); return 1; }
        var winrtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        Marshal.Release(inspectable);

        // --- 3. frame pool + session, and the border question
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;

        string border;
        try
        {
            session.IsBorderRequired = false;
            border = "accepted (no yellow border)";
        }
        catch (Exception ex) { border = $"REJECTED ({ex.GetType().Name}: {ex.Message})"; }
        Say($"borderless     : {border}");

        int frames = 0;
        int lastW = 0, lastH = 0;
        string lastPixel = "-";
        var gate = new object();

        pool.FrameArrived += (sender, _) =>
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;
            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
            var texIid = typeof(ID3D11Texture2D).GUID;
            IntPtr texPtr = access.GetInterface(ref texIid);
            using var tex = new ID3D11Texture2D(texPtr);
            var desc = tex.Description;
            lock (gate) { frames++; lastW = (int)desc.Width; lastH = (int)desc.Height; }
        };

        session.StartCapture();
        Thread.Sleep(Math.Max(1, seconds) * 1000);
        session.Dispose();
        pool.Dispose();

        lock (gate) { }
        Say($"frames arrived : {frames}  (last {lastW}x{lastH})");
        Say($"dup frames note: {lastPixel}");
        Say(frames > 0 ? "verdict: WGC delivers GPU frames" : "verdict: WGC produced no frames");
        return frames > 0 ? 0 : 1;
    }
}
