using System.Runtime.InteropServices;

namespace Stagecast.Rendering.Interop;

/// <summary>Composition needs a DispatcherQueue on the thread that creates the Compositor; WPF does not provide one.</summary>
internal static class DispatcherQueueHelper
{
    public static IntPtr Create()
    {
        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = 2,     // DQTYPE_THREAD_CURRENT
            apartmentType = 0,  // DQTAT_COM_NONE: WPF already initialised the STA
        };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out IntPtr controller));
        return controller;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
}
