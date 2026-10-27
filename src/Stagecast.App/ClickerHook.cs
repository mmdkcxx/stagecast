using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Stagecast.App;

/// <summary>
/// The global clicker mode: sees the page keys while another program has the focus. A key it handles is not passed on, so the
/// other program does not also turn its page. Installed only while the operator has the mode on; never persists.
/// </summary>
internal sealed class ClickerHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100, WmSysKeyDown = 0x0104;
    private const uint LlkhfInjected = 0x10;

    private readonly Func<int, bool, bool, bool, bool> _handle; // virtual key, ctrl, alt, shift -> true when taken
    private readonly HookProc _proc;
    private IntPtr _hook;

    public ClickerHook(Func<int, bool, bool, bool, bool> handle)
    {
        _handle = handle;
        _proc = Callback; // kept in a field: the native hook calls it for as long as it is installed
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero) throw new InvalidOperationException("The keyboard hook could not be installed (error " + Marshal.GetLastWin32Error() + ").");
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown))
        {
            var key = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            // Keys that programs send themselves (SendInput) are not the clicker.
            if ((key.Flags & LlkhfInjected) == 0)
            {
                bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0, alt = (GetAsyncKeyState(0x12) & 0x8000) != 0, shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
                if (_handle((int)key.VirtualKey, ctrl, alt, shift)) return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);
}
