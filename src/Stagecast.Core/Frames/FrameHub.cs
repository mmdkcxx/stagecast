using System.Diagnostics;
using Stagecast.Core.Output;

namespace Stagecast.Core.Frames;

/// <summary>Something that consumes the program picture: a window, NDI, OMT.</summary>
public interface IFrameSink
{
    string Name { get; }

    /// <summary>
    /// Called on the hub's tick thread, once per tick. <paramref name="repeated"/> is true when nothing new was drawn since the
    /// previous tick (a still page) and the same frame is sent again. The frame is valid for the duration of the call; call
    /// <see cref="Frame.Retain"/> to keep it longer.
    /// </summary>
    void Deliver(Frame frame, bool repeated);
}

/// <param name="Seconds">Time covered, since creation or the last <see cref="FrameHub.ResetStats"/>.</param>
/// <param name="Published">Frames handed to the hub by the capture.</param>
/// <param name="Delivered">Ticks that sent a new frame.</param>
/// <param name="Repeated">Ticks that sent the previous frame again because no new one had arrived.</param>
/// <param name="Dropped">Published frames replaced by a newer one before a tick could send them.</param>
/// <param name="Idle">Ticks before the first frame existed.</param>
/// <param name="SinkErrors">Exceptions thrown by sinks (a failing sink never stops the others).</param>
public sealed record FrameHubStats(
    double Seconds, long Published, long Delivered, long Repeated, long Dropped, long Idle, long SinkErrors,
    double AverageAgeMs, double MaxAgeMs, double TickP99Ms, double TickMaxMs)
{
    public double DeliveredPerSecond => Seconds <= 0 ? 0 : (Delivered + Repeated) / Seconds;

    public double PublishedPerSecond => Seconds <= 0 ? 0 : Published / Seconds;
}

/// <summary>
/// Sits between the capture and the outputs. The capture publishes frames whenever the page draws; a ticker calls
/// <see cref="Tick"/> at the output rate (30 or 60 Hz), and every tick sends the newest frame to all sinks, or the last one
/// again if the page did not change. So outputs always see a steady rate, even for a page that never moves.
/// </summary>
public sealed class FrameHub
{
    private const int MaxTickSamples = 3600;

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<double> _tickIntervalsMs = new();

    private readonly FramePool _blackPool = new();
    private Frame? _pending;
    private Frame? _last;
    private Frame? _held;
    private Frame? _liveSent;
    private Frame? _black;
    private GateMode _gateMode = GateMode.Live;
    private GateMode _sentMode = GateMode.Live;
    private IFrameSink[] _sinks = [];
    private long _published, _delivered, _repeated, _dropped, _idle, _sinkErrors;
    private double _ageSumMs, _ageMaxMs;
    private long _statsStart, _previousTick;

    public FrameHub(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _statsStart = _time.GetTimestamp();
    }

    /// <summary>Takes over the caller's reference to <paramref name="frame"/>.</summary>
    public void Publish(Frame frame)
    {
        lock (_gate)
        {
            _published++;
            if (_pending is not null)
            {
                _dropped++;
                _pending.Release();
            }
            _pending = frame;
        }
    }

    /// <summary>
    /// What leaves the application. Freeze repeats the frame that was live when it was switched on, Blackout sends black; the
    /// page, the capture and the preview are not affected. Switching back sends the newest picture on the very next tick.
    /// </summary>
    public GateMode Gate
    {
        get { lock (_gate) return _gateMode; }
        set { lock (_gate) _gateMode = value; }
    }

    public void AddSink(IFrameSink sink)
    {
        lock (_gate) _sinks = [.. _sinks, sink];
    }

    public void RemoveSink(IFrameSink sink)
    {
        lock (_gate) _sinks = _sinks.Where(s => !ReferenceEquals(s, sink)).ToArray();
    }

    /// <summary>One output tick. Returns false only while no frame has ever been published.</summary>
    public bool Tick()
    {
        Frame frame;
        bool repeated;
        IFrameSink[] sinks;
        long now = _time.GetTimestamp();
        lock (_gate)
        {
            if (_previousTick != 0 && _tickIntervalsMs.Count < MaxTickSamples)
                _tickIntervalsMs.Add(_time.GetElapsedTime(_previousTick, now).TotalMilliseconds);
            _previousTick = now;

            if (_pending is not null)
            {
                _last?.Release();
                _last = _pending;
                _pending = null;
                repeated = false;
                _delivered++;
                double age = _time.GetElapsedTime(_last.CapturedAt, now).TotalMilliseconds;
                _ageSumMs += age;
                _ageMaxMs = Math.Max(_ageMaxMs, age);
            }
            else if (_last is not null)
            {
                repeated = true;
                _repeated++;
            }
            else
            {
                _idle++;
                return false;
            }
            frame = ApplyGate(_last, ref repeated, now);
            frame.Retain();
            sinks = _sinks;
        }

        try
        {
            foreach (var sink in sinks)
            {
                try
                {
                    sink.Deliver(frame, repeated);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Interlocked.Increment(ref _sinkErrors);
                    Debug.WriteLine($"Sink {sink.Name} failed: {ex.Message}");
                }
            }
        }
        finally
        {
            frame.Release();
        }
        return true;
    }

    /// <summary>Called under the lock: the frame the sinks get for <paramref name="live"/> in the current gate mode.</summary>
    private Frame ApplyGate(Frame live, ref bool repeated, long now)
    {
        var mode = _gateMode;
        if (mode != GateMode.Freeze && _held is not null)
        {
            _held.Release();
            _held = null;
        }
        Frame chosen;
        switch (mode)
        {
            case GateMode.Freeze:
                if (_held is null)
                {
                    _held = _liveSent ?? live; // what the audience saw last, not a frame that arrived after the button
                    _held.Retain();
                }
                chosen = _held;
                repeated = true;
                break;
            case GateMode.Blackout:
                if (_black is null || _black.Width != live.Width || _black.Height != live.Height)
                {
                    _black?.Release();
                    _black = _blackPool.Rent(live.Width, live.Height, live.Width * 4);
                    Array.Fill(_black.Pixels, (byte)0);
                    for (int i = 3; i < _black.Pixels.Length; i += 4) _black.Pixels[i] = 255;
                }
                chosen = _black;
                repeated = true;
                break;
            default:
                chosen = live;
                if (!ReferenceEquals(_liveSent, live))
                {
                    _liveSent?.Release();
                    _liveSent = live;
                    live.Retain();
                }
                break;
        }
        // A sink that skips repeated frames must still see the first picture after the mode changed.
        if (mode != _sentMode && mode != GateMode.Freeze) repeated = false; // freezing sends the picture they already have
        _sentMode = mode;
        return chosen;
    }

    public FrameHubStats Snapshot()
    {
        lock (_gate)
        {
            var sorted = _tickIntervalsMs.OrderBy(x => x).ToArray();
            double p99 = sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))];
            return new FrameHubStats(
                _time.GetElapsedTime(_statsStart).TotalSeconds, _published, _delivered, _repeated, _dropped, _idle, Interlocked.Read(ref _sinkErrors),
                _delivered == 0 ? 0 : _ageSumMs / _delivered, _ageMaxMs, p99, sorted.Length == 0 ? 0 : sorted[^1]);
        }
    }

    public void ResetStats()
    {
        lock (_gate)
        {
            _published = _delivered = _repeated = _dropped = _idle = 0;
            Interlocked.Exchange(ref _sinkErrors, 0);
            _ageSumMs = _ageMaxMs = 0;
            _tickIntervalsMs.Clear();
            _statsStart = _time.GetTimestamp();
            _previousTick = 0;
        }
    }

    /// <summary>Forgets the held frames (for example when the page is replaced) so a stale picture is never repeated.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _pending?.Release();
            _last?.Release();
            _held?.Release();
            _liveSent?.Release();
            _pending = _last = _held = _liveSent = null;
        }
    }
}
