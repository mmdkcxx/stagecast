using Stagecast.Core.Rendering;

namespace Stagecast.Core.Tests;

public class ResolutionTests
{
    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(2520, true)]
    [InlineData(8192, true)]
    [InlineData(8193, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void IsValidSide_UsesTheProposedBounds(int side, bool expected)
    {
        Assert.Equal(expected, Resolution.IsValidSide(side));
    }

    [Fact]
    public void TryCreate_LedWall_Succeeds()
    {
        Assert.True(Resolution.TryCreate(2520, 1080, out var r));
        Assert.Equal(new Resolution(2520, 1080), r);
    }

    [Theory]
    [InlineData(99, 1080)]
    [InlineData(2520, 8193)]
    public void TryCreate_OutOfRange_Fails(int w, int h)
    {
        Assert.False(Resolution.TryCreate(w, h, out _));
    }

    [Fact]
    public void ToString_UsesTheMultiplicationSign()
    {
        Assert.Equal("2520 × 1080", new Resolution(2520, 1080).ToString());
    }
}
