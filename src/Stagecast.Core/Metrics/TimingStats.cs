namespace Stagecast.Core.Metrics;

/// <summary>Count, average, 99th percentile and maximum of a set of durations in milliseconds.</summary>
public readonly record struct TimingSnapshot(long Count, double AverageMs, double P99Ms, double MaxMs);

/// <summary>
/// Collects durations from any thread. Count, average and maximum are exact; the 99th percentile is taken from the first
/// <see cref="MaxSamples"/> samples since the last <see cref="Reset"/>, which bounds memory on long runs.
/// </summary>
public sealed class TimingStats
{
    public const int MaxSamples = 20_000;

    private readonly object _gate = new();
    private readonly List<double> _samples = new();
    private long _count;
    private double _sum, _max;

    public void Add(double milliseconds)
    {
        lock (_gate)
        {
            _count++;
            _sum += milliseconds;
            if (milliseconds > _max) _max = milliseconds;
            if (_samples.Count < MaxSamples) _samples.Add(milliseconds);
        }
    }

    public TimingSnapshot Snapshot()
    {
        lock (_gate)
        {
            if (_count == 0) return default;
            var sorted = _samples.OrderBy(x => x).ToArray();
            double p99 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))];
            return new TimingSnapshot(_count, _sum / _count, p99, _max);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _samples.Clear();
            _count = 0;
            _sum = _max = 0;
        }
    }
}
