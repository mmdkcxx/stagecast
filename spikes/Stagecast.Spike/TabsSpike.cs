using System.Diagnostics;
using System.IO;
using System.Numerics;
using Microsoft.Web.WebView2.Core;
using Stagecast.Core.Rendering;
using Windows.UI.Composition;

namespace Stagecast.Spike;

/// <summary>
/// S9: two browsers (two tabs), each in its own composition visual and its own WebView2 profile (so its own browser process).
/// A constant "stage" visual holds whichever tab is active, so the capture session never restarts when the source changes.
/// The inactive tab is muted. Each tab is a solid colour with its own tone, which lets the test read from the captured
/// frames which tab is on screen and from per-tab loopback which tab is audible.
/// </summary>
internal sealed class TabsSpike : IDisposable
{
    private sealed record Tab(string Name, string Color, int Freq, ContainerVisual Visual)
    {
        public CoreWebView2CompositionController Controller { get; set; } = null!;
        public CoreWebView2 Core => Controller.CoreWebView2;
        public int BrowserPid => (int)Core.BrowserProcessId;
        public double Peak;
    }

    private static readonly Resolution Size = new(2520, 1080);

    private readonly object _gate = new();
    private readonly List<Tab> _tabs = new();
    private IntPtr _dispatcherQueueController;
    private Compositor? _compositor;
    private ContainerVisual? _stage;
    private int _color; // 1 = red, 2 = blue, as sampled from the centre of the captured frame
    private bool _hideInactive;

    public async Task<bool> RunAsync(IntPtr hwnd, string dataRoot, bool hideInactive, string html)
    {
        _hideInactive = hideInactive;
        await CreateTabsAsync(hwnd, dataRoot, shared: false, html);
        Activate(0);
        await Task.Delay(1500);

        using var capture = new ContinuousCapture(_stage!, 30, SampleColor, 33);
        capture.Start();
        var meters = _tabs.Select(t => new ProcessLoopbackCapture(t.BrowserPid, (buf, frames) =>
        {
            float peak = 0;
            for (int i = 0; i < frames * ProcessLoopbackCapture.Channels; i++) peak = Math.Max(peak, Math.Abs(buf[i]));
            lock (_gate) t.Peak = Math.Max(t.Peak, peak);
        })).ToList();

        bool allOk = true;
        try
        {
            await Task.Delay(1000);
            int[] sequence = [0, 1, 0, 1];
            foreach (int active in sequence) allOk &= await MeasureSwitchAsync(active);
        }
        finally
        {
            foreach (var m in meters) m.Dispose();
        }
        return allOk;
    }

    private async Task CreateTabsAsync(IntPtr hwnd, string dataRoot, bool shared, string html)
    {
        _dispatcherQueueController = DispatcherQueueHelper.Create();
        _compositor = new Compositor();
        _stage = _compositor.CreateContainerVisual();
        _stage.Size = new Vector2(Size.Width, Size.Height);
        CoreWebView2Environment? sharedEnv = null;

        foreach (var (name, color, freq) in new[] { ("A", "red", 440), ("B", "blue", 880) })
        {
            var visual = _compositor.CreateContainerVisual();
            visual.Size = new Vector2(Size.Width, Size.Height);
            var tab = new Tab(name, color, freq, visual);
            var env = shared
                ? sharedEnv ??= await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(dataRoot, "WebView2-s12-shared"),
                    options: new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required" })
                : await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(dataRoot, $"WebView2-s9-{name}"),
                    options: new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required" });
            tab.Controller = await env.CreateCoreWebView2CompositionControllerAsync(hwnd);
            tab.Controller.RootVisualTarget = visual;
            tab.Controller.ShouldDetectMonitorScaleChanges = false;
            tab.Controller.RasterizationScale = 1.0;
            tab.Controller.Bounds = new System.Drawing.Rectangle(0, 0, Size.Width, Size.Height);
            var loaded = new TaskCompletionSource<bool>();
            tab.Core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            tab.Core.Navigate($"{new Uri(html).AbsoluteUri}?name={name}&color={color}&freq={freq}");
            if (!await loaded.Task) throw new InvalidOperationException($"tab {name} failed to load");
            _tabs.Add(tab);
            SpikeLog.Write($"tab {name} loaded, browser pid {tab.BrowserPid}{(shared ? " (shared browser process)" : "")}");
        }
    }

    private async Task<bool> MeasureSwitchAsync(int active)
    {
        var tab = _tabs[active];
        int expectedColor = active == 0 ? 1 : 2;
        var clock = Stopwatch.StartNew();
        Activate(active);
        while (_color != expectedColor && clock.ElapsedMilliseconds < 3000) await Task.Delay(5);
        long latency = clock.ElapsedMilliseconds;
        bool colorOk = _color == expectedColor;

        await Task.Delay(700); // let queued audio drain after the mute change
        var cpuBefore = _tabs.Select(CpuOf).ToArray();
        lock (_gate) foreach (var t in _tabs) t.Peak = 0;
        var wall = Stopwatch.StartNew();
        await Task.Delay(2500);
        double[] cpu = _tabs.Select((t, i) => (CpuOf(t) - cpuBefore[i]).TotalSeconds / wall.Elapsed.TotalSeconds / Environment.ProcessorCount * 100).ToArray();
        double[] db;
        lock (_gate) db = _tabs.Select(t => t.Peak <= 1e-9 ? -120.0 : 20 * Math.Log10(t.Peak)).ToArray();

        bool audioOk = db[active] > -40 && db[1 - active] < -80;
        SpikeLog.Write($"S9 switch to {tab.Name}: image {(colorOk ? "ok" : "WRONG")} after {latency} ms; " +
            $"audio A={db[0]:F1} dBFS B={db[1]:F1} dBFS ({(audioOk ? "ok" : "WRONG")}); browser tree CPU A={cpu[0]:F1}% B={cpu[1]:F1}%");
        return colorOk && audioOk;
    }

    /// <summary>Makes <paramref name="index"/> the visible, audible tab and mutes (and optionally hides) the other.</summary>
    private void Activate(int index)
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            bool active = i == index;
            _tabs[i].Core.IsMuted = !active;
            if (_hideInactive) _tabs[i].Controller.IsVisible = active;
        }
        _stage!.Children.RemoveAll();
        _stage.Children.InsertAtTop(_tabs[index].Visual);
    }

    private void SampleColor(byte[] pixels, int width, int height, int stride)
    {
        int offset = (height / 2) * stride + (width / 2) * 4; // BGRA
        byte b = pixels[offset], r = pixels[offset + 2];
        _color = r > 150 && b < 100 ? 1 : b > 150 && r < 100 ? 2 : 0;
    }


    // ---- S12: preview and program at the same time -------------------------------------------------------------------

    private readonly List<(long Ticks, int Color)> _programLog = new();
    private int _program; // index of the tab that is on program

    private sealed record Phase(CaptureSummary Fps, (double AvgMs, double MaxMs, long Count) Readback, double HostCpu, double BrowserCpu, List<GpuSampler.Reading> Gpu);

    /// <summary>
    /// Both tabs are rendered and captured all the time. The program tab's frames are copied to the CPU at full size (the cost
    /// the real outputs will have); the preview tab's frames are shrunk on the GPU first. TAKE only moves which stream is
    /// "program", so nothing restarts.
    /// </summary>
    public async Task<bool> RunDualAsync(IntPtr hwnd, string dataRoot, bool shared, string html, int seconds, double previewMinUpdateMs = 1)
    {
        await CreateTabsAsync(hwnd, dataRoot, shared, html);
        _program = 0;
        _tabs[0].Core.IsMuted = false;
        _tabs[1].Core.IsMuted = true;
        _tabs[1].Controller.IsVisible = false;
        await Task.Delay(1500);

        using var gpu = new GpuSampler();
        bool ok = true;
        using var capA = new ContinuousCapture(_tabs[0].Visual, 60, (_, _, _, _) => { }, 66) { PreviewMipLevel = 2, ProgramEnabled = true };
        using var capB = new ContinuousCapture(_tabs[1].Visual, 60, (_, _, _, _) => { }, 66) { PreviewMipLevel = 2, MinUpdateMs = previewMinUpdateMs };
        capA.OnProgramFrame = (px, w, h, stride) => OnProgramFrame(0, px, w, h, stride);
        capB.OnProgramFrame = (px, w, h, stride) => OnProgramFrame(1, px, w, h, stride);
        capA.Start();
        await Task.Delay(1000);

        // 1. One tab only (the S5 situation, but with the full-size CPU copy for every frame).
        var single = await MeasureAsync("one tab (program only)", seconds, capA, gpu, operatePreview: false);
        ok &= single.Fps.DeliveredFps >= 55;

        // 2. Preview tab on as well: visible, captured at 15 fps through the GPU downscale, and operated (mouse moving).
        _tabs[1].Controller.IsVisible = true;
        capB.Start();
        await Task.Delay(1000);
        var dual = await MeasureAsync("program + preview (preview operated)", seconds, capA, gpu, operatePreview: true);
        ok &= dual.Fps.DeliveredFps >= 55;
        var previewStats = capB.ReadbackStats();
        SpikeLog.Write($"S12 preview stream: {capB.Summarize().DeliveredFps:F1} fps delivered by the capture, shrunk readback {previewStats.AvgMs:F2} ms avg / {previewStats.MaxMs:F2} ms max over {previewStats.Count} frames");

        // 3. Audio: only the program tab may reach the program's loopback.
        ok &= await AudioLeakAsync(shared);

        // 4. Takes.
        for (int i = 0; i < 6; i++) ok &= await TakeAsync(capA, capB);
        return ok;
    }

    private void OnProgramFrame(int tab, byte[] pixels, int width, int height, int stride)
    {
        int offset = (height / 2) * stride + (width / 2) * 4;
        byte b = pixels[offset], r = pixels[offset + 2];
        int color = r > 150 && b < 100 ? 1 : b > 150 && r < 100 ? 2 : 0;
        lock (_programLog) _programLog.Add((Stopwatch.GetTimestamp(), color));
    }

    private async Task<Phase> MeasureAsync(string label, int seconds, ContinuousCapture program, GpuSampler gpu, bool operatePreview)
    {
        var hostPids = new HashSet<int> { Environment.ProcessId };
        var browserPids = new HashSet<int>();
        foreach (var t in _tabs) browserPids.UnionWith(TreePids(t.BrowserPid));
        var all = new HashSet<int>(hostPids); all.UnionWith(browserPids);
        gpu.Sample(all); // the first reading of a counter is always 0

        program.ResetStats();
        var hostCpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        var browserCpu0 = CpuOf(browserPids);
        var wall = Stopwatch.StartNew();
        var readings = new List<GpuSampler.Reading>();
        var rng = new Random(7);
        while (wall.Elapsed.TotalSeconds < seconds)
        {
            // Operating the preview tab: a mouse move every ~33 ms, like a person dragging.
            if (operatePreview) _tabs[1].Controller.SendMouseInput(CoreWebView2MouseEventKind.Move, CoreWebView2MouseEventVirtualKeys.None, 0,
                new System.Drawing.Point(rng.Next(0, Size.Width), rng.Next(0, Size.Height)));
            await Task.Delay(33);
        }
        double elapsed = wall.Elapsed.TotalSeconds;
        for (int i = 0; i < 3; i++) { readings.AddRange(gpu.Sample(all)); await Task.Delay(500); }

        var fps = program.Summarize();
        double host = (Process.GetCurrentProcess().TotalProcessorTime - hostCpu0).TotalSeconds / elapsed / Environment.ProcessorCount * 100;
        double browsers = (CpuOf(browserPids) - browserCpu0).TotalSeconds / elapsed / Environment.ProcessorCount * 100;
        var gpuAvg = readings.GroupBy(r => r.Adapter).Select(g => new GpuSampler.Reading(g.Key, g.Average(r => r.ThreeD), g.Average(r => r.Copy), g.Average(r => r.Video))).ToList();
        var readback = program.ReadbackStats();
        SpikeLog.Write($"S12 {label}: program {fps.DeliveredFps:F1} fps (arrived {fps.ArrivedFps:F0}/s, p99 {fps.P99IntervalMs:F1} ms, max {fps.MaxIntervalMs:F1} ms, >25 ms gaps {fps.Stutters}), " +
            $"full readback {readback.AvgMs:F2} ms avg / {readback.MaxMs:F2} ms max; CPU host {host:F1}% + browsers {browsers:F1}% (of all cores); " +
            $"GPU {string.Join("; ", gpuAvg.Select(g => $"{g.Adapter}: 3D {g.ThreeD:F1}% copy {g.Copy:F1}% video {g.Video:F1}%"))}");
        return new Phase(fps, readback, host, browsers, gpuAvg);
    }

    private async Task<bool> AudioLeakAsync(bool shared)
    {
        var samples = new List<float>();
        using (new ProcessLoopbackCapture(_tabs[_program].BrowserPid, (buf, frames) =>
        {
            lock (samples) for (int i = 0; i < frames; i++) samples.Add(buf[i * ProcessLoopbackCapture.Channels]);
        }))
        {
            await Task.Delay(3500);
        }
        float[] mono;
        lock (samples) mono = samples.Skip(ProcessLoopbackCapture.SampleRate / 2).ToArray(); // skip the first 0.5 s
        double own = ToneDb(mono, _tabs[_program].Freq), other = ToneDb(mono, _tabs[1 - _program].Freq);
        bool ok = own > -30 && other < -60;
        SpikeLog.Write($"S12 audio ({(shared ? "both tabs in ONE browser process" : "separate browser processes")}): program tab tone {_tabs[_program].Freq} Hz = {own:F1} dBFS, " +
            $"preview tab tone {_tabs[1 - _program].Freq} Hz = {other:F1} dBFS (muted) {(ok ? "PASS" : "FAIL")}");
        return ok;
    }

    // Level of a single frequency in dBFS (Goertzel); a full-scale sine reads 0 dBFS.
    private static double ToneDb(float[] mono, double freq)
    {
        double w = 2 * Math.PI * freq / ProcessLoopbackCapture.SampleRate, coeff = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        foreach (float x in mono) { double s0 = x + coeff * s1 - s2; s2 = s1; s1 = s0; }
        double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
        double amplitude = 2 * Math.Sqrt(Math.Max(power, 0)) / mono.Length;
        return amplitude < 1e-9 ? -120 : 20 * Math.Log10(amplitude);
    }

    private async Task<bool> TakeAsync(ContinuousCapture capA, ContinuousCapture capB)
    {
        int from = _program, to = 1 - _program;
        var captures = new[] { capA, capB };
        lock (_programLog) _programLog.Clear();
        await Task.Delay(200);
        lock (_programLog) _programLog.Clear();

        long t0 = Stopwatch.GetTimestamp();
        _tabs[to].Core.IsMuted = false;
        _tabs[from].Core.IsMuted = true;
        captures[from].ProgramEnabled = false;
        captures[to].ProgramEnabled = true;
        _program = to;

        int expected = to == 0 ? 1 : 2;
        long? firstNew = null;
        var clock = Stopwatch.StartNew();
        while (firstNew is null && clock.ElapsedMilliseconds < 2000)
        {
            lock (_programLog) firstNew = _programLog.Where(e => e.Color == expected && e.Ticks >= t0).Select(e => (long?)e.Ticks).FirstOrDefault();
            await Task.Delay(1);
        }
        await Task.Delay(400);
        (long Ticks, int Color)[] log;
        lock (_programLog) log = _programLog.Where(e => e.Ticks >= t0).ToArray();
        double latency = firstNew is null ? double.NaN : Stopwatch.GetElapsedTime(t0, firstNew.Value).TotalMilliseconds;
        int unknown = log.Count(e => e.Color == 0);
        int back = firstNew is null ? 0 : log.Count(e => e.Ticks > firstNew.Value && e.Color == (to == 0 ? 2 : 1)); // old tab reappearing after the swap
        bool ok = firstNew is not null && unknown == 0 && back == 0 && latency < 100;
        SpikeLog.Write($"S12 take {(from == 0 ? "A" : "B")} -> {(to == 0 ? "A" : "B")}: first new program frame after {latency:F1} ms; program frames in 400 ms window={log.Length}, black/other={unknown}, old tab reappeared={back} {(ok ? "PASS" : "FAIL")}");
        return ok;
    }

    private static TimeSpan CpuOf(IEnumerable<int> pids)
    {
        var total = TimeSpan.Zero;
        foreach (int pid in pids)
        {
            try { total += Process.GetProcessById(pid).TotalProcessorTime; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return total;
    }

    private static TimeSpan CpuOf(Tab tab)
    {
        var pids = TreePids(tab.BrowserPid);
        var total = TimeSpan.Zero;
        foreach (int pid in pids)
        {
            try { total += Process.GetProcessById(pid).TotalProcessorTime; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return total;
    }

    private static HashSet<int> TreePids(int root)
    {
        using var searcher = new System.Management.ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process");
        var rows = searcher.Get().Cast<System.Management.ManagementBaseObject>()
            .Select(o => (Pid: Convert.ToInt32(o["ProcessId"]), Parent: Convert.ToInt32(o["ParentProcessId"]))).ToList();
        var set = new HashSet<int> { root };
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var r in rows.Where(r => set.Contains(r.Parent) && set.Add(r.Pid))) grew = true;
        }
        return set;
    }

    public void Dispose()
    {
        foreach (var t in _tabs) t.Controller.Close();
        _stage?.Dispose();
        _compositor?.Dispose();
        if (_dispatcherQueueController != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(_dispatcherQueueController);
    }
}
