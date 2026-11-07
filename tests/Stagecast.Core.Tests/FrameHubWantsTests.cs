using Stagecast.Core.Frames;

namespace Stagecast.Core.Tests;

public class FrameHubWantsTests
{
    private sealed class ManualTime : TimeProvider
    {
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => 0;
    }

    private sealed class ToggleSink : IFrameSink
    {
        public string Name => "toggle";
        public bool Wants { get; set; } = true;
        public bool WantsFrames => Wants;
        public List<(Frame Frame, bool Repeated)> Calls { get; } = [];
        public void Deliver(Frame frame, bool repeated) => Calls.Add((frame, repeated));
    }

    private static Frame NewFrame(FramePool pool) => pool.Rent(4, 2, 16);

    [Fact]
    public void Hub_with_no_sink_does_not_want_frames_and_with_a_wanting_sink_it_does()
    {
        var hub = new FrameHub(new ManualTime());
        hub.Tick();
        Assert.False(hub.WantsFrames);

        hub.AddSink(new ToggleSink());
        hub.Tick();
        Assert.True(hub.WantsFrames);
    }

    [Fact]
    public void Sink_that_does_not_want_frames_is_not_called()
    {
        var pool = new FramePool();
        var hub = new FrameHub(new ManualTime());
        var sink = new ToggleSink { Wants = false };
        hub.AddSink(sink);

        hub.Publish(NewFrame(pool));
        hub.Tick();
        hub.Tick();

        Assert.Empty(sink.Calls);
        Assert.False(hub.WantsFrames);
    }

    [Fact]
    public void A_sink_that_wants_frames_from_the_start_causes_no_event_and_keeps_the_waiting_frame()
    {
        var pool = new FramePool();
        var hub = new FrameHub(new ManualTime());
        var sink = new ToggleSink();
        hub.AddSink(sink);
        int raised = 0;
        hub.FramesWanted += () => raised++;

        hub.Publish(NewFrame(pool));
        hub.Tick();

        Assert.Equal(0, raised);
        Assert.Single(sink.Calls);
        Assert.False(sink.Calls[0].Repeated);
    }

    [Fact]
    public void When_a_sink_starts_to_want_frames_old_ones_are_dropped_and_the_event_fires_once()
    {
        var pool = new FramePool();
        var hub = new FrameHub(new ManualTime());
        var sink = new ToggleSink { Wants = false };
        hub.AddSink(sink);
        int raised = 0;
        hub.FramesWanted += () => raised++;

        var stale = NewFrame(pool);
        hub.Publish(stale);
        hub.Tick(); // nobody wants it

        sink.Wants = true; // a receiver connects
        hub.Tick();
        hub.Tick();
        Assert.Equal(1, raised);
        Assert.Empty(sink.Calls); // the old picture never goes out

        var fresh = NewFrame(pool);
        hub.Publish(fresh);
        hub.Tick();
        Assert.Single(sink.Calls);
        Assert.Same(fresh, sink.Calls[0].Frame);
        Assert.False(sink.Calls[0].Repeated);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void The_event_fires_again_after_the_wanting_stops_and_restarts()
    {
        var hub = new FrameHub(new ManualTime());
        var sink = new ToggleSink { Wants = false };
        hub.AddSink(sink);
        int raised = 0;
        hub.FramesWanted += () => raised++;

        hub.Tick();
        sink.Wants = true; hub.Tick();
        sink.Wants = false; hub.Tick();
        sink.Wants = true; hub.Tick();

        Assert.Equal(2, raised);
    }
}
