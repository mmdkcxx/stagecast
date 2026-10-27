namespace Stagecast.Core.Audio;

/// <summary>Fixed audio format used everywhere inside the application: 48 kHz, 32-bit float, interleaved stereo.</summary>
public static class AudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
}

/// <summary>
/// A thread-safe queue of interleaved stereo samples between a capture thread (writes in bursts) and the mixer (reads on a steady
/// clock). When the writer gets too far ahead the oldest samples are dropped, so latency cannot grow without bound.
/// </summary>
public sealed class SampleRing
{
    private readonly float[] _buffer;
    private readonly object _gate = new();
    private int _start, _count; // in frames

    public SampleRing(int capacityFrames = AudioFormat.SampleRate)
    {
        if (capacityFrames <= 0) throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        Capacity = capacityFrames;
        _buffer = new float[capacityFrames * AudioFormat.Channels];
    }

    public int Capacity { get; }

    public int Available
    {
        get { lock (_gate) return _count; }
    }

    /// <summary>Frames discarded because the reader did not keep up.</summary>
    public long Overruns { get; private set; }

    public void Write(float[] samples, int frames)
    {
        if (frames <= 0) return;
        lock (_gate)
        {
            if (frames > Capacity)
            {
                // Only the newest part fits.
                samples = samples.AsSpan((frames - Capacity) * AudioFormat.Channels, Capacity * AudioFormat.Channels).ToArray();
                Overruns += frames - Capacity;
                frames = Capacity;
            }
            int overflow = _count + frames - Capacity;
            if (overflow > 0)
            {
                _start = (_start + overflow) % Capacity;
                _count -= overflow;
                Overruns += overflow;
            }
            int writeAt = (_start + _count) % Capacity;
            for (int i = 0; i < frames; i++)
            {
                int slot = (writeAt + i) % Capacity * AudioFormat.Channels;
                _buffer[slot] = samples[i * 2];
                _buffer[slot + 1] = samples[i * 2 + 1];
            }
            _count += frames;
        }
    }

    /// <summary>Adds up to <paramref name="frames"/> frames into <paramref name="destination"/> (mixing) and returns how many were available.</summary>
    public int ReadAdd(float[] destination, int frames)
    {
        lock (_gate)
        {
            int n = Math.Min(frames, _count);
            for (int i = 0; i < n; i++)
            {
                int slot = (_start + i) % Capacity * AudioFormat.Channels;
                destination[i * 2] += _buffer[slot];
                destination[i * 2 + 1] += _buffer[slot + 1];
            }
            _start = (_start + n) % Capacity;
            _count -= n;
            return n;
        }
    }

    /// <summary>Drops the oldest frames until at most <paramref name="keepFrames"/> remain.</summary>
    public void TrimTo(int keepFrames)
    {
        lock (_gate)
        {
            int drop = _count - keepFrames;
            if (drop <= 0) return;
            _start = (_start + drop) % Capacity;
            _count -= drop;
        }
    }

    public void Clear()
    {
        lock (_gate) _start = _count = 0;
    }
}
