using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Stagecast.Spike;

/// <summary>
/// S4: grab one frame of a composition visual with Windows.Graphics.Capture and save it as a PNG.
/// The visual is not attached to any window, so this proves (or disproves) that capture works for an off-screen visual.
/// </summary>
internal static class FrameCapture
{
    private static readonly Guid DxgiAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    public static async Task<(int Width, int Height)> CaptureOnceAsync(Windows.UI.Composition.ContainerVisual visual, string pngPath)
    {
        SpikeLog.Write($"capture supported={GraphicsCaptureSession.IsSupported()}");

        var item = GraphicsCaptureItem.CreateFromVisual(visual);
        SpikeLog.Write($"capture item created, size {item.Size.Width}x{item.Size.Height}");

        D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, Array.Empty<FeatureLevel>(), out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
        using (device)
        using (context)
        {
            using var dxgi = device!.QueryInterface<IDXGIDevice>();
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr inspectable));
            var winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);

            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            var got = new TaskCompletionSource<bool>();
            pool.FrameArrived += (_, _) => got.TrySetResult(true);
            using var session = pool.CreateCaptureSession(item);
            session.StartCapture();
            SpikeLog.Write("capture session started");

            var winner = await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            if (winner != got.Task) throw new TimeoutException("no frame arrived within 10 s");

            using var frame = pool.TryGetNextFrame() ?? throw new InvalidOperationException("TryGetNextFrame returned null");
            SpikeLog.Write($"frame arrived, content size {frame.ContentSize.Width}x{frame.ContentSize.Height}");

            var access = (IDirect3DDxgiInterfaceAccess)GetComObject(frame.Surface);
            Guid texIid = typeof(ID3D11Texture2D).GUID;
            IntPtr texPtr = access.GetInterface(ref texIid);
            using var texture = new ID3D11Texture2D(texPtr);
            var desc = texture.Description;
            SpikeLog.Write($"texture {desc.Width}x{desc.Height} format={desc.Format}");

            var stagingDesc = desc with
            {
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            };
            using var staging = device.CreateTexture2D(stagingDesc);
            context!.CopyResource(staging, texture);
            var map = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var pixels = new byte[(int)(desc.Height * map.RowPitch)];
                Marshal.Copy(map.DataPointer, pixels, 0, pixels.Length);
                var bitmap = BitmapSource.Create((int)desc.Width, (int)desc.Height, 96, 96, PixelFormats.Bgra32, null, pixels, (int)map.RowPitch);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
                using var file = File.Create(pngPath);
                encoder.Save(file);
                SpikeLog.Write($"png saved {pngPath}");
                return ((int)desc.Width, (int)desc.Height);
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }
    }

    private static object GetComObject(object winrtObject)
    {
        var native = ((IWinRTObject)winrtObject).NativeObject;
        Guid iid = DxgiAccessIid;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native.ThisPtr, ref iid, out IntPtr ptr));
        try { return Marshal.GetObjectForIUnknown(ptr); }
        finally { Marshal.Release(ptr); }
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
