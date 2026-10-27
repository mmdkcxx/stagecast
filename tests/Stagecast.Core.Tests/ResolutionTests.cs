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

public class RenderSurfaceTests
{
    [Theory]
    [InlineData(2520, 1080, 1.0, 2520, 1080)]
    [InlineData(2520, 1080, 1.25, 3150, 1350)]
    [InlineData(2520, 1080, 1.5, 3780, 1620)]
    [InlineData(2520, 1080, 2.0, 5040, 2160)]
    [InlineData(1080, 1920, 1.25, 1350, 2400)]
    [InlineData(1921, 1081, 1.5, 2882, 1622)]   // 2881.5 and 1621.5 round away from zero
    public void PhysicalSize_IsCssSizeTimesScale(int w, int h, double scale, int expectedW, int expectedH)
    {
        var (pw, ph) = RenderSurface.PhysicalSize(new Resolution(w, h), scale);

        Assert.Equal(expectedW, pw);
        Assert.Equal(expectedH, ph);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void PhysicalSize_RejectsInvalidScale(double scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RenderSurface.PhysicalSize(new Resolution(2520, 1080), scale));
    }
}
