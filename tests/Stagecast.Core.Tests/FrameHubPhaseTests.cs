using Stagecast.Core.Frames;

namespace Stagecast.Core.Tests;

public class FrameHubPhaseTests
{
    private sealed class StepTime : TimeProvider
    {
        private long _ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _ms;
        public void Advance(long ms) => _ms += ms;
    }

    private sealed class Nothing : IFrameSink
    {
        public string Name => "nothing";
        public void Deliver(Frame frame, bool repeated) { }
    }

    private static FrameHub Hub(StepTime time)
    {
        var hub = new FrameHub(time);
        hub.SetPatternFormat(1920, 1080, 50); // a 20 ms interval keeps the manual clock in whole milliseconds
        hub.AddSink(new Nothing());
        return hub;
    }

    private static void Cycles(FrameHub hub, StepTime time, FramePool pool, int count, int publishAfterTickMs)
    {
        for (int i = 0; i < count; i++)
        {
            time.Advance(1);
            hub.Tick();
            time.Advance(publishAfterTickMs);
            hub.Publish(pool.Rent(4, 2, 16));
            time.Advance(20 - 1 - publishAfterTickMs);
        }
    }

    [Fact]
    public void Frames_arriving_too_soon_after_the_tick_ask_for_earlier_ticks()
    {
        var time = new StepTime();
        var hub = Hub(time);
        Cycles(hub, time, new FramePool(), 12, publishAfterTickMs: 2); // target is 10 ms after the tick
        Assert.True(hub.TakePhaseCorrectionMs() < 0);
    }

    [Fact]
    public void Frames_arriving_too_late_after_the_tick_ask_for_later_ticks()
    {
        var time = new StepTime();
        var hub = Hub(time);
        Cycles(hub, time, new FramePool(), 12, publishAfterTickMs: 17);
        Assert.True(hub.TakePhaseCorrectionMs() > 0);
    }

    [Fact]
    public void Frames_at_the_target_phase_ask_for_nothing()
    {
        var time = new StepTime();
        var hub = Hub(time);
        Cycles(hub, time, new FramePool(), 12, publishAfterTickMs: 10);
        Assert.Equal(0, hub.TakePhaseCorrectionMs());
    }

    [Fact]
    public void Changing_the_rate_forgets_the_phase()
    {
        var time = new StepTime();
        var hub = Hub(time);
        Cycles(hub, time, new FramePool(), 12, publishAfterTickMs: 2);
        hub.SetPatternFormat(1920, 1080, 25);
        Assert.Equal(0, hub.TakePhaseCorrectionMs());
    }

    [Fact]
    public void Publishing_before_any_tick_is_ignored()
    {
        var time = new StepTime();
        var hub = Hub(time);
        var pool = new FramePool();
        for (int i = 0; i < 12; i++) { time.Advance(20); hub.Publish(pool.Rent(4, 2, 16)); }
        Assert.Equal(0, hub.TakePhaseCorrectionMs());
    }
}
