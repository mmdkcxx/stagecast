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
    private SinkEntry[] _sinks = [];
    private readonly TestPattern _pattern = new();
    private int _patternWidth = 1920, _patternHeight = 1080, _patternFps = 60;
    private bool _noLive;
    private long _published, _delivered, _repeated, _dropped, _idle, _sinkErrors;
    private double _ageSumMs, _ageMaxMs;
    private long _statsStart, _previousTick;

    public FrameHub(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _statsStart = _time.GetTimestamp();
    }

    private sealed record SinkEntry(IFrameSink Sink, Func<bool>? WantsPattern);

    /// <summary>The size and rate of the test pattern, normally the render size: used when no live picture exists to take them from.</summary>
    public void SetPatternFormat(int width, int height, int fps)
    {
        lock (_gate) (_patternWidth, _patternHeight, _patternFps) = (width, height, fps);
    }

    /// <summary>
    /// True when no tab is live: every sink then shows the test pattern (Blackout still wins). Sinks that asked for the pattern
    /// themselves (see <see cref="AddSink"/>) show it whether or not a tab is live.
    /// </summary>
    public bool NoLive
    {
        get { lock (_gate) return _noLive; }
        set { lock (_gate) _noLive = value; }
    }

    /// <summary>Same as setting <see cref="NoLive"/>; a method so callers that also drive the audio read alike.</summary>
    public void SetNoLiveFlag(bool noLive) => NoLive = noLive;

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

    /// <param name="wantsPattern">Asked on every tick; true makes this sink show the test pattern instead of the live picture.</param>
    public void AddSink(IFrameSink sink, Func<bool>? wantsPattern = null)
    {
        lock (_gate) _sinks = [.. _sinks, new SinkEntry(sink, wantsPattern)];
    }

    public void RemoveSink(IFrameSink sink)
    {
        lock (_gate) _sinks = _sinks.Where(s => !ReferenceEquals(s.Sink, sink)).ToArray();
    }

    /// <summary>One output tick. Returns false only while there is nothing to send: no frame has ever been published and no sink shows the pattern.</summary>
    public bool Tick()
    {
        Frame? frame = null;
        bool repeated = false;
        SinkEntry[] sinks;
        bool wantPattern, blackout, noLive;
        int pw, ph, pfps;
        long now = _time.GetTimestamp();
        lock (_gate)
        {
            if (_previousTick != 0 && _tickIntervalsMs.Count < MaxTickSamples)
                _tickIntervalsMs.Add(_time.GetElapsedTime(_previousTick, now).TotalMilliseconds);
            _previousTick = now;
            sinks = _sinks;
            noLive = _noLive;
            wantPattern = noLive || sinks.Any(s => s.WantsPattern?.Invoke() == true);
            blackout = _gateMode == GateMode.Blackout;
            (pw, ph, pfps) = (_patternWidth, _patternHeight, _patternFps);

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
            else if (!wantPattern)
            {
                _idle++;
                return false;
            }

            if (_last is not null)
            {
                frame = ApplyGate(_last, ref repeated, now);
                frame.Retain();
            }
            else if (blackout)
            {
                frame = BlackFrame(pw, ph);
                frame.Retain();
                repeated = true;
            }
        }

        Frame? pattern = wantPattern && !blackout ? _pattern.Render(pw, ph, pfps, TestPattern.Now()) : null;
        try
        {
            foreach (var entry in sinks)
            {
                bool shows = pattern is not null && (noLive || entry.WantsPattern?.Invoke() == true);
                Frame? chosen = shows ? pattern : frame;
                if (chosen is null) continue;
                try
                {
                    entry.Sink.Deliver(chosen, shows ? false : repeated);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Interlocked.Increment(ref _sinkErrors);
                    Debug.WriteLine($"Sink {entry.Sink.Name} failed: {ex.Message}");
                }
            }
        }
        finally
        {
            frame?.Release();
            pattern?.Release();
        }
        return true;
    }

    /// <summary>Called under the lock.</summary>
    private Frame BlackFrame(int width, int height)
    {
        if (_black is null || _black.Width != width || _black.Height != height)
        {
            _black?.Release();
            _black = _blackPool.Rent(width, height, width * 4);
            Array.Fill(_black.Pixels, (byte)0);
            for (int i = 3; i < _black.Pixels.Length; i += 4) _black.Pixels[i] = 255;
        }

        return _black;
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
                chosen = BlackFrame(live.Width, live.Height);
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
