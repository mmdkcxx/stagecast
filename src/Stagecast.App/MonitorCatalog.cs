using System.Runtime.InteropServices;
using Stagecast.Core.Display;

namespace Stagecast.App;

/// <summary>Lists the monitors with Win32. The process is per-monitor DPI aware, so all numbers are physical pixels.</summary>
internal static class MonitorCatalog
{
    public static List<MonitorInfo> List()
    {
        var found = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref Rect rect, IntPtr _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                string device = info.Device;
                found.Add(new MonitorInfo(device, NumberOf(device), info.Monitor.Left, info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, (info.Flags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return found.OrderBy(m => m.Number).ToList();
    }

    /// <summary>The monitor a window is mostly on.</summary>
    public static MonitorInfo? Of(IReadOnlyList<MonitorInfo> monitors, IntPtr window)
    {
        var handle = MonitorFromWindow(window, 2 /* nearest */);
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (handle == IntPtr.Zero || !GetMonitorInfo(handle, ref info)) return null;
        return OutputGuard.Find(monitors, info.Device);
    }

    private static int NumberOf(string device)
    {
        var digits = new string(device.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int n) ? n : 0;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);
}
