using Stagecast.Core.Metrics;

namespace Stagecast.Core.Tests;

public class ProcessCpuSamplerTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public void New_process_is_only_a_baseline()
    {
        var cpu = new Dictionary<int, TimeSpan?> { [1] = TimeSpan.FromSeconds(50) };
        var sampler = new ProcessCpuSampler(pid => cpu[pid], 4);
        Assert.Equal(0, sampler.Sample(new[] { 1 }, 2 * Second));
    }

    [Fact]
    public void Cpu_percent_is_delta_over_wall_over_cores()
    {
        var cpu = new Dictionary<int, TimeSpan?> { [1] = TimeSpan.FromSeconds(1) };
        var sampler = new ProcessCpuSampler(pid => cpu[pid], 4);
        sampler.Sample(new[] { 1 }, 2 * Second);
        cpu[1] = TimeSpan.FromSeconds(2); // one more CPU second in two wall seconds on 4 cores
        Assert.Equal(12.5, sampler.Sample(new[] { 1 }, 2 * Second), 6);
    }

    [Fact]
    public void Gone_process_is_ignored_and_forgotten()
    {
        var cpu = new Dictionary<int, TimeSpan?> { [1] = TimeSpan.FromSeconds(1), [2] = TimeSpan.FromSeconds(10) };
        var sampler = new ProcessCpuSampler(pid => cpu[pid], 1);
        sampler.Sample(new[] { 1, 2 }, Second);
        cpu[1] = null; // ended between samples
        cpu[2] = TimeSpan.FromSeconds(10.5);
        Assert.Equal(50, sampler.Sample(new[] { 1, 2 }, Second), 6);

        cpu[1] = TimeSpan.FromSeconds(100); // the id shows up again: a baseline, not a huge jump
        Assert.Equal(0, sampler.Sample(new[] { 1 }, Second), 6);
    }

    [Fact]
    public void Sums_several_processes()
    {
        var cpu = new Dictionary<int, TimeSpan?> { [1] = TimeSpan.Zero, [2] = TimeSpan.Zero };
        var sampler = new ProcessCpuSampler(pid => cpu[pid], 2);
        sampler.Sample(new[] { 1, 2 }, Second);
        cpu[1] = TimeSpan.FromSeconds(0.5);
        cpu[2] = TimeSpan.FromSeconds(0.5);
        Assert.Equal(50, sampler.Sample(new[] { 1, 2 }, Second), 6);
    }

    [Fact]
    public void Zero_wall_time_gives_zero()
    {
        var sampler = new ProcessCpuSampler(_ => TimeSpan.FromSeconds(1), 2);
        Assert.Equal(0, sampler.Sample(new[] { 1 }, TimeSpan.Zero));
    }
}
