using Stagecast.Core.Display;
using Stagecast.Core.Rendering;

namespace Stagecast.Core.Tests;

public class ScaleMathTests
{
    private static readonly Resolution Led = new(2520, 1080);
    private static readonly Resolution Hdmi = new(1920, 1080);

    [Fact]
    public void Squeeze_LedOntoFullHd_NarrowsWidthOnly()
    {
        var r = ScaleMath.Compute(Led, Hdmi, ScaleMode.Squeeze);

        Assert.Equal(1920.0 / 2520.0, r.ScaleX, 6);   // 76.19%
        Assert.Equal(1.0, r.ScaleY, 6);
        Assert.Equal(1920, r.DestWidth);
        Assert.Equal(1080, r.DestHeight);
        Assert.False(r.Cropped);
    }

    [Fact]
    public void Squeeze_RestoreFactorMatchesWhatTheOperatorTypesInResolumeOrVmix()
    {
        var r = ScaleMath.Compute(Led, Hdmi, ScaleMode.Squeeze);

        Assert.Equal(1.3125, r.RestoreX, 6);          // 131.25%
        Assert.Equal(1.0, r.RestoreY, 6);
    }

    [Fact]
    public void Squeeze_RestoredSizeEqualsRenderSize()
    {
        var r = ScaleMath.Compute(Led, Hdmi, ScaleMode.Squeeze);

        Assert.Equal(Led.Width, r.DestWidth * r.RestoreX, 6);
        Assert.Equal(Led.Height, r.DestHeight * r.RestoreY, 6);
    }

    [Fact]
    public void Squeeze_SameSizeAsMonitor_IsIdentity()
    {
        var r = ScaleMath.Compute(Hdmi, Hdmi, ScaleMode.Squeeze);

        Assert.Equal(1.0, r.ScaleX);
        Assert.Equal(1.0, r.RestoreX);
    }

    [Fact]
    public void Squeeze_FourKOntoFullHd_ScalesBothAxes()
    {
        var r = ScaleMath.Compute(new Resolution(3840, 2160), Hdmi, ScaleMode.Squeeze);

        Assert.Equal(0.5, r.ScaleX, 6);
        Assert.Equal(0.5, r.ScaleY, 6);
        Assert.Equal(2.0, r.RestoreX, 6);
        Assert.Equal(2.0, r.RestoreY, 6);
    }

    [Fact]
    public void Fit_KeepsAspectRatioAndLeavesBlackBars()
    {
        var r = ScaleMath.Compute(Led, Hdmi, ScaleMode.Fit);

        Assert.Equal(r.ScaleX, r.ScaleY);
        Assert.Equal(1920.0 / 2520.0, r.ScaleX, 6);
        Assert.Equal(1920, r.DestWidth, 6);
        Assert.Equal(1080 * 1920.0 / 2520.0, r.DestHeight, 6);
        Assert.True(r.DestHeight < 1080);             // black bars top and bottom
        Assert.Equal(1.0, r.RestoreX);
    }

    [Fact]
    public void Fit_VerticalRenderOnLandscapeMonitor_LimitedByHeight()
    {
        var r = ScaleMath.Compute(new Resolution(1080, 1920), Hdmi, ScaleMode.Fit);

        Assert.Equal(1080.0 / 1920.0, r.ScaleX, 6);
        Assert.Equal(1080, r.DestHeight, 6);
    }

    [Fact]
    public void OneToOne_LargerThanMonitor_IsCropped()
    {
        var r = ScaleMath.Compute(Led, Hdmi, ScaleMode.OneToOne);

        Assert.Equal(1.0, r.ScaleX);
        Assert.Equal(2520, r.DestWidth);
        Assert.True(r.Cropped);
    }

    [Fact]
    public void OneToOne_SmallerThanMonitor_IsNotCropped()
    {
        var r = ScaleMath.Compute(new Resolution(1280, 720), Hdmi, ScaleMode.OneToOne);

        Assert.False(r.Cropped);
    }

    [Fact]
    public void UnknownMode_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScaleMath.Compute(Led, Hdmi, (ScaleMode)99));
    }
}
