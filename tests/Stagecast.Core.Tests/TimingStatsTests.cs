using Stagecast.Core.Metrics;

namespace Stagecast.Core.Tests;

public class TimingStatsTests
{
    [Fact]
    public void Empty_snapshot_is_all_zero()
    {
        var s = new TimingStats().Snapshot();
        Assert.Equal(new TimingSnapshot(0, 0, 0, 0), s);
    }

    [Fact]
    public void Average_p99_and_max_of_1_to_100()
    {
        var stats = new TimingStats();
        for (int i = 1; i <= 100; i++) stats.Add(i);
        var s = stats.Snapshot();
        Assert.Equal(100, s.Count);
        Assert.Equal(50.5, s.AverageMs, 6);
        Assert.Equal(100, s.MaxMs);
        Assert.Equal(100, s.P99Ms);
    }

    [Fact]
    public void Max_and_average_stay_exact_beyond_the_sample_cap()
    {
        var stats = new TimingStats();
        for (int i = 0; i < 25_000; i++) stats.Add(1.0);
        stats.Add(9.0);
        var s = stats.Snapshot();
        Assert.Equal(25_001, s.Count);
        Assert.Equal(9.0, s.MaxMs);
        Assert.Equal((25_000 + 9.0) / 25_001, s.AverageMs, 9);
    }

    [Fact]
    public void Reset_clears_everything()
    {
        var stats = new TimingStats();
        stats.Add(5);
        stats.Reset();
        Assert.Equal(new TimingSnapshot(0, 0, 0, 0), stats.Snapshot());
    }

    [Fact]
    public void Add_from_many_threads_counts_every_sample()
    {
        var stats = new TimingStats();
        Parallel.For(0, 8, _ => { for (int i = 0; i < 1000; i++) stats.Add(1); });
        Assert.Equal(8000, stats.Snapshot().Count);
    }
}
