using Stagecast.Core.Frames;

namespace Stagecast.Core.Tests;

public class TestPatternTests
{
    private static (byte R, byte G, byte B) At(Frame f, double fx, double fy)
    {
        int x = (int)(f.Width * fx), y = (int)(f.Height * fy), o = y * f.Stride + x * 4;
        return (f.Pixels[o + 2], f.Pixels[o + 1], f.Pixels[o]);
    }

    [Theory]
    [InlineData(2520, 1080)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void Renders_ColourBarsAndRamp_AtAnySize(int w, int h)
    {
        var pattern = new TestPattern();
        using var _ = new Releaser(pattern.Render(w, h, 60, TimeSpan.Zero), out Frame f);
        Assert.Equal((w, h), (f.Width, f.Height));
        Assert.Equal(((byte)255, (byte)255, (byte)255), At(f, 0.5 / 7, 0.2)); // white bar
        Assert.Equal(((byte)255, (byte)0, (byte)0), At(f, 5.5 / 7, 0.2));     // red bar
        Assert.Equal(((byte)0, (byte)0, (byte)255), At(f, 6.5 / 7, 0.2));    // blue bar
        var dark = At(f, 0.5 / 11 * 1.0, 0.48);
        var light = At(f, 10.5 / 11, 0.48);
        Assert.True(dark.R < 40 && light.R > 215, "the grey ramp goes from dark to light");
    }

    [Fact]
    public void EveryPixel_IsOpaque()
    {
        var pattern = new TestPattern();
        using var _ = new Releaser(pattern.Render(640, 360, 30, TimeSpan.FromSeconds(3.5)), out Frame f);
        for (int i = 3; i < f.Pixels.Length; i += 4) Assert.Equal(255, f.Pixels[i]);
    }

    [Fact]
    public void BeepBox_FlashesOnlyDuringTheFirst100Ms_OfEachSecond()
    {
        Assert.True(TestPattern.BeepOn(TimeSpan.FromMilliseconds(5000)));
        Assert.True(TestPattern.BeepOn(TimeSpan.FromMilliseconds(5099)));
        Assert.False(TestPattern.BeepOn(TimeSpan.FromMilliseconds(5100)));
        Assert.False(TestPattern.BeepOn(TimeSpan.FromMilliseconds(5900)));
    }

    [Fact]
    public void MovingPart_ChangesBetweenFrames_ButTheBarsDoNot()
    {
        var pattern = new TestPattern();
        using var a = new Releaser(pattern.Render(1280, 720, 60, TimeSpan.Zero), out Frame f1);
        using var b = new Releaser(pattern.Render(1280, 720, 60, TimeSpan.FromMilliseconds(500)), out Frame f2);
        int top = (int)(720 * 0.84) * f1.Stride;
        Assert.False(f1.Pixels.AsSpan(top).SequenceEqual(f2.Pixels.AsSpan(top)), "the bottom strip moves");
        Assert.True(f1.Pixels.AsSpan(0, top).Slice(0, (int)(720 * 0.40) * f1.Stride).SequenceEqual(f2.Pixels.AsSpan(0, (int)(720 * 0.40) * f2.Stride)), "the bars stay");
    }

    private sealed class Releaser : IDisposable
    {
        private readonly Frame _frame;

        public Releaser(Frame frame, out Frame same)
        {
            _frame = frame;
            same = frame;
        }

        public void Dispose() => _frame.Release();
    }
}
