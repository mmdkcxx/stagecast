using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Stagecast.Spike;

/// <summary>Reads the Windows "GPU Engine" counters for a set of processes, summed per adapter and engine kind.</summary>
internal sealed partial class GpuSampler : IDisposable
{
    private readonly PerformanceCounterCategory _category = new("GPU Engine");
    private readonly Dictionary<string, PerformanceCounter> _counters = new();
    private readonly Dictionary<string, string> _adapterNames = LoadAdapterNames();

    public sealed record Reading(string Adapter, double ThreeD, double Copy, double Video);

    public List<Reading> Sample(ICollection<int> pids)
    {
        var sums = new Dictionary<string, double[]>();
        foreach (string instance in _category.GetInstanceNames())
        {
            var m = InstanceName().Match(instance);
            if (!m.Success || !pids.Contains(int.Parse(m.Groups[1].Value))) continue;
            if (!_counters.TryGetValue(instance, out var counter))
                _counters[instance] = counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true);
            double value;
            try { value = counter.NextValue(); } catch (InvalidOperationException) { continue; }
            string luid = m.Groups[2].Value;
            string kind = m.Groups[3].Value.ToLowerInvariant();
            int slot = kind == "3d" ? 0 : kind == "copy" ? 1 : kind.StartsWith("video") ? 2 : -1;
            if (slot < 0) continue;
            if (!sums.TryGetValue(luid, out var row)) sums[luid] = row = new double[3];
            row[slot] += value;
        }
        return sums.Select(kv => new Reading(_adapterNames.GetValueOrDefault(kv.Key, kv.Key), kv.Value[0], kv.Value[1], kv.Value[2])).ToList();
    }

    private static Dictionary<string, string> LoadAdapterNames()
    {
        var names = new Dictionary<string, string>();
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\DirectX");
        foreach (string sub in root?.GetSubKeyNames() ?? [])
        {
            using var key = root!.OpenSubKey(sub);
            if (key?.GetValue("Description") is string description && key.GetValue("AdapterLuid") is long luid)
                names[$"luid_0x{(uint)(luid >> 32):x8}_0x{(uint)luid:x8}"] = description;
        }
        return names;
    }

    [GeneratedRegex(@"pid_(\d+)_(luid_0x[0-9a-f]{8}_0x[0-9a-f]{8})_.*engtype_(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex InstanceName();

    public void Dispose()
    {
        foreach (var c in _counters.Values) c.Dispose();
    }
}
