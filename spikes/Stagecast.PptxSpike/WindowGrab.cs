using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

/// <summary>Windows Graphics Capture of one window (by HWND): counts frames and saves chosen frames as PNG.</summary>
sealed class WindowGrab : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly object _gate = new();
    private int _frames;
    private int _width, _height;
    private byte[]? _last;
    private long _lastTick;

    public WindowGrab(IntPtr hwnd)
    {
        D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, Array.Empty<FeatureLevel>(),
            out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
        _device = device!;
        _context = context!;
        using var dxgi = _device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr inspectable));
        _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        Marshal.Release(inspectable);

        var interop = GraphicsCaptureItemInterop();
        Guid itemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        Marshal.ThrowExceptionForHR(interop.CreateForWindow(hwnd, ref itemIid, out IntPtr itemPtr));
        GraphicsCaptureItem item = GraphicsCaptureItem.FromAbi(itemPtr);
        Marshal.Release(itemPtr);
        Console.WriteLine($"capture item '{item.DisplayName}' {item.Size.Width}x{item.Size.Height}");

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, item.Size);
        _pool.FrameArrived += OnFrame;
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false;
        try { _session.IsBorderRequired = false; } catch (Exception ex) { Console.WriteLine("IsBorderRequired not available: " + ex.Message); }
        _session.MinUpdateInterval = TimeSpan.FromMilliseconds(1);
        _session.StartCapture();
    }

    public int Frames { get { lock (_gate) return _frames; } }

    private void OnFrame(Direct3D11CaptureFramePool pool, object _)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame is null) return;
        using ID3D11Texture2D texture = TextureOf(frame.Surface);
        var desc = texture.Description;
        var staging = new Texture2DDescription
        {
            Width = desc.Width, Height = desc.Height, MipLevels = 1, ArraySize = 1, Format = desc.Format,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        };
        using ID3D11Texture2D copy = _device.CreateTexture2D(staging);
        _context.CopyResource(copy, texture);
        var map = _context.Map(copy, 0, MapMode.Read);
        var pixels = new byte[desc.Width * desc.Height * 4];
        for (int y = 0; y < desc.Height; y++)
            Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, pixels, y * (int)desc.Width * 4, (int)desc.Width * 4);
        _context.Unmap(copy, 0);
        lock (_gate)
        {
            _frames++;
            _width = (int)desc.Width;
            _height = (int)desc.Height;
            _last = pixels;
            _lastTick = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Size of the newest frame and the pixel at (x, y) as "R,G,B".</summary>
    public string Probe(params (int X, int Y)[] points)
    {
        lock (_gate)
        {
            if (_last is null) return "no frame yet";
            string text = $"{_width}x{_height}";
            foreach (var (x, y) in points)
            {
                int o = (Math.Min(y, _height - 1) * _width + Math.Min(x, _width - 1)) * 4;
                text += $" ({x},{y})={_last[o + 2]},{_last[o + 1]},{_last[o]}";
            }
            return text;
        }
    }

    public void SavePng(string path)
    {
        byte[] pixels; int w, h;
        lock (_gate) { if (_last is null) { Console.WriteLine("no frame to save"); return; } pixels = _last; w = _width; h = _height; }
        using var raw = new MemoryStream();
        for (int y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < w; x++) { int o = (y * w + x) * 4; raw.WriteByte(pixels[o + 2]); raw.WriteByte(pixels[o + 1]); raw.WriteByte(pixels[o]); }
        }
        using var file = File.Create(path);
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Chunk(file, "IHDR", new byte[] { (byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, 8, 2, 0, 0, 0 });
        using var zipped = new MemoryStream();
        using (var z = new ZLibStream(zipped, CompressionLevel.Fastest, true)) raw.WriteTo(z);
        Chunk(file, "IDAT", zipped.ToArray());
        Chunk(file, "IEND", Array.Empty<byte>());
        Console.WriteLine($"saved {path} ({w}x{h})");
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        byte[] length = { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length };
        s.Write(length);
        byte[] t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in t.Concat(data)) { crc ^= b; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1; }
        crc ^= 0xFFFFFFFF;
        s.Write(new byte[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc });
    }

    public void Dispose()
    {
        _pool.FrameArrived -= OnFrame;
        _session.Dispose();
        _pool.Dispose();
        _device.Dispose();
    }

    private static ID3D11Texture2D TextureOf(IDirect3DSurface surface)
    {
        var native = ((IWinRTObject)surface).NativeObject;
        Guid accessIid = typeof(IDirect3DDxgiInterfaceAccess).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native.ThisPtr, ref accessIid, out IntPtr ptr));
        try
        {
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(ptr);
            Guid textureIid = typeof(ID3D11Texture2D).GUID;
            return new ID3D11Texture2D(access.GetInterface(ref textureIid));
        }
        finally { Marshal.Release(ptr); }
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess { IntPtr GetInterface([In] ref Guid iid); }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr window, [In] ref Guid iid, out IntPtr result);
        [PreserveSig] int CreateForMonitor(IntPtr monitor, [In] ref Guid iid, out IntPtr result);
    }

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void RoGetActivationFactory(IntPtr className, [In] ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);

    private static IGraphicsCaptureItemInterop GraphicsCaptureItemInterop()
    {
        WindowsCreateString("Windows.Graphics.Capture.GraphicsCaptureItem", "Windows.Graphics.Capture.GraphicsCaptureItem".Length, out IntPtr name);
        try
        {
            Guid iid = typeof(IGraphicsCaptureItemInterop).GUID;
            RoGetActivationFactory(name, ref iid, out IntPtr factory);
            return (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
        }
        finally { WindowsDeleteString(name); }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
