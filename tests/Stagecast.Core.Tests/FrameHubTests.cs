using Stagecast.Core.Frames;

namespace Stagecast.Core.Tests;

public class FrameHubTests
{
    /// <summary>A clock the test moves by hand, in milliseconds.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _ticks;
        public void Advance(long ms) => _ticks += ms;
    }

    private sealed class RecordingSink : IFrameSink
    {
        public string Name => "recording";
        public List<(Frame Frame, bool Repeated)> Calls { get; } = [];
        public void Deliver(Frame frame, bool repeated) => Calls.Add((frame, repeated));
    }

    private sealed class ThrowingSink : IFrameSink
    {
        public string Name => "throwing";
        public void Deliver(Frame frame, bool repeated) => throw new InvalidOperationException("boom");
    }

    private static Frame NewFrame(FramePool pool, long capturedAt = 0, int width = 4, int height = 2)
    {
        var frame = pool.Rent(width, height, width * 4);
        frame.CapturedAt = capturedAt;
        return frame;
    }

    [Fact]
    public void Tick_BeforeAnyFrame_IsIdleAndSendsNothing()
    {
        var hub = new FrameHub(new ManualTime());
        var sink = new RecordingSink();
        hub.AddSink(sink);

        Assert.False(hub.Tick());
        Assert.Empty(sink.Calls);
        Assert.Equal(1, hub.Snapshot().Idle);
    }

    [Fact]
    public void Tick_SendsTheNewestFrame_ThenRepeatsItForAStillPage()
    {
        var pool = new FramePool();
        var hub = new FrameHub(new ManualTime());
        var sink = new RecordingSink();
        hub.AddSink(sink);
        var frame = NewFrame(pool);

        hub.Publish(frame);
        Assert.True(hub.Tick());
        Assert.True(hub.Tick());
        Assert.True(hub.Tick());

        Assert.Equal([false, true, true], sink.Calls.Select(c => c.Repeated).ToArray());
        Assert.All(sink.Calls, c => Assert.Same(frame, c.Frame));
        var stats = hub.Snapshot();
        Assert.Equal(1, stats.Delivered);
        Assert.Equal(2, stats.Repeated);
    }

    [Fact]
    public void Publish_SeveralTimesBetweenTicks_SendsOnlyTheNewest_AndCountsTheDropped()
    {
        var pool = new FramePool();
        var hub = new FrameHub(new ManualTime());
        var sink = new RecordingSink();
        hub.AddSink(sink);
        var f1 = NewFrame(pool);
        var f2 = NewFrame(pool);
        var f3 = NewFrame(pool);

        hub.Publish(f1);
        hub.Publish(f2);
        hub.Publish(f3);
        hub.Tick();

        Assert.Same(f3, Assert.Single(sink.Calls).Frame);
        var stats = hub.Snapshot();
        Assert.Equal(3, stats.Published);
        Assert.Equal(2, stats.Dropped);
        Assert.Equal(1, stats.Delivered);
    }

    [Fact]
    public void Buffers_ReturnToThePoolOnlyWhenNobodyHoldsTheFrame()
    {
        var pool = new FramePool();
        var hub = new FrameHub(new ManualTime());

        hub.Publish(NewFrame(pool));
        hub.Tick();
        hub.Publish(NewFrame(pool)); // replaces the held frame on the next tick
        hub.Tick();
        hub.Publish(NewFrame(pool));
        hub.Tick();
        hub.Publish(NewFrame(pool));
        hub.Tick();

        // At most the held frame and the one being published exist at once, so reuse keeps allocation at 2.
        Assert.True(pool.Allocated <= 2, $"allocated {pool.Allocated} buffers");
    }

    [Fact]
    public void ASinkThatKeepsAFrame_KeepsItsPixelsValid()
    {
        var pool = new FramePool();
        var hub = new FrameHub(new ManualTime());
        var first = NewFrame(pool);
        first.Pixels[0] = 42;
        Frame? kept = null;
        hub.AddSink(new CallbackSink(f =>
        {
            if (kept is not null) return;
            kept = f;
            f.Retain();
        }));

        hub.Publish(first);
        hub.Tick();
        hub.Publish(NewFrame(pool));
        hub.Tick(); // the hub lets go of `first`, but the sink still holds it

        Assert.NotNull(kept);
        Assert.Equal(42, kept!.Pixels[0]);
        var other = NewFrame(pool);
        Assert.NotSame(kept.Pixels, other.Pixels);
        kept.Release();
    }

    private sealed class CallbackSink(Action<Frame> onFrame) : IFrameSink
    {
        public string Name => "callback";
        public void Deliver(Frame frame, bool repeated) => onFrame(frame);
    }

    [Fact]
    public void AFailingSink_DoesNotStopTheOthers()
    {
        var pool = new FramePool();
        var hub = new FrameHub(new ManualTime());
        var good = new RecordingSink();
        hub.AddSink(new ThrowingSink());
        hub.AddSink(good);

        hub.Publish(NewFrame(pool));
        hub.Tick();

        Assert.Single(good.Calls);
        Assert.Equal(1, hub.Snapshot().SinkErrors);
    }

    [Fact]
    public void RemovedSinks_ReceiveNothingMore()
    {
        var pool = new FramePool();
        var hub = new FrameHub(new ManualTime());
        var sink = new RecordingSink();
        hub.AddSink(sink);
        hub.Publish(NewFrame(pool));
        hub.Tick();

        hub.RemoveSink(sink);
        hub.Tick();

        Assert.Single(sink.Calls);
    }

    [Fact]
    public void Stats_MeasureFrameAgeAndTickSpacing()
    {
        var time = new ManualTime();
        var pool = new FramePool();
        var hub = new FrameHub(time);
        hub.AddSink(new RecordingSink());

        hub.Publish(NewFrame(pool, capturedAt: time.GetTimestamp()));
        time.Advance(5);
        hub.Tick();        // frame is 5 ms old
        time.Advance(17);
        hub.Publish(NewFrame(pool, capturedAt: time.GetTimestamp()));
        time.Advance(10);
        hub.Tick();        // 27 ms after the previous tick, frame 10 ms old

        var stats = hub.Snapshot();
        Assert.Equal(7.5, stats.AverageAgeMs, 6);
        Assert.Equal(10, stats.MaxAgeMs, 6);
        Assert.Equal(27, stats.TickMaxMs, 6);
        Assert.Equal(0.032, stats.Seconds, 6);
    }

    [Fact]
    public void ResetStats_ClearsCountersButNotTheHeldFrame()
    {
        var time = new ManualTime();
        var pool = new FramePool();
        var hub = new FrameHub(time);
        var sink = new RecordingSink();
        hub.AddSink(sink);
        hub.Publish(NewFrame(pool));
        hub.Tick();

        hub.ResetStats();
        hub.Tick();

        var stats = hub.Snapshot();
        Assert.Equal(0, stats.Published);
        Assert.Equal(1, stats.Repeated); // still repeating the held frame
        Assert.True(sink.Calls[^1].Repeated);
    }

    [Fact]
    public void Clear_ForgetsTheHeldFrame()
    {
        var pool = new FramePool();
        var hub = new FrameHub(new ManualTime());
        var sink = new RecordingSink();
        hub.AddSink(sink);
        hub.Publish(NewFrame(pool));
        hub.Tick();

        hub.Clear();

        Assert.False(hub.Tick());
        Assert.Single(sink.Calls);
    }

    [Fact]
    public void Pool_DropsBuffersOfTheOldSizeAfterAResolutionChange()
    {
        var pool = new FramePool();
        var small = NewFrame(pool, width: 4, height: 2);
        small.Release();
        Assert.Equal(1, pool.Allocated);

        var big = NewFrame(pool, width: 8, height: 4);
        Assert.Equal(2, pool.Allocated);
        Assert.Equal(8 * 4 * 4, big.Pixels.Length);
        big.Release();

        var again = NewFrame(pool, width: 8, height: 4);
        Assert.Equal(2, pool.Allocated); // reused
        again.Release();
    }

    [Fact]
    public void Frame_ReleasedTooOften_Throws()
    {
        var frame = NewFrame(new FramePool());
        frame.Release();
        Assert.Throws<InvalidOperationException>(() => frame.Release());
        Assert.Throws<InvalidOperationException>(() => frame.Retain());
    }

    [Fact]
    public void Rent_RejectsInvalidGeometry()
    {
        var pool = new FramePool();
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(0, 10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(10, 10, 39));
    }
}
