using Stagecast.Core.Input;

namespace Stagecast.Core.Tests;

public class ClickerKeysTests
{
    [Theory]
    [InlineData(ClickerKeys.PageDown, ClickerCommand.Next)]
    [InlineData(ClickerKeys.Right, ClickerCommand.Next)]
    [InlineData(ClickerKeys.Space, ClickerCommand.Next)]
    [InlineData(ClickerKeys.Enter, ClickerCommand.Next)]
    [InlineData(ClickerKeys.PageUp, ClickerCommand.Previous)]
    [InlineData(ClickerKeys.Left, ClickerCommand.Previous)]
    [InlineData(ClickerKeys.Back, ClickerCommand.Previous)]
    [InlineData(ClickerKeys.B, ClickerCommand.Blackout)]
    [InlineData(ClickerKeys.Period, ClickerCommand.Blackout)]
    [InlineData(ClickerKeys.F5, ClickerCommand.Restart)]
    [InlineData(0x1B, ClickerCommand.None)] // Esc is not used
    [InlineData(0x41, ClickerCommand.None)]
    public void Keys_map_to_commands(int key, ClickerCommand command) => Assert.Equal(command, ClickerKeys.Map(key));

    [Fact]
    public void Ctrl_or_Alt_make_a_key_someone_elses_shortcut()
    {
        Assert.Equal(ClickerCommand.None, ClickerKeys.Map(ClickerKeys.B, ctrl: true, shift: true));
        Assert.Equal(ClickerCommand.None, ClickerKeys.Map(ClickerKeys.Right, alt: true));
    }

    [Fact]
    public void Shift_only_counts_for_the_letter_B()
    {
        Assert.Equal(ClickerCommand.Blackout, ClickerKeys.Map(ClickerKeys.B, shift: true));
        Assert.Equal(ClickerCommand.None, ClickerKeys.Map(ClickerKeys.PageDown, shift: true));
    }

    [Theory]
    [InlineData(ClickerKeys.PageDown, true)]
    [InlineData(ClickerKeys.PageUp, true)]
    [InlineData(ClickerKeys.Right, true)]
    [InlineData(ClickerKeys.Left, true)]
    [InlineData(ClickerKeys.Space, false)]
    [InlineData(ClickerKeys.Enter, false)]
    [InlineData(ClickerKeys.Back, false)]
    [InlineData(ClickerKeys.B, false)]
    [InlineData(ClickerKeys.Period, false)]
    [InlineData(ClickerKeys.F5, false)]
    public void Global_mode_only_takes_page_keys(int key, bool global) => Assert.Equal(global, ClickerKeys.IsGlobalKey(key));
}
