using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Metrics;
using Stagecast.Core.Frames;
using Stagecast.Core.Rendering;
using Stagecast.Rendering.Ndi;

namespace Stagecast.App;

internal sealed partial class SelfTest
{
    /// <summary>Stands in for the screen (HDMI) output: it always wants frames, so the copy to system memory stays on like it does with a monitor attached.</summary>
    private sealed class ScreenLikeSink : IFrameSink
    {
        public string Name => "screen-like";
        public long Frames;
        public void Deliver(Frame frame, bool repeated) => Interlocked.Increment(ref Frames);
    }

    /// <summary>
    /// The repeatable performance scenario (<c>--selftest perf</c>, or scripts/perf.sh): 2520x1080 at 60 with the preview on, through
    /// an idle page, opening a page (one row per second), a steady page, and NDI and OMT with and without a receiver.
    /// One CSV row per phase goes to the log folder; nothing is asserted except that it ran, a person (or the next stage) compares the numbers.
    /// STAGECAST_PERF_URLS (comma separated) replaces the built-in moving test page with the first real site.
    /// </summary>
    private async Task RunPerfAsync(MainWindow window)
    {
        window.Confirm = _ => true;
        window.ApplyFromCode(new Resolution(2520, 1080), 60);
        string[] urls = (Environment.GetEnvironmentVariable("STAGECAST_PERF_URLS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string page = urls.Length > 0 ? urls[0] : new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "selftest-soak.html")).AbsoluteUri;

        Directory.CreateDirectory(window.LogDirectory);
        string csv = Path.Combine(window.LogDirectory, $"perf-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        await File.WriteAllTextAsync(csv, PerfCsv.Header + "\n");
        _logger.LogInformation("PERF start: 2520x1080@60, page {Page}, {Cores} logical processors, csv {Csv}", page, Environment.ProcessorCount, csv);

        var me = Process.GetCurrentProcess();
        var appCpu = new ProcessCpuSampler(ReadCpu, Environment.ProcessorCount);
        var webCpu = new ProcessCpuSampler(ReadCpu, Environment.ProcessorCount);
        var gpu = new GpuUsage();
        var clock = Stopwatch.StartNew();
        int rows = 0;

        static TimeSpan? ReadCpu(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return p.TotalProcessorTime;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                return null; // ended, or not ours to read
            }
        }

        async Task Phase(string name, TimeSpan duration)
        {
            _engine.ResetStats();
            window.Preview.ResetStats();
            // The first reading of a process only sets the reference; the one after the wait is the phase.
            appCpu.Sample(new[] { me.Id }, TimeSpan.FromSeconds(1));
            webCpu.Sample(_engine.WebViewProcessIds(), TimeSpan.FromSeconds(1));
            gpu.Begin();
            long started = Stopwatch.GetTimestamp();
            await Task.Delay(duration);
            var wall = Stopwatch.GetElapsedTime(started);

            var gpuReading = gpu.End(_engine.WebViewProcessIds().Append(me.Id));
            var e = _engine.Stats();
            var p = window.Preview.Stats();
            var sample = new PerfSample(name, clock.Elapsed.TotalSeconds, e.Hub.DeliveredPerSecond, e.Hub.Repeated, e.Hub.Dropped, e.Capture.ArrivedPerSecond,
                new TimingSnapshot(0, e.Capture.ReadbackAverageMs, 0, e.Capture.ReadbackMaxMs), e.Capture.Handler, e.Capture.LockWait, e.Capture.PreviewCapture,
                p.ShownPerSecond, p.SkippedPerSecond, p.Lag, e.Hub.TickP99Ms,
                appCpu.Sample(new[] { me.Id }, wall), webCpu.Sample(_engine.WebViewProcessIds(), wall), gpuReading?.Total,
                window.Ndi?.Connections ?? 0, window.Omt?.Receivers ?? 0, e.Hub.AverageAgeMs, e.Hub.MaxAgeMs);
            await File.AppendAllTextAsync(csv, PerfCsv.Line(sample) + "\n");
            if (name == "steady" && gpuReading is not null)
            {
                // Which process uses which graphics adapter: two busy GPUs for one pipeline would mean copies between them.
                string Who(int pid) { try { using var proc = Process.GetProcessById(pid); return $"{proc.ProcessName}:{pid}"; } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return $"?:{pid}"; } }
                _logger.LogInformation("PERF gpu-by-process: {Detail}", string.Join("; ", gpuReading.ByProcess.Where(kv => kv.Value >= 0.5)
                    .OrderByDescending(kv => kv.Value).Select(kv => FormattableString.Invariant($"{Who(kv.Key.Pid)} on {kv.Key.Adapter} {kv.Value:F1}%"))));
            }
            rows++;
            _logger.LogInformation(
                "PERF {Phase}: out {Out:F1}/s repeated {Repeated} dropped {Dropped}, compositor {Arrived:F0}/s, copy {Copy:F1} ms (max {CopyMax:F0}), handler p99 {HandlerP99:F1} ms (max {HandlerMax:F0}), lock wait p99 {LockP99:F1} ms (max {LockMax:F0}), preview capture avg {PreviewAvg:F1} ms (max {PreviewMax:F0}), preview shown {Shown:F1}/s skipped {Skipped:F1}/s lag avg {Lag:F0} ms, app cpu {App:F1}%, webview cpu {Web:F1}%, age avg {AgeAvg:F1} ms max {AgeMax:F0}, receivers ndi {Ndi} omt {Omt}, skipped copies {Skipped}, gpu {Gpu}",
                name, sample.OutFps, sample.Repeated, sample.Dropped, sample.CompositorFps, sample.Copy.AverageMs, sample.Copy.MaxMs,
                sample.Handler.P99Ms, sample.Handler.MaxMs, sample.LockWait.P99Ms, sample.LockWait.MaxMs, sample.PreviewCapture.AverageMs, sample.PreviewCapture.MaxMs,
                sample.PreviewShownFps, sample.PreviewSkippedFps, sample.PreviewLag.AverageMs, sample.AppCpuPercent, sample.WebViewCpuPercent,
                sample.HubAgeAvgMs, sample.HubAgeMaxMs, sample.NdiConnections, sample.OmtConnections, e.Capture.ReadbackSkipped,
                gpuReading is null ? "n/a" : string.Join(", ", gpuReading.ByAdapter.Select(a => FormattableString.Invariant($"{a.Key} {a.Value:F1}%"))));
        }

        // The first phases run as if a monitor is connected (like the earlier baselines, whose output path always copied frames).
        var screenLike = new ScreenLikeSink();
        _engine.Hub.AddSink(screenLike);

        // idle
        await _engine.NavigateAsync("about:blank", TimeSpan.FromSeconds(10));
        await Task.Delay(2000);
        await Phase("idle", TimeSpan.FromSeconds(10));
        Expect(_engine.WebViewProcessIds().Count > 0, "the WebView2 processes can be listed");

        // load: the page opens while one row per second is written, so the spike of opening it is visible
        var navigation = _engine.NavigateAsync(page, TimeSpan.FromSeconds(30));
        for (int second = 1; second <= 20; second++) await Phase($"load-{second}", TimeSpan.FromSeconds(1));
        await navigation;

        await Phase("steady", TimeSpan.FromSeconds(30));

        // NDI without and with a receiver
        Expect(window.RenameNdi("Stagecast Perf"), "the NDI name can be set");
        bool ndi = window.StartNdi();
        Expect(ndi, $"NDI can be switched on ({window.NdiMessage})");
        if (ndi)
        {
            await Task.Delay(1500);
            await Phase("ndi-0", TimeSpan.FromSeconds(20));
            using var probe = NdiProbe.Connect("(Stagecast Perf)", TimeSpan.FromSeconds(8));
            Expect(probe is not null, "a receiver can connect to NDI");
            if (probe is not null)
            {
                using var stop = new CancellationTokenSource();
                var drain = Task.Run(() => { while (!stop.IsCancellationRequested) probe.ReadLatestVideo(TimeSpan.FromMilliseconds(100)); });
                await Phase("ndi-1", TimeSpan.FromSeconds(20));
                stop.Cancel();
                await drain;
            }
            window.StopNdi(null);
        }

        // OMT without a receiver
        bool omt = window.StartOmt();
        Expect(omt, $"OMT can be switched on ({window.OmtMessage})");
        if (omt)
        {
            await Task.Delay(1500);
            await Phase("omt-0", TimeSpan.FromSeconds(20));
            window.StopOmt(null);
        }

        // With nothing wanting frames the capture should not copy them at all; a receiver-less NDI or OMT must cost almost nothing.
        _engine.Hub.RemoveSink(screenLike);
        await Phase("no-output", TimeSpan.FromSeconds(20));
        if (window.StartNdi())
        {
            await Task.Delay(1500);
            await Phase("ndi-0-alone", TimeSpan.FromSeconds(20));
            window.StopNdi(null);
        }
        if (window.StartOmt())
        {
            await Task.Delay(1500);
            await Phase("omt-0-alone", TimeSpan.FromSeconds(20));
            window.StopOmt(null);
        }

        Expect(rows >= 24, $"the scenario wrote its rows ({rows})");
        _logger.LogInformation(_failures.Count == 0 ? "PERF finished ok: {Rows} rows in {Csv}" : "PERF finished with problems: {Failures}",
            _failures.Count == 0 ? rows : string.Join("; ", _failures), csv);
    }
}
