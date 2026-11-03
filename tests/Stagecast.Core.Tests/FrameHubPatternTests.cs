using Stagecast.Core.Frames;
using Stagecast.Core.Output;

namespace Stagecast.Core.Tests;

public class FrameHubPatternTests
{
    private sealed class Probe : IFrameSink
    {
        public string Name => "probe";
        public int Width, Calls;
        public byte Red, Green, Blue, Alpha;
        public bool Repeated;

        public void Deliver(Frame frame, bool repeated)
        {
            Width = frame.Width;
            (Red, Green, Blue, Alpha) = (frame.Pixels[2], frame.Pixels[1], frame.Pixels[0], frame.Pixels[3]);
            Repeated = repeated;
            Calls++;
        }
    }

    private static Frame Solid(FramePool pool, byte red)
    {
        var frame = pool.Rent(64, 36, 256);
        for (int i = 0; i < frame.Pixels.Length; i += 4) { frame.Pixels[i + 2] = red; frame.Pixels[i + 3] = 255; }
        return frame;
    }

    [Fact]
    public void NoLiveTab_EverySinkShowsThePattern_EvenBeforeAnyFrameExists()
    {
        var hub = new FrameHub();
        hub.SetPatternFormat(320, 180, 30);
        var probe = new Probe();
        hub.AddSink(probe);
        Assert.False(hub.Tick()); // nothing live and no pattern wanted: nothing to send
        hub.NoLive = true;
        Assert.True(hub.Tick());
        Assert.Equal(320, probe.Width);
        Assert.False(probe.Repeated);
        Assert.Equal(255, probe.Red); // the white bar at the top left
    }

    [Fact]
    public void ASinkThatAsksForThePattern_GetsItWhileOthersKeepTheLivePicture()
    {
        var hub = new FrameHub();
        hub.SetPatternFormat(320, 180, 30);
        var pool = new FramePool();
        bool pattern = false;
        Probe screen = new(), ndi = new();
        hub.AddSink(screen, () => pattern);
        hub.AddSink(ndi);
        hub.Publish(Solid(pool, 10));
        hub.Tick();
        Assert.Equal((64, 10), (screen.Width, screen.Red));
        Assert.Equal((64, 10), (ndi.Width, ndi.Red));

        pattern = true;
        hub.Publish(Solid(pool, 20));
        hub.Tick();
        Assert.Equal(320, screen.Width);
        Assert.Equal((64, 20), (ndi.Width, ndi.Red));

        pattern = false;
        hub.Tick();
        Assert.Equal(64, screen.Width);
    }

    [Fact]
    public void Blackout_BeatsThePattern_AndIsOpaque()
    {
        var hub = new FrameHub();
        hub.SetPatternFormat(320, 180, 30);
        var probe = new Probe();
        hub.AddSink(probe);
        hub.NoLive = true;
        hub.Gate = GateMode.Blackout;
        Assert.True(hub.Tick());
        Assert.Equal((0, 0, 0, 255), (probe.Red, probe.Green, probe.Blue, probe.Alpha));
        Assert.Equal(320, probe.Width);
    }

    [Fact]
    public void Freeze_DoesNotFreezeThePattern()
    {
        var hub = new FrameHub();
        hub.SetPatternFormat(320, 180, 30);
        var probe = new Probe();
        hub.AddSink(probe);
        hub.NoLive = true;
        hub.Gate = GateMode.Freeze;
        hub.Tick();
        Assert.False(probe.Repeated);
        Assert.Equal(255, probe.Red);
    }
}
