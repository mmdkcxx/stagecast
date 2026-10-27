using Stagecast.Core.Display;
using Stagecast.Core.Rendering;

namespace Stagecast.Core.Tests;

public class ViewTransformTests
{
    private static readonly Resolution Led = new(2520, 1080);

    [Fact]
    public void Fit_ScalesToTheLimitingSideAndCentres()
    {
        var t = ViewTransform.Create(Led, 1260, 800, ViewMode.Fit);
        Assert.Equal(0.5, t.Scale, 6);
        Assert.Equal(0, t.OffsetX, 6);
        Assert.Equal((800 - 540) / 2.0, t.OffsetY, 6);
    }

    [Fact]
    public void Fit_ControlCentreMapsToPageCentre()
    {
        var t = ViewTransform.Create(Led, 1000, 700, ViewMode.Fit);
        var (x, y) = t.ToPage(500, 350);
        Assert.Equal(1260, x, 6);
        Assert.Equal(540, y, 6);
    }

    [Theory]
    [InlineData(ViewMode.Fit, 0, 0)]
    [InlineData(ViewMode.Actual, 600, 300)]
    [InlineData(ViewMode.Actual, 2400, 1000)]
    public void ToControlThenToPage_RoundTrips(ViewMode mode, double cx, double cy)
    {
        var t = ViewTransform.Create(Led, 1280, 720, mode, cx, cy);
        var (controlX, controlY) = t.ToControl(1234.5, 678.25);
        var (x, y) = t.ToPage(controlX, controlY);
        Assert.Equal(1234.5, x, 6);
        Assert.Equal(678.25, y, 6);
    }

    [Fact]
    public void Actual_CentresTheChosenPoint()
    {
        var t = ViewTransform.Create(Led, 1000, 600, ViewMode.Actual, 1000, 400);
        Assert.Equal(1.0, t.Scale);
        var (x, y) = t.ToPage(500, 300);
        Assert.Equal(1000, x, 6);
        Assert.Equal(400, y, 6);
    }

    [Fact]
    public void Actual_DoesNotPanPastTheEdges()
    {
        var left = ViewTransform.Create(Led, 1000, 600, ViewMode.Actual, -500, -500);
        Assert.Equal(0, left.OffsetX);
        Assert.Equal(0, left.OffsetY);

        var right = ViewTransform.Create(Led, 1000, 600, ViewMode.Actual, 9999, 9999);
        Assert.Equal(1000 - 2520, right.OffsetX);
        Assert.Equal(600 - 1080, right.OffsetY);
    }

    [Fact]
    public void Actual_CentresWhenThePageIsSmallerThanTheControl()
    {
        var t = ViewTransform.Create(new Resolution(800, 400), 1000, 600, ViewMode.Actual, 0, 0);
        Assert.Equal(100, t.OffsetX);
        Assert.Equal(100, t.OffsetY);
    }

    [Fact]
    public void Contains_IsFalseOutsideThePage()
    {
        var t = ViewTransform.Create(Led, 1260, 800, ViewMode.Fit);
        Assert.True(t.Contains(Led, 10, 400));
        Assert.False(t.Contains(Led, 10, 5)); // letterbox above the page
        Assert.False(t.Contains(Led, 1260, 400));
    }

    [Fact]
    public void Create_RejectsAnEmptyControl()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ViewTransform.Create(Led, 0, 100, ViewMode.Fit));
    }
}
