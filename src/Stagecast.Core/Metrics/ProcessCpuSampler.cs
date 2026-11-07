namespace Stagecast.Core.Metrics;

/// <summary>
/// Turns the CPU time counters of a set of processes into a percentage of the whole machine between two samples.
/// A process seen for the first time only sets the reference; one that has ended (the reader returns null) is ignored and forgotten.
/// </summary>
public sealed class ProcessCpuSampler
{
    private readonly Func<int, TimeSpan?> _readTotalCpu;
    private readonly int _cores;
    private readonly Dictionary<int, TimeSpan> _last = new();

    /// <param name="readTotalCpu">Total processor time of a process, or null when it no longer exists or cannot be read.</param>
    /// <param name="cores">Logical processors; 100% means all of them busy.</param>
    public ProcessCpuSampler(Func<int, TimeSpan?> readTotalCpu, int cores)
    {
        _readTotalCpu = readTotalCpu;
        _cores = Math.Max(1, cores);
    }

    /// <summary>Percent of total CPU capacity (0-100) used by <paramref name="pids"/> during <paramref name="wallElapsed"/>.</summary>
    public double Sample(IEnumerable<int> pids, TimeSpan wallElapsed)
    {
        var wanted = pids.Distinct().ToList();
        foreach (int gone in _last.Keys.Except(wanted).ToList()) _last.Remove(gone);

        double usedSeconds = 0;
        foreach (int pid in wanted)
        {
            var now = _readTotalCpu(pid);
            if (now is null) { _last.Remove(pid); continue; }
            if (_last.TryGetValue(pid, out var before) && now.Value > before) usedSeconds += (now.Value - before).TotalSeconds;
            _last[pid] = now.Value;
        }
        return wallElapsed <= TimeSpan.Zero ? 0 : usedSeconds / wallElapsed.TotalSeconds / _cores * 100;
    }
}
