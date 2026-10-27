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
        _dispatcherQueueController = DispatcherQueueHelper.Create();
        _compositor = new Compositor();
        _stage = _compositor.CreateContainerVisual();
        _stage.Size = new Vector2(Size.Width, Size.Height);

        foreach (var (name, color, freq) in new[] { ("A", "red", 440), ("B", "blue", 880) })
        {
            var visual = _compositor.CreateContainerVisual();
            visual.Size = new Vector2(Size.Width, Size.Height);
            var tab = new Tab(name, color, freq, visual);
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(dataRoot, $"WebView2-s9-{name}"),
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
            SpikeLog.Write($"tab {name} loaded, browser pid {tab.BrowserPid}, profile WebView2-s9-{name}");
        }

        Activate(0);
        await Task.Delay(1500);

        using var capture = new ContinuousCapture(_stage, 30, SampleColor, 33);
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
