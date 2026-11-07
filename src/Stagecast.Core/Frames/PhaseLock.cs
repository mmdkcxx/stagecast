namespace Stagecast.Core.Frames;

/// <summary>
/// Keeps the output tick and the frames from the capture from running past each other. Each published frame reports how long after
/// the last tick it arrived; the lock keeps a circular mean of the last few of those and, tick by tick, asks for a small shift of the tick
/// schedule so that frames arrive <see cref="TargetSlackFraction"/> of an interval before the tick that uses them. There a jitter of
/// half an interval either way still leaves exactly one new frame per tick, instead of a tick without a frame followed by one with two.
/// Pure logic: the caller supplies all times (milliseconds on any steady clock).
/// </summary>
public sealed class PhaseLock
{
    private readonly object _gate = new();
    private readonly double[] _since; // ring of ms after the previous tick at which each frame arrived
    private readonly int _minSamples;
    private readonly double _maxStepMs, _deadbandMs, _minCoherence, _maxGapMs;
    private int _count, _next;
    private double _lastPublishMs = double.NegativeInfinity;
    private double _intervalMs = 1000.0 / 60;

    public PhaseLock(double targetSlackFraction = 0.5, int window = 24, int minSamples = 8, double maxStepMs = 0.5,
        double deadbandMs = 0.5, double minCoherence = 0.5, double maxGapMs = 250)
    {
        TargetSlackFraction = targetSlackFraction;
        _since = new double[window];
        _minSamples = Math.Min(minSamples, window);
        _maxStepMs = maxStepMs;
        _deadbandMs = deadbandMs;
        _minCoherence = minCoherence;
        _maxGapMs = maxGapMs;
    }

    /// <summary>How long before the tick that uses it a frame should arrive, as a fraction of the interval.</summary>
    public double TargetSlackFraction { get; }

    /// <summary>The tick interval. Changing it forgets what was measured.</summary>
    public double IntervalMs
    {
        get { lock (_gate) return _intervalMs; }
        set
        {
            lock (_gate)
            {
                if (Math.Abs(value - _intervalMs) < 1e-9 || value <= 0) return;
                _intervalMs = value;
                ClearLocked();
            }
        }
    }

    /// <summary>How far the circular mean is from the target at the last <see cref="TakeCorrection"/> (positive: frames arrive too late after the tick).</summary>
    public double LastErrorMs { get; private set; }

    public void Reset()
    {
        lock (_gate) ClearLocked();
    }

    /// <param name="nowMs">When the frame was published.</param>
    /// <param name="msSinceTick">Milliseconds from the start of the latest tick to the publish.</param>
    public void AddPublish(double nowMs, double msSinceTick)
    {
        lock (_gate)
        {
            if (nowMs - _lastPublishMs > _maxGapMs) ClearLocked(); // the page was still: the capture schedule restarted somewhere else
            _lastPublishMs = nowMs;
            _since[_next] = Wrap(msSinceTick, _intervalMs);
            _next = (_next + 1) % _since.Length;
            if (_count < _since.Length) _count++;
        }
    }

    /// <summary>
    /// Milliseconds to add to the delay before the next tick (negative: tick sooner). Zero when there is not enough to go on, when the arrivals
    /// follow no pattern against the ticks, or when the phase is already within the deadband. Applying it is the caller's job; the lock assumes it is applied.
    /// </summary>
    public double TakeCorrection(double nowMs)
    {
        lock (_gate)
        {
            if (nowMs - _lastPublishMs > _maxGapMs) ClearLocked();
            if (_count < _minSamples) return 0;

            double sin = 0, cos = 0;
            for (int i = 0; i < _count; i++)
            {
                double angle = 2 * Math.PI * _since[i] / _intervalMs;
                sin += Math.Sin(angle);
                cos += Math.Cos(angle);
            }
            if (Math.Sqrt(sin * sin + cos * cos) / _count < _minCoherence) return 0;

            double meanMs = Math.Atan2(sin, cos) / (2 * Math.PI) * _intervalMs; // (-I/2, I/2]
            double targetMs = (1 - TargetSlackFraction) * _intervalMs;
            double error = Wrap(meanMs - targetMs + _intervalMs / 2, _intervalMs) - _intervalMs / 2;
            LastErrorMs = error;
            if (Math.Abs(error) < _deadbandMs) return 0;

            double correction = Math.Clamp(error, -_maxStepMs, _maxStepMs);
            // The ticks move by the correction, so every stored arrival now comes that much sooner after its tick.
            for (int i = 0; i < _count; i++) _since[i] = Wrap(_since[i] - correction, _intervalMs);
            return correction;
        }
    }

    private void ClearLocked()
    {
        _count = 0;
        _next = 0;
        LastErrorMs = 0;
    }

    private static double Wrap(double value, double period)
    {
        double r = value % period;
        return r < 0 ? r + period : r;
    }
}
