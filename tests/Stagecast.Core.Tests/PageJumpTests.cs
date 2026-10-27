using Stagecast.Core.Tabs;

namespace Stagecast.Core.Tests;

public class PageJumpTests
{
    [Theory]
    [InlineData("1", 40, 0)]
    [InlineData("40", 40, 39)]
    [InlineData(" 7 ", 40, 6)]
    [InlineData("007", 40, 6)]
    public void A_page_in_range_gives_its_index(string text, int count, int index)
    {
        Assert.True(PageJump.TryParse(text, count, out int result));
        Assert.Equal(index, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("41")]
    [InlineData("-3")]
    [InlineData("+3")]
    [InlineData("3.5")]
    [InlineData("abc")]
    [InlineData("3a")]
    [InlineData("99999999999")]
    [InlineData("١٢")]
    public void Anything_else_is_not_a_page(string? text)
    {
        Assert.False(PageJump.TryParse(text, 40, out int result));
        Assert.Equal(-1, result);
    }

    [Fact]
    public void A_document_without_pages_accepts_nothing() => Assert.False(PageJump.TryParse("1", 0, out _));

    [Fact]
    public void The_hint_names_the_range() => Assert.Contains("1 sampai 40", PageJump.Hint(40));
}
