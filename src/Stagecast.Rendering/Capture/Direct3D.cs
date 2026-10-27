using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Stagecast.Rendering.Capture;

/// <summary>The few Direct3D 11 / WinRT bridging calls Windows Graphics Capture needs.</summary>
internal static class Direct3D
{
    public static (ID3D11Device Device, ID3D11DeviceContext Context, IDirect3DDevice WinRtDevice) CreateDevice()
    {
        D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, Array.Empty<FeatureLevel>(),
            out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
        using var dxgi = device!.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr inspectable));
        var winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        Marshal.Release(inspectable);
        return (device, context!, winrtDevice);
    }

    /// <summary>The Direct3D texture behind a captured frame surface.</summary>
    public static ID3D11Texture2D TextureOf(IDirect3DSurface surface)
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
        finally
        {
            Marshal.Release(ptr);
        }
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
