using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Stagecast.Rendering.Capture;

/// <summary>
/// Windows Graphics Capture of one window (by its handle) on a given Direct3D device. The window may be off screen or covered:
/// it is still drawn and captured. A frame comes only when the window changes, so a still slide delivers one frame.
/// </summary>
internal sealed class WindowCapturer : IDisposable
{
    private readonly IDirect3DDevice _device;
    private readonly Action<ID3D11Texture2D, int, int> _onFrame;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private Windows.Graphics.SizeInt32 _poolSize;
    private bool _disposed;

    /// <param name="onFrame">Called on a capture thread with the frame texture and the size of its content; the texture is only valid during the call.</param>
    public WindowCapturer(IntPtr window, IDirect3DDevice device, Action<ID3D11Texture2D, int, int> onFrame)
    {
        _device = device;
        _onFrame = onFrame;
        var interop = ItemInterop();
        Guid itemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        Marshal.ThrowExceptionForHR(interop.CreateForWindow(window, ref itemIid, out IntPtr itemPtr));
        GraphicsCaptureItem item;
        try { item = GraphicsCaptureItem.FromAbi(itemPtr); }
        finally { Marshal.Release(itemPtr); }
        _poolSize = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, item.Size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false;
        try { _session.IsBorderRequired = false; } catch (Exception ex) when (ex is InvalidOperationException or COMException or NotImplementedException) { }
        _session.MinUpdateInterval = TimeSpan.FromMilliseconds(1);
        _session.StartCapture();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
    {
        if (_disposed) return;
        using var frame = pool.TryGetNextFrame();
        if (frame is null) return;
        var content = frame.ContentSize;
        if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
        {
            _poolSize = content;
            pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, content);
        }

        using ID3D11Texture2D texture = Direct3D.TextureOf(frame.Surface);
        _onFrame(texture, content.Width, content.Height);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _pool.Dispose();
    }

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

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    private static IGraphicsCaptureItemInterop ItemInterop()
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(className, className.Length, out IntPtr name);
        try
        {
            Guid iid = typeof(IGraphicsCaptureItemInterop).GUID;
            RoGetActivationFactory(name, ref iid, out IntPtr factory);
            return (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }
}
