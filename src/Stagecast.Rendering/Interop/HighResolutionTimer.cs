using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Stagecast.Rendering.Interop;

/// <summary>
/// A waitable timer with sub-millisecond accuracy (Windows 10 1803+). Thread.Sleep and PeriodicTimer only wake on the system
/// tick (about 15.6 ms), which is useless for 60 fps pacing.
/// </summary>
internal sealed class HighResolutionTimer : IDisposable
{
    private const uint HighResolution = 0x00000002, AllAccess = 0x1F0003;

    private readonly SafeWaitHandle _handle;

    public HighResolutionTimer()
    {
        _handle = CreateWaitableTimerExW(IntPtr.Zero, null, HighResolution, AllAccess);
        if (_handle.IsInvalid) throw new InvalidOperationException("high resolution timers are not available", new System.ComponentModel.Win32Exception());
    }

    /// <summary>Blocks the calling thread for <paramref name="delay"/> (no-op when it is not positive).</summary>
    public void Wait(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return;
        long dueTime = -delay.Ticks; // negative = relative, in 100 ns units
        if (!SetWaitableTimer(_handle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false)) throw new System.ComponentModel.Win32Exception();
        WaitForSingleObject(_handle, 0xFFFFFFFF);
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completion, IntPtr completionArgument, [MarshalAs(UnmanagedType.Bool)] bool resume);
}
