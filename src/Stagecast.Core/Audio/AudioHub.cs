using System.Diagnostics;

namespace Stagecast.Core.Audio;

/// <summary>Something that plays or sends the program audio: the local device now, NDI and OMT later.</summary>
public interface IAudioSink
{
    string Name { get; }

    /// <summary>Called on the hub thread with interleaved 48 kHz float stereo.</summary>
    void Write(float[] samples, int frames);
}

/// <summary>
/// Mixes the audio of every browser process into one program signal on a steady clock and hands it to the sinks. Freeze makes the
/// program silent (nothing is repeated, because repeating audio sounds broken) and it comes back live, not delayed, when released.
/// A test tone replaces the page audio while it is on.
/// </summary>
public sealed class AudioHub : IDisposable
{
    public const int MaxBacklogFrames = AudioFormat.SampleRate / 10;
    public const int KeepAfterTrimFrames = AudioFormat.SampleRate / 50;
    public static readonly TimeSpan ToneLimit = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, SampleRing> _sources = [];
    private readonly ToneGenerator _tone = new();
    private readonly LevelMeter _meter = new();
    private readonly TimeProvider _time;
    private IAudioSink[] _sinks = [];
    private float[] _mix = new float[AudioFormat.SampleRate / 10 * AudioFormat.Channels];
    private double _gain = 1;
    private long _toneUntil;
    private bool _toneOn;
    private Thread? _thread;
    private volatile bool _stop;

    public AudioHub(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>0 to 1.</summary>
    public double Volume { get; set; } = 1;

    public bool Muted { get; set; }

    /// <summary>Asked on every chunk; true makes the program silent (Freeze).</summary>
    public Func<bool>? Silenced { get; set; }

    public bool IsSilenced => Silenced?.Invoke() ?? false;

    public bool ToneActive
    {
        get
        {
            lock (_gate)
            {
                if (_toneOn && _time.GetTimestamp() >= _toneUntil) _toneOn = false;
                return _toneOn;
            }
        }
    }

    public ToneChannel ToneChannel => _tone.Channel;

    public Levels Levels => _meter.Read();

    /// <summary>Chunks that were played from an empty source (the page gave less audio than the clock asked for) — normal when a page is silent.</summary>
    public long Chunks { get; private set; }

    public SampleRing AddSource(string key)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(key, out var ring)) _sources[key] = ring = new SampleRing();
            return ring;
        }
    }

    public void RemoveSource(string key)
    {
        lock (_gate) _sources.Remove(key);
    }

    public IReadOnlyCollection<string> SourceKeys
    {
        get { lock (_gate) return _sources.Keys.ToArray(); }
    }

    public void AddSink(IAudioSink sink)
    {
        lock (_gate) _sinks = [.. _sinks, sink];
    }

    public void RemoveSink(IAudioSink sink)
    {
        lock (_gate) _sinks = _sinks.Where(s => !ReferenceEquals(s, sink)).ToArray();
    }

    public void StartTone(ToneChannel channel)
    {
        lock (_gate)
        {
            _tone.Channel = channel;
            _toneOn = true;
            _toneUntil = _time.GetTimestamp() + (long)(ToneLimit.TotalSeconds * _time.TimestampFrequency);
        }
    }

    public void StopTone()
    {
        lock (_gate) _toneOn = false;
    }

    /// <summary>Produces <paramref name="frames"/> frames of program audio and sends them to the sinks. The clock thread calls this; tests call it directly.</summary>
    public void Step(int frames)
    {
        if (frames <= 0) return;
        if (frames * AudioFormat.Channels > _mix.Length) _mix = new float[frames * AudioFormat.Channels];
        SampleRing[] rings;
        IAudioSink[] sinks;
        bool tone;
        lock (_gate)
        {
            rings = [.. _sources.Values];
            sinks = _sinks;
            if (_toneOn && _time.GetTimestamp() >= _toneUntil) _toneOn = false;
            tone = _toneOn;
        }

        Array.Clear(_mix, 0, frames * AudioFormat.Channels);
        // Sources are always drained, also while the tone plays or the program is silent, so nothing old is heard afterwards.
        foreach (var ring in rings)
        {
            if (ring.Available > MaxBacklogFrames) ring.TrimTo(KeepAfterTrimFrames);
            ring.ReadAdd(_mix, frames);
        }
        if (tone)
        {
            lock (_gate) _tone.Fill(_mix, frames);
        }

        bool silent = Muted || IsSilenced;
        double target = silent ? 0 : Math.Clamp(Volume, 0, 1);
        double start = _gain;
        for (int i = 0; i < frames; i++)
        {
            // Ramp over the chunk so a change of volume, mute or freeze does not click.
            float g = (float)(start + (target - start) * (i + 1) / frames);
            for (int c = 0; c < 2; c++)
            {
                float v = _mix[i * 2 + c] * g;
                _mix[i * 2 + c] = v > 1 ? 1 : v < -1 ? -1 : v;
            }
        }
        _gain = target;
        Chunks++;
        if (silent && target == 0) _meter.Reset(); // the meter drops to zero at once when the program is silent
        else _meter.Update(_mix, frames, (double)frames / AudioFormat.SampleRate);

        foreach (var sink in sinks)
        {
            try
            {
                sink.Write(_mix, frames);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Debug.WriteLine($"Audio sink {sink.Name} failed: {ex.Message}");
            }
        }
    }

    public void Start()
    {
        if (_thread is not null) return;
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "audio-hub", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        long last = _time.GetTimestamp();
        double owed = 0;
        while (!_stop)
        {
            Thread.Sleep(5);
            long now = _time.GetTimestamp();
            owed += _time.GetElapsedTime(last, now).TotalSeconds * AudioFormat.SampleRate;
            last = now;
            int frames = (int)owed;
            if (frames < 120) continue; // at least 2.5 ms
            if (frames > AudioFormat.SampleRate / 5) frames = AudioFormat.SampleRate / 5; // after a long stall do not catch up in one burst
            owed -= frames;
            if (owed > AudioFormat.SampleRate / 5) owed = 0;
            Step(frames);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(1000);
        _thread = null;
    }
}
