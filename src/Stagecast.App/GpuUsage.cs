using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Vortice.DXGI;

namespace Stagecast.App;

/// <summary>GPU use of some processes: the sum over all their engines, in percent of one engine, and the same split per graphics adapter.</summary>
internal sealed record GpuReading(double Total, IReadOnlyDictionary<string, double> ByAdapter, IReadOnlyDictionary<(int Pid, string Adapter), double> ByProcess);

/// <summary>
/// Reads the Windows "GPU Engine" performance counters (the ones Task Manager shows) for a set of processes between two moments.
/// <see cref="Begin"/> takes the first reading, <see cref="End"/> the second and returns the use in between. Never throws: when the
/// counters are missing or cannot be read the result is null. A process that starts between the two readings is not counted.
/// </summary>
internal sealed class GpuUsage
{
    private static readonly Regex Instance = new(@"^pid_(\d+)_luid_(0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)_phys_\d+_eng_\d+_engtype_(.+)$", RegexOptions.Compiled);

    private InstanceDataCollection? _first;

    public void Begin() => _first = Read();

    public GpuReading? End(IEnumerable<int> pids)
    {
        var second = Read();
        var first = _first;
        _first = null;
        if (first is null || second is null) return null;
        var wanted = pids.ToHashSet();
        var names = AdapterNames();
        var byAdapter = new Dictionary<string, double>();
        var byProcess = new Dictionary<(int, string), double>();
        double total = 0;
        try
        {
            foreach (string name in second.Keys)
            {
                var m = Instance.Match(name);
                if (!m.Success || !wanted.Contains(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) || !first.Contains(name)) continue;
                double value = CounterSample.Calculate(first[name].Sample, second[name].Sample);
                // One engine cannot exceed 100%; the counter sometimes returns garbage for an instance that appeared or vanished.
                if (!double.IsFinite(value) || value < 0 || value > 100.5) continue;
                string adapter = names.TryGetValue(m.Groups[2].Value.ToUpperInvariant(), out var n) ? n : m.Groups[2].Value;
                byAdapter[adapter] = byAdapter.GetValueOrDefault(adapter) + value;
                var key = (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), adapter);
                byProcess[key] = byProcess.GetValueOrDefault(key) + value;
                total += value;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            return null; // an instance vanished between the two readings
        }
        return new GpuReading(total, byAdapter, byProcess);
    }

    private static InstanceDataCollection? Read()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists("GPU Engine")) return null;
            var all = new PerformanceCounterCategory("GPU Engine").ReadCategory();
            return all.Contains("Utilization Percentage") ? all["Utilization Percentage"] : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Adapter names by the luid the counters use ("0X00000000_0X0000D2E4").</summary>
    private static Dictionary<string, string> AdapterNames()
    {
        var names = new Dictionary<string, string>();
        try
        {
            DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
            using (factory)
            {
                for (uint i = 0; factory!.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success; i++)
                {
                    using (adapter)
                    {
                        var d = adapter!.Description1;
                        names[$"0X{d.Luid.HighPart:X8}_0X{d.Luid.LowPart:X8}"] = d.Description;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or SharpGen.Runtime.SharpGenException)
        {
            // names stay as luids
        }
        return names;
    }
}
