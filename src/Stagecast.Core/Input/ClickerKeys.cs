namespace Stagecast.Core.Input;

public enum ClickerCommand
{
    None,
    Next,
    Previous,
    Blackout,

    /// <summary>Back to the first page.</summary>
    Restart,
}

/// <summary>
/// What a common presentation clicker sends (it looks like a keyboard to Windows) and what Stagecast does with each key. Keys are
/// Windows virtual-key codes so this stays free of any UI library. Esc is not used.
/// </summary>
public static class ClickerKeys
{
    public const int Back = 0x08, Enter = 0x0D, Space = 0x20, PageUp = 0x21, PageDown = 0x22, Left = 0x25, Right = 0x27, B = 0x42, F5 = 0x74, Period = 0xBE;

    public static ClickerCommand Map(int virtualKey, bool ctrl = false, bool alt = false, bool shift = false)
    {
        // Ctrl or Alt means the key belongs to some other shortcut (Ctrl+Shift+B is the Blackout hotkey on its own).
        if (ctrl || alt) return ClickerCommand.None;
        if (shift && virtualKey != B) return ClickerCommand.None;
        return virtualKey switch
        {
            PageDown or Right or Space or Enter => ClickerCommand.Next,
            PageUp or Left or Back => ClickerCommand.Previous,
            B or Period => ClickerCommand.Blackout,
            F5 => ClickerCommand.Restart,
            _ => ClickerCommand.None,
        };
    }

    /// <summary>
    /// Keys the global mode takes over (also while another program has the focus). Only the page keys: Space, Enter, Backspace, B and
    /// the full stop would break typing everywhere else.
    /// </summary>
    public static bool IsGlobalKey(int virtualKey) => virtualKey is PageDown or PageUp or Right or Left;
}
