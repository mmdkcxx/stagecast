using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using WinRT;

namespace Stagecast.Rendering.Capture;

/// <summary>
/// Puts decoded pixels on the stage: creates composition surfaces and copies a picture into them. One Direct3D device is shared by
/// all documents of an engine and is created the first time a document needs it.
/// </summary>
internal sealed class DocumentSurfaces : IDisposable
{
    private readonly Compositor _compositor;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private CompositionGraphicsDevice? _graphics;

    public DocumentSurfaces(Compositor compositor) => _compositor = compositor;

    public Compositor Compositor => _compositor;

    private void EnsureDevice()
    {
        if (_graphics is not null) return;
        var (device, context, _) = Direct3D.CreateDevice();
        using (var multithread = device.QueryInterface<ID3D11Multithread>()) multithread.SetMultithreadProtected(true);
        _device = device;
        _context = context;

        IntPtr compositorPtr = ((IWinRTObject)_compositor).NativeObject.ThisPtr;
        Guid iid = typeof(ICompositorInterop).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(compositorPtr, ref iid, out IntPtr interopPtr));
        try
        {
            var interop = (ICompositorInterop)Marshal.GetObjectForIUnknown(interopPtr);
            interop.CreateGraphicsDevice(device.NativePointer, out IntPtr graphicsPtr);
            try { _graphics = MarshalInspectable<CompositionGraphicsDevice>.FromAbi(graphicsPtr); }
            finally { Marshal.Release(graphicsPtr); }
        }
        finally
        {
            Marshal.Release(interopPtr);
        }
    }

    public CompositionDrawingSurface CreateSurface(int width, int height)
    {
        EnsureDevice();
        return _graphics!.CreateDrawingSurface(new Windows.Foundation.Size(width, height),
            DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
    }

    /// <summary>Copies premultiplied BGRA pixels (<paramref name="stride"/> bytes per row) over the whole surface.</summary>
    public void Draw(CompositionDrawingSurface surface, byte[] pixels, int width, int height, int stride)
    {
        EnsureDevice();
        IntPtr surfacePtr = ((IWinRTObject)surface).NativeObject.ThisPtr;
        Guid iid = typeof(ICompositionDrawingSurfaceInterop).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePtr, ref iid, out IntPtr interopPtr));
        try
        {
            var interop = (ICompositionDrawingSurfaceInterop)Marshal.GetObjectForIUnknown(interopPtr);
            Guid textureIid = typeof(ID3D11Texture2D).GUID;
            interop.BeginDraw(IntPtr.Zero, ref textureIid, out IntPtr texturePtr, out Point offset);
            try
            {
                using var texture = new ID3D11Texture2D(texturePtr);
                GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    var box = new Vortice.Mathematics.Box(offset.X, offset.Y, 0, offset.X + width, offset.Y + height, 1);
                    _context!.UpdateSubresource(handle.AddrOfPinnedObject(), texture, 0, (uint)stride, 0, box);
                }
                finally
                {
                    handle.Free();
                }
            }
            finally
            {
                interop.EndDraw();
            }
        }
        finally
        {
            Marshal.Release(interopPtr);
        }
    }

    public void Dispose()
    {
        _graphics?.Dispose();
        _graphics = null;
        _context?.Dispose();
        _device?.Dispose();
        _context = null;
        _device = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [ComImport, Guid("25297D5C-3AD4-4C9C-B5CF-E36A38512330"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorInterop
    {
        void CreateCompositionSurfaceForHandle(IntPtr swapChain, out IntPtr result);

        void CreateCompositionSurfaceForSwapChain(IntPtr swapChain, out IntPtr result);

        void CreateGraphicsDevice(IntPtr renderingDevice, out IntPtr result);
    }

    [ComImport, Guid("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositionDrawingSurfaceInterop
    {
        void BeginDraw(IntPtr updateRect, ref Guid iid, out IntPtr updateObject, out Point updateOffset);

        void EndDraw();
    }
}
