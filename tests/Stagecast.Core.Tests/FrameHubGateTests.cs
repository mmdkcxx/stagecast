using Stagecast.Core.Frames;
using Stagecast.Core.Output;

namespace Stagecast.Core.Tests;

public class FrameHubGateTests
{
    private sealed class Probe : IFrameSink
    {
        public string Name => "probe";
        public byte Red, Alpha;
        public bool Repeated;
        public long Sequence;
        public int Calls;

        public void Deliver(Frame frame, bool repeated)
        {
            Red = frame.Pixels[2];
            Alpha = frame.Pixels[3];
            Sequence = frame.Sequence;
            Repeated = repeated;
            Calls++;
        }
    }

    private static Frame Coloured(FramePool pool, byte red, long sequence)
    {
        var frame = pool.Rent(4, 2, 16);
        for (int i = 0; i < frame.Pixels.Length; i += 4)
        {
            frame.Pixels[i + 2] = red;
            frame.Pixels[i + 3] = 255;
        }
        frame.Sequence = sequence;
        return frame;
    }

    [Fact]
    public void Freeze_RepeatsTheFrameThatWasLiveAndIgnoresNewOnes()
    {
        var hub = new FrameHub();
        var pool = new FramePool();
        var probe = new Probe();
        hub.AddSink(probe);
        hub.Publish(Coloured(pool, 10, 1));
        hub.Tick();
        hub.Gate = GateMode.Freeze;
        hub.Publish(Coloured(pool, 200, 2));
        hub.Tick();

        Assert.Equal(10, probe.Red);
        Assert.Equal(1, probe.Sequence);
        Assert.True(probe.Repeated);
    }

    [Fact]
    public void LeavingFreeze_ShowsTheNewestFrameAsNew()
    {
        var hub = new FrameHub();
        var pool = new FramePool();
        var probe = new Probe();
        hub.AddSink(probe);
        hub.Publish(Coloured(pool, 10, 1));
        hub.Tick();
        hub.Gate = GateMode.Freeze;
        hub.Publish(Coloured(pool, 200, 2));
        hub.Tick();
        hub.Gate = GateMode.Live;
        hub.Tick(); // the page did not change since the last publish

        Assert.Equal(200, probe.Red);
        Assert.False(probe.Repeated); // a sink that skips repeats must still redraw
        hub.Tick();
        Assert.True(probe.Repeated);
    }

    [Fact]
    public void Blackout_SendsOpaqueBlack_ThenLiveComesBack()
    {
        var hub = new FrameHub();
        var pool = new FramePool();
        var probe = new Probe();
        hub.AddSink(probe);
        hub.Publish(Coloured(pool, 90, 1));
        hub.Tick();
        hub.Gate = GateMode.Blackout;
        hub.Tick();
        Assert.Equal(0, probe.Red);
        Assert.Equal(255, probe.Alpha);
        Assert.False(probe.Repeated); // first black frame
        hub.Tick();
        Assert.True(probe.Repeated);

        hub.Gate = GateMode.Live;
        hub.Tick();
        Assert.Equal(90, probe.Red);
        Assert.False(probe.Repeated);
    }

    [Fact]
    public void Blackout_FollowsAResolutionChange()
    {
        var hub = new FrameHub();
        var pool = new FramePool();
        var sizes = new List<(int, int)>();
        hub.AddSink(new CallbackFrameSink((f, _) => sizes.Add((f.Width, f.Height))));
        hub.Publish(Coloured(pool, 1, 1));
        hub.Tick();
        hub.Gate = GateMode.Blackout;
        hub.Tick();
        var bigger = pool.Rent(8, 4, 32);
        hub.Publish(bigger);
        hub.Tick();
        Assert.Equal((8, 4), sizes[^1]);
    }

    private sealed class CallbackFrameSink(Action<Frame, bool> action) : IFrameSink
    {
        public string Name => "cb";
        public void Deliver(Frame frame, bool repeated) => action(frame, repeated);
    }
}
