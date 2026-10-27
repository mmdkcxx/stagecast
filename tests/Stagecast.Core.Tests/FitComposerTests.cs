using Stagecast.Core.Frames;

namespace Stagecast.Core.Tests;

public class FitComposerTests
{
    [Theory]
    [InlineData(1920, 1080, 2520, 1080, 1920, 1080, 300, 0)]
    [InlineData(1024, 768, 2520, 1080, 1440, 1080, 540, 0)]
    [InlineData(1280, 720, 1920, 1080, 1920, 1080, 0, 0)]
    [InlineData(1280, 720, 3840, 2160, 3840, 2160, 0, 0)]
    [InlineData(1024, 768, 1920, 1080, 1440, 1080, 240, 0)]
    [InlineData(612, 792, 1920, 1080, 835, 1080, 542, 0)]
    [InlineData(1920, 1080, 1080, 1920, 1080, 608, 0, 656)]
    public void Fit_places_the_picture_in_the_centre_without_changing_its_ratio(double sw, double sh, int cw, int ch, int w, int h, int x, int y)
    {
        FitLayout layout = FitComposer.Fit(sw, sh, cw, ch);
        Assert.Equal((x, y, w, h), (layout.X, layout.Y, layout.Width, layout.Height));
        Assert.Equal(cw, layout.CanvasWidth);
        Assert.Equal(ch, layout.CanvasHeight);
    }

    [Fact]
    public void Bars_add_up_to_the_canvas()
    {
        FitLayout layout = FitComposer.Fit(1024, 768, 2520, 1080);
        Assert.Equal(540, layout.BarLeft);
        Assert.Equal(540, layout.BarRight);
        Assert.Equal(0, layout.BarTop);
        Assert.Equal(0, layout.BarBottom);
        Assert.True(layout.HasBars);
        Assert.False(FitComposer.Fit(1920, 1080, 1920, 1080).HasBars);
    }

    [Fact]
    public void Odd_leftovers_leave_the_extra_pixel_on_the_right()
    {
        FitLayout layout = FitComposer.Fit(1000, 1000, 2521, 1080);
        Assert.Equal(1080, layout.Width);
        Assert.Equal(720, layout.BarLeft);
        Assert.Equal(721, layout.BarRight);
    }

    [Fact]
    public void A_picture_smaller_than_the_canvas_is_scaled_up()
    {
        FitLayout layout = FitComposer.Fit(100, 50, 2520, 1080);
        Assert.Equal(2160, layout.Width);
        Assert.Equal(1080, layout.Height);
    }

    [Theory]
    [InlineData(0, 100, 1920, 1080)]
    [InlineData(100, -1, 1920, 1080)]
    [InlineData(double.NaN, 100, 1920, 1080)]
    [InlineData(100, 100, 0, 1080)]
    [InlineData(100, 100, 1920, 0)]
    public void Fit_rejects_empty_sizes(double sw, double sh, int cw, int ch)
        => Assert.Throws<ArgumentOutOfRangeException>(() => FitComposer.Fit(sw, sh, cw, ch));
}
