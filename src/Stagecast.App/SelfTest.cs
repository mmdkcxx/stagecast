using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Stagecast.Core.Configuration;
using Stagecast.Core.Display;
using Stagecast.Core.Presets;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Rendering;
using Stagecast.Rendering;

namespace Stagecast.App;

/// <summary>
/// Scripted check of the render engine, run with <c>--selftest a2</c>: steady 60 fps, switching to 30 fps while running, changing
/// the resolution while running, and a page that never changes (the hub must keep the output rate by repeating the last frame).
/// </summary>
internal sealed class SelfTest
{
    private readonly RenderEngine _engine;
    private readonly ILogger _logger;
    private readonly List<string> _failures = [];

    public SelfTest(RenderEngine engine, ILogger logger)
    {
        _engine = engine;
        _logger = logger;
    }

    public async Task RunAsync(string name, MainWindow window)
    {
        if (name == "a2b") { await RunControlsAsync(window); return; }
        if (name == "a2c") { await RunChangesAndPresetsAsync(window); return; }
        if (name != "a2") { _logger.LogError("Unknown self test {Name}", name); return; }

        string animation = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "selftest-animation.html")).AbsoluteUri;
        await _engine.NavigateAsync(animation, TimeSpan.FromSeconds(20));
        await Task.Delay(1500);

        var s60 = await MeasureAsync("2520x1080 @60, moving page", 8);
        Expect(s60.Hub.DeliveredPerSecond >= 55, "output rate >= 55/s at 60 fps");
        // The capture schedule and the pump tick are separate 60 Hz clocks, so their phase decides how many ticks repeat (seen: 3 to 119 of 480).
        Expect(s60.Hub.Repeated < s60.Hub.Delivered / 2, "a moving page is mostly new frames");
        Expect(s60.Hub.SinkErrors == 0, "no sink errors");
        Expect(s60.Capture is { Width: 2520, Height: 1080 }, "frames are 2520x1080");

        window.ApplyFromCode(new Resolution(2520, 1080), 30);
        var s30 = await MeasureAsync("2520x1080 @30 (changed while running)", 6);
        Expect(s30.Hub.DeliveredPerSecond is >= 28 and <= 32, "output rate about 30/s after switching to 30 fps");

        window.ApplyFromCode(new Resolution(1920, 1080), 60);
        await Task.Delay(1500);
        var s1920 = await MeasureAsync("1920x1080 @60 (changed while running)", 6);
        Expect(s1920.Hub.DeliveredPerSecond >= 55, "output rate >= 55/s after changing the resolution");
        Expect(s1920.Capture is { Width: 1920, Height: 1080 }, "frames are 1920x1080 after the change");

        await _engine.NavigateAsync("data:text/html,<body style='background:%23123456;margin:0'></body>", TimeSpan.FromSeconds(10));
        await Task.Delay(1500);
        var still = await MeasureAsync("1920x1080 @60, page that never changes", 6);
        Expect(still.Hub.DeliveredPerSecond >= 55, "a still page still gets the full output rate");
        Expect(still.Hub.Repeated > still.Hub.Delivered * 4, "a still page is almost all repeated frames");

        window.ApplyFromCode(new Resolution(2520, 1080), 60);
        await Task.Delay(1500);
        var back = await MeasureAsync("back to 2520x1080 @60", 4);
        Expect(back.Capture is { Width: 2520, Height: 1080 }, "frames are 2520x1080 again");

        _logger.LogInformation(_failures.Count == 0 ? "A2 selftest ok" : "A2 selftest FAILED: {Failures}", string.Join("; ", _failures));
    }

    /// <summary>Confirmation of resolution/rate changes while an output is on (simulated), and the resolution presets.</summary>
    private async Task RunChangesAndPresetsAsync(MainWindow window)
    {
        var hd = new Resolution(1920, 1080);
        var wide = new Resolution(2520, 1080);
        await Task.Delay(500);

        window.ApplyFromCode(hd, 60);
        Expect(_engine.Resolution == hd && !window.BannerVisible, "with no output on, a new resolution applies at once, without a banner");

        window.SetSimulatedLive(true);
        window.ApplyFromCode(wide, 30);
        await Task.Delay(300);
        Expect(_engine.Resolution == hd && _engine.Fps == 60, "with an output on, nothing changes before confirmation");
        Expect(window.BannerVisible && window.PendingChange == new Stagecast.Core.Output.RenderChange(wide, 30), "the banner waits with the requested resolution and rate");

        window.CancelPending();
        Expect(_engine.Resolution == hd && _engine.Fps == 60 && !window.BannerVisible && window.PendingChange is null, "Batal keeps everything as it was");

        window.ApplyFromCode(wide, 30);
        window.ConfirmPending();
        await Task.Delay(1500);
        Expect(_engine.Resolution == wide && _engine.Fps == 30 && !window.BannerVisible, "Terapkan applies the waiting change");
        var stats = _engine.Stats();
        Expect(stats.Capture is { Width: 2520, Height: 1080 }, "frames follow the confirmed resolution");

        window.SetSimulatedLive(false);
        window.ApplyFromCode(hd, 60);
        Expect(_engine.Resolution == hd && _engine.Fps == 60 && !window.BannerVisible, "with the output off again, changes apply at once");

        // Presets.
        window.Confirm = _ => true;
        Expect(window.SavePreset("") == PresetError.NameEmpty, "an empty preset name is refused");
        Expect(window.SavePreset("Full HD baru") == PresetError.ResolutionInUse, "a resolution that already has a preset is refused");
        window.ApplyFromCode(new Resolution(1600, 900), 60);
        Expect(window.SavePreset(new string('x', 25)) == PresetError.NameTooLong, "a name over 24 characters is refused");
        Expect(window.SavePreset("led 7×3") == PresetError.NameInUse, "a name already used (ignoring case) is refused");
        Expect(window.SavePreset("  Panggung  ") == PresetError.None, "a valid preset is saved");
        Expect(window.SavePreset("Lagi") == PresetError.ResolutionInUse, "the same resolution cannot be saved twice");

        var reloaded = PresetCatalog.FromConfig(window.ReadConfigFromDisk());
        Expect(reloaded.All.Any(p => p.Name == "Panggung" && p.Resolution == new Resolution(1600, 900)), "the preset is in the settings file, so it survives a restart");

        Expect(!window.DeletePreset(PresetRules.BuiltIns[0]), "a built-in preset cannot be deleted");
        var mine = window.Presets.All.First(p => p.Name == "Panggung");
        bool asked = false;
        window.Confirm = _ => { asked = true; return false; };
        Expect(!window.DeletePreset(mine) && asked && window.Presets.All.Contains(mine), "declining the confirmation keeps the preset");
        window.Confirm = _ => true;
        Expect(window.DeletePreset(mine) && !window.Presets.All.Contains(mine), "deleting asks and then removes the preset");
        Expect(_engine.Resolution == new Resolution(1600, 900), "deleting the preset in use does not change the active resolution");
        Expect(!PresetCatalog.FromConfig(window.ReadConfigFromDisk()).All.Any(p => p.Name == "Panggung"), "the deleted preset is gone from the settings file");

        window.ApplyFromCode(wide, 60);
        _logger.LogInformation(_failures.Count == 0 ? "A2c selftest ok" : "A2c selftest FAILED: {Failures}", string.Join("; ", _failures));
    }

    /// <summary>Page controls: pointer mapping in Fit and 100%, preview region, panning, wheel, page zoom, keyboard and Esc, history.</summary>
    private async Task RunControlsAsync(MainWindow window)
    {
        var preview = window.Preview;
        var render = _engine.Resolution;
        string page = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "selftest-controls.html")).AbsoluteUri;
        await _engine.NavigateAsync(page, TimeSpan.FromSeconds(20));
        await Task.Delay(1000);
        preview.Interactive = true;
        var size = preview.SurfaceSize;
        _logger.LogInformation("A2b preview area {W:F0}x{H:F0} for a {Render} page", size.Width, size.Height, render);

        (double X, double Y, string? Button)[] targets =
        [
            (60, 60, "t1"), (1260, 60, "t2"), (2460, 60, "t3"), (60, 540, "t4"), (1260, 540, "t5"),
            (2460, 540, "t6"), (60, 1020, "t7"), (1260, 1020, "t8"), (2460, 1020, "t9"),
            (917.3, 411.9, null), (1801.6, 733.4, null),
            (1100, 450, null), (1400, 640, null), (1500, 500, null), (2300, 880, null), (2380, 960, null), (2000, 800, null),
        ];
        foreach (var (label, setup) in new (string, Action)[]
        {
            ("Fit", preview.ShowFit),
            ("100% centre", () => { preview.ShowActual(); preview.ShowActualAtForTest(1260, 540); }),
            ("100% right-bottom", () => preview.ShowActualAtForTest(2400, 1000)),
        })
        {
            setup();
            await Task.Delay(700);
            double worst = 0;
            int hits = 0, wrong = 0, missed = 0;
            foreach (var (x, y, button) in targets)
            {
                var at = preview.ControlPointOf(x, y);
                if (at.X < 0 || at.Y < 0 || at.X >= size.Width || at.Y >= size.Height || !preview.PageContainsControlPoint(at)) continue;
                await _engine.EvaluateAsync("window.resetClicks()");
                preview.InjectClick(at);
                var click = await WaitForClickAsync();
                if (click is null) { missed++; continue; }
                hits++;
                worst = Math.Max(worst, Math.Sqrt(Math.Pow(click.Value.X - x, 2) + Math.Pow(click.Value.Y - y, 2)));
                if (button is not null && click.Value.Id != button) wrong++;
            }
            var shown = preview.ShownPicture;
            var wantedRegion = preview.View.Region(render, size.Width, size.Height);
            _logger.LogInformation("A2b {Label}: {Hits} clicks, worst error {Worst:F2} px, wrong buttons {Wrong}, missed {Missed}; picture {PW}x{PH} at ({PX},{PY}) mip {Mip}",
                label, hits, worst, wrong, missed, shown.Width, shown.Height, shown.X, shown.Y, shown.Mip);
            Expect(hits >= 2 && worst <= 2.0 && wrong == 0 && missed == 0, $"{label}: clicks land on the intended page point");
            Expect(shown.Mip == wantedRegion.Mip, $"{label}: preview picture uses the requested level (got {shown.Mip}, wanted {wantedRegion.Mip})");
            if (label != "Fit") Expect(shown.X == wantedRegion.X && shown.Y == wantedRegion.Y, $"{label}: preview picture starts at the requested corner");
        }

        // Dragging the page in 100% moves the visible part and cannot leave the page.
        preview.ShowActualAtForTest(1260, 540);
        double before = preview.View.CenterX;
        preview.PanByForTest(-100, 0);
        Expect(Math.Abs(preview.View.CenterX - (before + 100)) < 0.5, "dragging 100 px left moves the view 100 px right");
        preview.PanByForTest(100000, 100000);
        Expect(preview.View.CenterX <= render.Width / 2.0 && preview.View.CenterX >= 0, "dragging far cannot leave the page");

        _engine.SendMouse(CoreWebView2MouseEventKind.Wheel, CoreWebView2MouseEventVirtualKeys.None, unchecked((uint)-360), new System.Drawing.Point(1300, 350));
        await Task.Delay(500);
        Expect((await ReadStateAsync()).GetProperty("scrollTop").GetDouble() > 0, "mouse wheel scrolls the page");

        foreach (double zoom in new[] { 2.0, 0.5, 1.0 })
        {
            window.SetPageZoom(zoom);
            await Task.Delay(500);
            int w = (await ReadStateAsync()).GetProperty("w").GetInt32();
            _logger.LogInformation("A2b page zoom {Zoom}: page viewport width {W}", zoom, w);
            Expect(Math.Abs(w - render.Width / zoom) <= 1, $"page zoom {zoom} changes the page viewport to {render.Width / zoom}");
        }

        // Keyboard to the page, then Esc takes it back.
        preview.ShowFit();
        await Task.Delay(500);
        Expect(await window.BringToFrontAsync(), "the window can be brought to the front for the keyboard test");
        preview.InjectClick(preview.ControlPointOf(500, 230));
        _engine.FocusPage();
        await Task.Delay(400);
        TypeText("stagecast");
        await Task.Delay(400);
        string typed = (await ReadStateAsync()).GetProperty("input").GetString() ?? "";
        _logger.LogInformation("A2b keyboard: typed '{Typed}', page has focus {Focus}", typed, _engine.PageHasFocus);
        Expect(typed == "stagecast", "keystrokes reach the page");
        Expect(_engine.PageHasFocus, "page reports keyboard focus");
        PressKey(0x1B);
        for (int i = 0; i < 20 && _engine.PageHasFocus; i++) await Task.Delay(50);
        Expect(!_engine.PageHasFocus, "Esc releases the keyboard from the page");
        TypeText("zzz");
        await Task.Delay(400);
        string after = (await ReadStateAsync()).GetProperty("input").GetString() ?? "";
        Expect(after == "stagecast", $"keys no longer reach the page after Esc (input is '{after}')");

        // History.
        await _engine.NavigateAsync("data:text/html,<body>second</body>", TimeSpan.FromSeconds(10));
        Expect(_engine.CanGoBack, "back is available after a second page");
        var back = new TaskCompletionSource();
        void Done(object? s, CoreWebView2NavigationCompletedEventArgs e) => back.TrySetResult();
        _engine.Browser.NavigationCompleted += Done;
        _engine.GoBack();
        await Task.WhenAny(back.Task, Task.Delay(5000));
        _engine.Browser.NavigationCompleted -= Done;
        Expect(_engine.CurrentUrl.EndsWith("selftest-controls.html"), "back returns to the first page");
        Expect(_engine.CanGoForward, "forward is available after going back");

        preview.Interactive = false;
        _logger.LogInformation(_failures.Count == 0 ? "A2b selftest ok" : "A2b selftest FAILED: {Failures}", string.Join("; ", _failures));
    }

    private async Task<(double X, double Y, string Id)?> WaitForClickAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var clicks = (await ReadStateAsync()).GetProperty("clicks").EnumerateArray().ToArray();
            if (clicks.Length > 0) return (clicks[^1][0].GetDouble(), clicks[^1][1].GetDouble(), clicks[^1][2].GetString() ?? "");
            await Task.Delay(20);
        }
        return null;
    }

    private async Task<JsonElement> ReadStateAsync()
        => JsonDocument.Parse(JsonSerializer.Deserialize<string>(await _engine.EvaluateAsync("window.state()"))!).RootElement.Clone();

    // Real keystrokes to whatever has the keyboard focus, like a person typing.
    private static void TypeText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (char c in text)
        {
            inputs.Add(Key(0, c, 0x0004));
            inputs.Add(Key(0, c, 0x0004 | 0x0002));
        }
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private static void PressKey(ushort virtualKey)
    {
        INPUT[] inputs = [Key(virtualKey, 0, 0), Key(virtualKey, 0, 0x0002)];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, ushort scan, uint flags) => new() { type = 1, ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } };

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    private async Task<RenderEngineStats> MeasureAsync(string label, int seconds)
    {
        _engine.ResetStats();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        var s = _engine.Stats();
        _logger.LogInformation(
            "A2 {Label}: out {Out:F1}/s (new {New}, repeated {Repeated}), published {Published:F1}/s, dropped {Dropped}, compositor {Arrived:F0}/s, frame {W}x{H}, age avg {Age:F1} ms max {AgeMax:F0}, tick p99 {P99:F1} ms max {TickMax:F0}, copy {Copy:F1} ms",
            label, s.Hub.DeliveredPerSecond, s.Hub.Delivered, s.Hub.Repeated, s.Hub.PublishedPerSecond, s.Hub.Dropped, s.Capture.ArrivedPerSecond,
            s.Capture.Width, s.Capture.Height, s.Hub.AverageAgeMs, s.Hub.MaxAgeMs, s.Hub.TickP99Ms, s.Hub.TickMaxMs, s.Capture.ReadbackAverageMs);
        return s;
    }

    private void Expect(bool condition, string what)
    {
        if (!condition) _failures.Add(what);
    }
}
