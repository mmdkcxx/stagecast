using System.IO;

namespace Stagecast.Spike;

/// <summary>
/// S8: record the process tree's audio for 10 s through WASAPI process loopback, mute and unmute the page in the middle,
/// report the level per phase and save a WAV for listening.
/// </summary>
internal static class AudioSpike
{
    public sealed record Phase(string Name, double PeakDb, double RmsDb, double Seconds);

    public static async Task<IReadOnlyList<Phase>> RunAsync(int processId, Func<bool, Task> setMuted, string wavPath)
    {
        var all = new List<float>();
        string current = "unmuted-1";
        long phaseStartSamples = 0, settleSamples = ProcessLoopbackCapture.SampleRate * ProcessLoopbackCapture.Channels / 2;
        long totalSamples = 0;
        var stats = new Dictionary<string, (double Peak, double SumSq, long Samples)>();
        var order = new List<string>();
        var gate = new object();

        using (var capture = new ProcessLoopbackCapture(processId, (buf, frames) =>
        {
            int n = frames * ProcessLoopbackCapture.Channels;
            lock (gate)
            {
                double peak = stats.TryGetValue(current, out var s) ? s.Peak : 0, sumSq = s.SumSq;
                long samples = s.Samples;
                for (int i = 0; i < n; i++)
                {
                    float v = buf[i];
                    all.Add(v);
                    totalSamples++;
                    // Audio queued before a mute is still draining for a moment; the first 0.5 s of each phase is not scored.
                    if (totalSamples - phaseStartSamples < settleSamples) continue;
                    peak = Math.Max(peak, Math.Abs(v));
                    sumSq += (double)v * v;
                    samples++;
                }
                stats[current] = (peak, sumSq, samples);
            }
        }))
        {
            SpikeLog.Write("process loopback started");
            SpikeLog.Write($"process tree: {DescribeTree(processId)}");
            async Task PhaseAsync(string name, bool muted, int seconds)
            {
                await setMuted(muted);
                lock (gate) { current = name; order.Add(name); phaseStartSamples = totalSamples; }
                for (int i = 0; i < seconds; i++)
                {
                    await Task.Delay(1000);
                    lock (gate)
                    {
                        var s = stats.GetValueOrDefault(name);
                        SpikeLog.Write($"level {name} t={i + 1}s peak={ToDb(s.Peak):F1} dBFS");
                    }
                }
            }
            order.Add("unmuted-1");
            await PhaseAsync("unmuted-1", false, 4);
            await PhaseAsync("muted", true, 3);
            await PhaseAsync("unmuted-2", false, 3);
        }

        WriteWav(wavPath, all);
        SpikeLog.Write($"wav saved {wavPath} ({all.Count / ProcessLoopbackCapture.Channels / (double)ProcessLoopbackCapture.SampleRate:F1} s)");
        return order.Distinct().Select(n =>
        {
            var s = stats.GetValueOrDefault(n);
            double rms = s.Samples == 0 ? 0 : Math.Sqrt(s.SumSq / s.Samples);
            return new Phase(n, ToDb(s.Peak), ToDb(rms), s.Samples / (double)ProcessLoopbackCapture.Channels / ProcessLoopbackCapture.SampleRate);
        }).ToList();
    }

    private static string DescribeTree(int rootPid)
    {
        using var searcher = new System.Management.ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name FROM Win32_Process");
        var rows = searcher.Get().Cast<System.Management.ManagementBaseObject>()
            .Select(o => (Pid: Convert.ToInt32(o["ProcessId"]), Parent: Convert.ToInt32(o["ParentProcessId"]), Name: (string)o["Name"])).ToList();
        var found = new List<string>();
        void Walk(int pid, int depth)
        {
            foreach (var r in rows.Where(r => r.Parent == pid)) { found.Add($"{new string('-', depth)}{r.Name}({r.Pid})"); Walk(r.Pid, depth + 1); }
        }
        Walk(rootPid, 1);
        int webviews = rows.Count(r => r.Name == "msedgewebview2.exe");
        return $"root={rootPid} descendants=[{string.Join(" ", found)}] allWebview2Processes={webviews}";
    }

    private static double ToDb(double linear) => linear <= 1e-9 ? -120 : 20 * Math.Log10(linear);

    private static void WriteWav(string path, List<float> samples)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new BinaryWriter(File.Create(path));
        int dataBytes = samples.Count * 4;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + dataBytes); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)3); w.Write((short)ProcessLoopbackCapture.Channels);
        w.Write(ProcessLoopbackCapture.SampleRate); w.Write(ProcessLoopbackCapture.SampleRate * ProcessLoopbackCapture.Channels * 4);
        w.Write((short)(ProcessLoopbackCapture.Channels * 4)); w.Write((short)32);
        w.Write("data"u8.ToArray()); w.Write(dataBytes);
        foreach (float s in samples) w.Write(s);
    }
}
