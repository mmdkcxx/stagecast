using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
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
    private Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice? _winrtDevice;
    private CompositionGraphicsDevice? _graphics;

    private readonly ILogger _logger;

    public DocumentSurfaces(Compositor compositor, ILogger logger)
    {
        _compositor = compositor;
        _logger = logger;
    }

    private void Step(string what) => _logger.LogDebug("surfaces: {Step}", what);

    public Compositor Compositor => _compositor;

    private void EnsureDevice()
    {
        if (_graphics is not null) return;
        Step("create device");
        var (device, context, winrtDevice) = Direct3D.CreateDevice();
        _winrtDevice = winrtDevice;
        using (var multithread = device.QueryInterface<ID3D11Multithread>()) multithread.SetMultithreadProtected(true);
        _device = device;
        _context = context;
        _logger.LogInformation("Document surfaces: Direct3D device ready");

        IntPtr compositorPtr = ((IWinRTObject)_compositor).NativeObject.ThisPtr;
        Guid iid = typeof(ICompositorInterop).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(compositorPtr, ref iid, out IntPtr interopPtr));
        try
        {
            var interop = (ICompositorInterop)Marshal.GetObjectForIUnknown(interopPtr);
            Step("CreateGraphicsDevice");
            interop.CreateGraphicsDevice(device.NativePointer, out IntPtr graphicsPtr);
            Step("wrap graphics device");
            try { _graphics = MarshalInspectable<CompositionGraphicsDevice>.FromAbi(graphicsPtr); }
            finally { Marshal.Release(graphicsPtr); }
        }
        finally
        {
            Marshal.Release(interopPtr);
        }
    }

    /// <summary>The same device for Windows Graphics Capture, so a captured frame can be copied into a surface without leaving the GPU.</summary>
    public Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice WinRtDevice
    {
        get
        {
            EnsureDevice();
            return _winrtDevice!;
        }
    }

    public CompositionDrawingSurface CreateSurface(int width, int height)
    {
        EnsureDevice();
        Step($"CreateDrawingSurface {width}x{height}");
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
            Step("BeginDraw");
            interop.BeginDraw(IntPtr.Zero, ref textureIid, out IntPtr texturePtr, out Point offset);
            try
            {
                Step($"texture at {offset.X},{offset.Y}");
                using var texture = new ID3D11Texture2D(texturePtr);
                // UpdateSubresource crashed the process on this texture (an atlas that belongs to the compositor), so the pixels go
                // up in a texture of our own and are copied into the surface.
                GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    var description = new Texture2DDescription
                    {
                        Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                        SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
                        CPUAccessFlags = CpuAccessFlags.None, MiscFlags = ResourceOptionFlags.None,
                    };
                    Step("create upload texture");
                    using ID3D11Texture2D upload = _device!.CreateTexture2D(description, new SubresourceData(handle.AddrOfPinnedObject(), (uint)stride));
                    Step("CopySubresourceRegion");
                    _context!.CopySubresourceRegion(texture, 0, (uint)offset.X, (uint)offset.Y, 0, upload, 0);
                    // The compositor reads the surface on its own schedule: the copy has to be on its way to the GPU before EndDraw.
                    _context.Flush();
                }
                finally
                {
                    handle.Free();
                }
            }
            finally
            {
                Step("EndDraw");
                interop.EndDraw();
            }
        }
        finally
        {
            Marshal.Release(interopPtr);
        }
    }

    /// <summary>
    /// Copies a region of a texture of this device (a captured frame) into the top left of the surface, on the GPU. The surface has
    /// to be at least <paramref name="width"/> x <paramref name="height"/>.
    /// </summary>
    public void CopyFrom(CompositionDrawingSurface surface, ID3D11Texture2D source, int left, int top, int width, int height)
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
                var region = new Vortice.Mathematics.Box(left, top, 0, left + width, top + height, 1);
                _context!.CopySubresourceRegion(texture, 0, (uint)offset.X, (uint)offset.Y, 0, source, 0, region);
                _context.Flush();
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
