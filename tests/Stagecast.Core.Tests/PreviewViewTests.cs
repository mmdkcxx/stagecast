using Stagecast.Core.Display;
using Stagecast.Core.Rendering;

namespace Stagecast.Core.Tests;

public class PreviewViewTests
{
    private static readonly Resolution Wide = new(2520, 1080);

    [Fact]
    public void StartsFitted()
    {
        var view = new PreviewView();
        Assert.Equal(ViewMode.Fit, view.Mode);
        Assert.False(view.IsCropped(Wide, 1280, 720));
    }

    [Fact]
    public void ShowActual_StartsAtPageCentre()
    {
        var view = new PreviewView();
        view.ShowActual(Wide, 1280, 720);
        Assert.Equal(1260, view.CenterX);
        Assert.Equal(540, view.CenterY);
        Assert.True(view.IsCropped(Wide, 1280, 720));
    }

    [Fact]
    public void PanBy_MovesThePageWithThePointer()
    {
        var view = new PreviewView();
        view.ShowActual(Wide, 1280, 720);
        view.PanBy(Wide, 1280, 720, -100, -50); // drag left and up: we now see more of the right and bottom
        Assert.Equal(1360, view.CenterX);
        Assert.Equal(590, view.CenterY);
    }

    [Fact]
    public void PanBy_CannotLeaveThePage()
    {
        var view = new PreviewView();
        view.ShowActual(Wide, 1280, 720);
        view.PanBy(Wide, 1280, 720, 99999, 99999);
        Assert.Equal(640, view.CenterX); // left edge of the page at the left edge of the control
        Assert.Equal(360, view.CenterY);
        view.PanBy(Wide, 1280, 720, -50, 0);
        Assert.Equal(690, view.CenterX); // no hidden distance was built up by the overshoot
    }

    [Fact]
    public void PanBy_IgnoredWhileFitted()
    {
        var view = new PreviewView();
        view.PanBy(Wide, 1280, 720, 100, 100);
        Assert.Equal(ViewMode.Fit, view.Mode);
        Assert.Equal(0, view.CenterX);
    }

    [Fact]
    public void ActualInALargeControl_ShowsWholePageCentred_NotCropped()
    {
        var view = new PreviewView();
        view.ShowActual(Wide, 3000, 1200);
        Assert.False(view.IsCropped(Wide, 3000, 1200));
        var t = view.Transform(Wide, 3000, 1200);
        Assert.Equal(1.0, t.Scale);
        Assert.Equal(240, t.OffsetX);
        Assert.Equal(60, t.OffsetY);
    }

    [Fact]
    public void Visible_IsTheControlSizedWindowOnThePage()
    {
        var view = new PreviewView();
        view.ShowActualAt(Wide, 1280, 720, 1500, 600);
        var (x, y, w, h) = view.Visible(Wide, 1280, 720);
        Assert.Equal(1280, w);
        Assert.Equal(720, h);
        Assert.Equal(860, x, 0.001);
        Assert.Equal(240, y, 0.001);
    }

    [Fact]
    public void Visible_ClampsAtTheRightAndBottomEdge()
    {
        var view = new PreviewView();
        view.ShowActualAt(Wide, 1280, 720, 99999, 99999);
        var (x, y, w, h) = view.Visible(Wide, 1280, 720);
        Assert.Equal(Wide.Width, x + w, 0.001);
        Assert.Equal(Wide.Height, y + h, 0.001);
    }

    [Theory]
    [InlineData(2520, 1080, 1260, 540, 1)] // fit 0.5 -> half size is enough
    [InlineData(2520, 1080, 2520, 1080, 0)] // fit 1.0
    [InlineData(2520, 1080, 5000, 3000, 0)] // would upscale: full size
    [InlineData(2520, 1080, 630, 270, 2)] // fit 0.25
    [InlineData(2520, 1080, 400, 170, 2)] // fit 0.158 -> 2^-2 = 0.25 is the smallest not below it
    [InlineData(8192, 8192, 100, 100, 4)] // capped at the deepest level
    public void Region_Fit_PicksTheSmallestMipThatIsNotBelowTheDisplayedSize(int rw, int rh, int cw, int ch, int mip)
    {
        var view = new PreviewView();
        var region = view.Region(new Resolution(rw, rh), cw, ch);
        Assert.Equal(new PreviewRegion(0, 0, rw, rh, mip), region);
    }

    [Fact]
    public void Region_Actual_IsFullResolutionAndInsidePage()
    {
        var view = new PreviewView();
        view.ShowActualAt(Wide, 1280, 720, 1260, 540);
        var region = view.Region(Wide, 1280, 720);
        Assert.Equal(0, region.Mip);
        Assert.Equal(620, region.X);
        Assert.Equal(180, region.Y);
        Assert.Equal(1280, region.Width);
        Assert.Equal(720, region.Height);

        view.ShowActualAt(Wide, 1280, 720, 99999, 99999);
        region = view.Region(Wide, 1280, 720);
        Assert.True(region.X + region.Width <= Wide.Width);
        Assert.True(region.Y + region.Height <= Wide.Height);
    }

    [Fact]
    public void ShowActualAt_RoundTripsThroughTheTransform()
    {
        var view = new PreviewView();
        view.ShowActualAt(Wide, 1280, 720, 900, 400);
        var t = view.Transform(Wide, 1280, 720);
        var (px, py) = t.ToPage(640, 360);
        Assert.Equal(900, px, 0.001);
        Assert.Equal(400, py, 0.001);
    }
}
