using Stagecast.Core.Audio;

namespace Stagecast.Core.Tests;

public class AudioHubTests
{
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _ticks;
        public void AdvanceSeconds(double s) => _ticks += (long)(s * 1000);
    }

    private sealed class Capture : IAudioSink
    {
        public string Name => "capture";
        public List<float> Samples { get; } = [];
        public void Write(float[] samples, int frames) => Samples.AddRange(samples.Take(frames * 2));
        public float Peak => Samples.Count == 0 ? 0 : Samples.Max(Math.Abs);
    }

    private static float[] Constant(float value, int frames)
    {
        var data = new float[frames * 2];
        Array.Fill(data, value);
        return data;
    }

    private static (AudioHub Hub, Capture Sink) Hub(TimeProvider? time = null)
    {
        var hub = new AudioHub(time);
        var sink = new Capture();
        hub.AddSink(sink);
        return (hub, sink);
    }

    [Fact]
    public void Sources_AreSummed()
    {
        var (hub, sink) = Hub();
        hub.AddSource("a").Write(Constant(0.25f, 480), 480);
        hub.AddSource("b").Write(Constant(0.25f, 480), 480);
        hub.Step(480);
        Assert.Equal(0.5f, sink.Samples[^1], 3);
    }

    [Fact]
    public void Sum_IsLimitedToFullScale()
    {
        var (hub, sink) = Hub();
        hub.AddSource("a").Write(Constant(0.9f, 480), 480);
        hub.AddSource("b").Write(Constant(0.9f, 480), 480);
        hub.Step(480);
        Assert.Equal(1f, sink.Peak);
    }

    [Fact]
    public void EmptySource_GivesSilence()
    {
        var (hub, sink) = Hub();
        hub.AddSource("a");
        hub.Step(480);
        Assert.Equal(480 * 2, sink.Samples.Count);
        Assert.Equal(0f, sink.Peak);
    }

    [Fact]
    public void Mute_RampsToSilenceWithoutAStep()
    {
        var (hub, sink) = Hub();
        var ring = hub.AddSource("a");
        ring.Write(Constant(0.5f, 960), 960);
        hub.Step(480);
        hub.Muted = true;
        hub.Step(480);
        var second = sink.Samples.Skip(960).ToArray();
        Assert.True(second[0] > 0.4f, "starts near the old level");
        Assert.Equal(0f, second[^1], 3);
        Assert.True(Enumerable.Range(1, second.Length - 1).All(i => second[i] <= second[i - 1] + 1e-6f), "falls smoothly");
    }

    [Fact]
    public void Freeze_Silences_AndLiveAudioComesBackNotTheOldOne()
    {
        var (hub, sink) = Hub();
        bool frozen = false;
        hub.Silenced = () => frozen;
        var ring = hub.AddSource("a");
        ring.Write(Constant(0.5f, 480), 480);
        hub.Step(480);
        frozen = true;
        ring.Write(Constant(0.5f, 4800), 4800); // heard by nobody, and not kept for later
        for (int i = 0; i < 10; i++) hub.Step(480);
        Assert.Equal(0, ring.Available);
        Assert.Equal(0f, sink.Samples.Skip(480 * 2 + 480 * 2).Max(Math.Abs), 3);

        frozen = false;
        ring.Write(Constant(0.5f, 480), 480);
        hub.Step(480);
        Assert.Equal(0.5f, sink.Samples[^1], 2);
    }

    [Fact]
    public void Volume_ScalesTheMix()
    {
        var (hub, sink) = Hub();
        hub.Volume = 0.5;
        var ring = hub.AddSource("a");
        ring.Write(Constant(0.8f, 4800), 4800);
        hub.Step(480);
        hub.Step(480);
        Assert.Equal(0.4f, sink.Samples[^1], 3);
    }

    [Fact]
    public void Tone_IsMinus20dBFS_ReplacesPageAudio_AndStopsAfter30Seconds()
    {
        var time = new ManualTime();
        var (hub, sink) = Hub(time);
        var page = hub.AddSource("a");
        page.Write(Constant(0.9f, 4800), 4800);
        hub.StartTone(ToneChannel.Both);
        Assert.True(hub.ToneActive);
        hub.Step(4800);
        Assert.Equal(0.1f, sink.Peak, 2); // -20 dBFS, the 0.9 page audio is not heard
        time.AdvanceSeconds(31);
        Assert.False(hub.ToneActive);
        sink.Samples.Clear();
        page.Write(Constant(0.9f, 480), 480);
        hub.Step(480);
        Assert.True(sink.Peak > 0.5f, "page audio is back");
    }

    [Theory]
    [InlineData(ToneChannel.Left, true, false)]
    [InlineData(ToneChannel.Right, false, true)]
    [InlineData(ToneChannel.Both, true, true)]
    public void Tone_ChannelSelection(ToneChannel channel, bool left, bool right)
    {
        var (hub, sink) = Hub();
        hub.StartTone(channel);
        hub.Step(4800);
        float l = 0, r = 0;
        for (int i = 0; i < sink.Samples.Count; i += 2)
        {
            l = Math.Max(l, Math.Abs(sink.Samples[i]));
            r = Math.Max(r, Math.Abs(sink.Samples[i + 1]));
        }
        Assert.Equal(left, l > 0.05f);
        Assert.Equal(right, r > 0.05f);
    }

    [Fact]
    public void Backlog_IsTrimmedSoLatencyStaysSmall()
    {
        var (hub, _) = Hub();
        var ring = hub.AddSource("a");
        ring.Write(Constant(0.1f, 24000), 24000); // half a second behind
        hub.Step(480);
        Assert.True(ring.Available <= AudioHub.KeepAfterTrimFrames);
    }

    [Fact]
    public void Meter_ShowsPeakInDb_AndZeroWhenSilenced()
    {
        var (hub, _) = Hub();
        var ring = hub.AddSource("a");
        ring.Write(Constant(0.1f, 4800), 4800);
        hub.Step(480);
        Assert.InRange(hub.Levels.LeftDb, -20.5, -19.5);
        bool frozen = true;
        hub.Silenced = () => frozen;
        for (int i = 0; i < 40; i++) hub.Step(480);
        Assert.True(hub.Levels.LeftDb < -60);
    }

    [Fact]
    public void FailingSink_DoesNotStopOthers()
    {
        var (hub, sink) = Hub();
        hub.AddSink(new Thrower());
        hub.Step(480);
        Assert.Equal(960, sink.Samples.Count);
    }

    private sealed class Thrower : IAudioSink
    {
        public string Name => "thrower";
        public void Write(float[] samples, int frames) => throw new InvalidOperationException();
    }
}

public class SampleRingTests
{
    [Fact]
    public void Overflow_DropsTheOldest()
    {
        var ring = new SampleRing(4);
        ring.Write([1, 1, 2, 2, 3, 3], 3);
        ring.Write([4, 4, 5, 5, 6, 6], 3);
        var output = new float[8];
        Assert.Equal(4, ring.ReadAdd(output, 4));
        Assert.Equal([3f, 3f, 4f, 4f, 5f, 5f, 6f, 6f], output);
        Assert.Equal(2, ring.Overruns);
    }

    [Fact]
    public void WriteLargerThanCapacity_KeepsTheNewest()
    {
        var ring = new SampleRing(2);
        ring.Write([1, 1, 2, 2, 3, 3], 3);
        var output = new float[4];
        ring.ReadAdd(output, 2);
        Assert.Equal([2f, 2f, 3f, 3f], output);
    }
}

public class AudioHubBeepTests
{
    private sealed class Peak : Stagecast.Core.Audio.IAudioSink
    {
        public string Name => "peak";
        public float Max;
        public int Loud, Quiet;

        public void Write(float[] samples, int frames)
        {
            float m = 0;
            for (int i = 0; i < frames * 2; i++) m = Math.Max(m, Math.Abs(samples[i]));
            Max = Math.Max(Max, m);
            if (m > 0.01f) Loud++; else Quiet++;
        }
    }

    [Fact]
    public void ASinkThatWantsTheBeep_HearsBurstsOfAToneOnceASecond_WhileOthersHearThePage()
    {
        using var hub = new Stagecast.Core.Audio.AudioHub();
        var beeper = new Peak();
        var page = new Peak();
        hub.AddSink(beeper, () => true);
        hub.AddSink(page);
        for (int i = 0; i < 125; i++)
        {
            hub.Step(480); // 10 ms
            Thread.Sleep(10);
        }

        Assert.True(beeper.Loud > 3 && beeper.Quiet > 20, $"bursts, not a steady tone (loud {beeper.Loud}, quiet {beeper.Quiet})");
        Assert.InRange(beeper.Max, 0.05f, 0.11f); // about -20 dBFS
        Assert.Equal(0, page.Loud); // no page audio was fed, so the other sink stays silent
    }

    [Fact]
    public void NoLive_GivesEverySinkTheBeep()
    {
        using var hub = new Stagecast.Core.Audio.AudioHub { NoLive = true };
        var sink = new Peak();
        hub.AddSink(sink);
        for (int i = 0; i < 125; i++)
        {
            hub.Step(480);
            Thread.Sleep(10);
        }

        Assert.True(sink.Loud > 3);
    }
}
