using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Stagecast.App;

/// <summary>
/// Keeps Windows from sleeping and the display from turning off while an output is on (a power request that Windows drops by itself if the
/// process ends). It does not stop a lock screen set by policy, Win+L or a closed laptop lid.
/// </summary>
internal sealed class SleepGuard : IDisposable
{
    private readonly ILogger _logger;
    private IntPtr _request;
    private bool _failed; // a request that Windows refuses is not asked again every quarter second

    public SleepGuard(ILogger logger) => _logger = logger;

    public bool Active => _request != IntPtr.Zero;

    /// <summary>Asks for (or gives back) the request; does nothing when it already is as asked.</summary>
    public void Set(bool wanted)
    {
        if (wanted == Active || (wanted && _failed)) return;
        if (!wanted)
        {
            Release();
            return;
        }

        var context = new ReasonContext { Version = 0, Flags = 0x1, Reason = "Stagecast output is on" };
        IntPtr handle = PowerCreateRequest(ref context);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            _logger.LogWarning("Could not create a power request (error {Error})", Marshal.GetLastWin32Error());
            _failed = true;
            return;
        }

        if (!PowerSetRequest(handle, 0) || !PowerSetRequest(handle, 1)) // display required, system required
        {
            _logger.LogWarning("Could not set the power request (error {Error})", Marshal.GetLastWin32Error());
            CloseHandle(handle);
            _failed = true;
            return;
        }

        _request = handle;
        _logger.LogInformation("Sleep is held off while an output is on");
    }

    private void Release()
    {
        IntPtr handle = _request;
        _request = IntPtr.Zero;
        PowerClearRequest(handle, 0);
        PowerClearRequest(handle, 1);
        CloseHandle(handle);
        _logger.LogInformation("Sleep is allowed again");
    }

    public void Dispose()
    {
        if (Active) Release();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string Reason;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PowerSetRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PowerClearRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
