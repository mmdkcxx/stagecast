using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Stagecast.Core.Display;
using Stagecast.Core.Rendering;

namespace Stagecast.Spike;

/// <summary>
/// S10: operating a page from the preview. Uses the same mapping code the app will use (<see cref="ViewTransform"/>)
/// to turn preview points into page points, then checks where the page says the click landed, that wheel and
/// keyboard reach the page, what page zoom does, and how long a click takes to show up in the captured frame.
/// </summary>
internal sealed class ControlSpike
{
    private static readonly Resolution Render = new(2520, 1080);

    private readonly CompositionSpike _page;
    private volatile int _sampledRed; // 1 when the sample point of the captured frame is red, 0 when blue/other

    public ControlSpike(CompositionSpike page) => _page = page;

    public async Task<bool> RunAsync(System.Windows.Window window)
    {
        using var capture = _page.StartPreview(SampleBackground, 33);
        await Task.Delay(1000);
        bool ok = true;
        ok &= await AccuracyAsync();
        ok &= await WheelAsync();
        ok &= await KeyboardAsync(window);
        ok &= await ZoomAsync();
        await CursorAsync();
        ok &= await LatencyAsync();
        return ok;
    }

    private async Task<bool> AccuracyAsync()
    {
        (string Name, ViewTransform T)[] views =
        [
            ("Fit in 1280x720", ViewTransform.Create(Render, 1280, 720, ViewMode.Fit)),
            ("100% centre", ViewTransform.Create(Render, 1280, 720, ViewMode.Actual, 1260, 540)),
            ("100% right-bottom", ViewTransform.Create(Render, 1280, 720, ViewMode.Actual, 2400, 1000)),
        ];
        // Page points to hit in every view if visible: inside buttons 1-9 (button centres) and some free points.
        (double X, double Y, string? Button)[] targets =
        [
            (60, 60, "t1"), (1260, 60, "t2"), (2460, 60, "t3"), (60, 540, "t4"), (1260, 540, "t5"),
            (2460, 540, "t6"), (60, 1020, "t7"), (1260, 1020, "t8"), (2460, 1020, "t9"),
            (917.3, 411.9, null), (1801.6, 733.4, null), (2331.1, 912.7, null),
        ];

        bool allOk = true;
        foreach (var (name, view) in views)
        {
            double maxVsIntended = 0, maxVsSent = 0;
            int hits = 0, wrongButton = 0;
            foreach (var (x, y, button) in targets)
            {
                var (cx, cy) = view.ToControl(x, y);
                if (cx < 0 || cy < 0 || cx >= 1280 || cy >= 720) continue; // not visible in this view
                // A real mouse reports whole pixels: round, then map back exactly as the app will.
                var (px, py) = view.ToPage(Math.Round(cx), Math.Round(cy));
                await _page.EvalAsync("window.resetClicks()");
                var point = new System.Drawing.Point((int)Math.Round(px), (int)Math.Round(py));
                _page.SendMouse(CoreWebView2MouseEventKind.LeftButtonDown, CoreWebView2MouseEventVirtualKeys.LeftButton, 0, point);
                _page.SendMouse(CoreWebView2MouseEventKind.LeftButtonUp, CoreWebView2MouseEventVirtualKeys.None, 0, point);
                var clicks = await WaitForClicksAsync();
                if (clicks.Length == 0) { allOk = false; SpikeLog.Write($"S10 {name}: no click arrived for page ({x},{y})"); continue; }
                var (rx, ry, id) = clicks[^1];
                maxVsSent = Math.Max(maxVsSent, Dist(rx - point.X, ry - point.Y));
                maxVsIntended = Math.Max(maxVsIntended, Dist(rx - x, ry - y));
                if (button is not null && id != button) wrongButton++;
                hits++;
            }
            bool ok = hits > 0 && maxVsIntended <= 2.0 && maxVsSent < 0.5 && wrongButton == 0;
            allOk &= ok;
            SpikeLog.Write($"S10 accuracy {name}: scale={view.Scale:F3} points={hits} max error vs intended={maxVsIntended:F2}px, vs sent={maxVsSent:F2}px, wrong buttons={wrongButton} {(ok ? "PASS" : "FAIL")}");
        }
        return allOk;
    }

    private static double Dist(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);

    private async Task<bool> WheelAsync()
    {
        _page.SendMouse(CoreWebView2MouseEventKind.Wheel, CoreWebView2MouseEventVirtualKeys.None, unchecked((uint)-360), new System.Drawing.Point(1300, 350));
        await Task.Delay(500);
        var state = await ReadStateAsync();
        bool ok = state.GetProperty("scrollTop").GetDouble() > 0;
        SpikeLog.Write($"S10 wheel: scrollTop={state.GetProperty("scrollTop").GetDouble()} {(ok ? "PASS" : "FAIL")}");
        return ok;
    }

    private async Task<bool> KeyboardAsync(System.Windows.Window window)
    {
        window.Activate();
        _page.FocusBrowser();
        var inputPoint = new System.Drawing.Point(500, 230);
        _page.SendMouse(CoreWebView2MouseEventKind.LeftButtonDown, CoreWebView2MouseEventVirtualKeys.LeftButton, 0, inputPoint);
        _page.SendMouse(CoreWebView2MouseEventKind.LeftButtonUp, CoreWebView2MouseEventVirtualKeys.None, 0, inputPoint);
        await Task.Delay(300);
        TypeText("stagecast 2520");
        await Task.Delay(500);
        string typed = (await ReadStateAsync()).GetProperty("input").GetString() ?? "";
        bool ok = typed == "stagecast 2520";
        SpikeLog.Write($"S10 keyboard: typed '{typed}' foreground={GetForegroundWindow() == new System.Windows.Interop.WindowInteropHelper(window).Handle} {(ok ? "PASS" : "FAIL")}");
        return ok;
    }

    private async Task<bool> ZoomAsync()
    {
        bool ok = true;
        foreach (double zoom in new[] { 2.0, 0.5, 1.0 })
        {
            _page.Controller.ZoomFactor = zoom;
            await Task.Delay(400);
            var s = await ReadStateAsync();
            int w = s.GetProperty("w").GetInt32(), h = s.GetProperty("h").GetInt32();
            double dpr = s.GetProperty("dpr").GetDouble();
            bool match = Math.Abs(w - Render.Width / zoom) <= 1 && Math.Abs(h - Render.Height / zoom) <= 1;
            ok &= match;
            SpikeLog.Write($"S10 page zoom {zoom:F1}: page viewport {w}x{h} dpr={dpr} (render stays {Render}) {(match ? "PASS" : "FAIL")}");
        }
        return ok;
    }

    private async Task CursorAsync()
    {
        var seen = new List<string>();
        void Handler(object? s, object e) => seen.Add(_page.Controller.Cursor.ToString() ?? "?");
        _page.Controller.CursorChanged += Handler;
        _page.SendMouse(CoreWebView2MouseEventKind.Move, CoreWebView2MouseEventVirtualKeys.None, 0, new System.Drawing.Point(300, 940)); // over #hover
        await Task.Delay(300);
        _page.SendMouse(CoreWebView2MouseEventKind.Move, CoreWebView2MouseEventVirtualKeys.None, 0, new System.Drawing.Point(1200, 800)); // away
        await Task.Delay(300);
        _page.Controller.CursorChanged -= Handler;
        SpikeLog.Write($"S10 cursor: CursorChanged fired {seen.Count} times, system cursor handle values: {string.Join(", ", seen)}");
    }

    private async Task<bool> LatencyAsync()
    {
        var flash = new System.Drawing.Point(750, 950);
        var samples = new List<long>();
        for (int i = 0; i < 12; i++)
        {
            int before = _sampledRed;
            var clock = Stopwatch.StartNew();
            _page.SendMouse(CoreWebView2MouseEventKind.LeftButtonDown, CoreWebView2MouseEventVirtualKeys.LeftButton, 0, flash);
            _page.SendMouse(CoreWebView2MouseEventKind.LeftButtonUp, CoreWebView2MouseEventVirtualKeys.None, 0, flash);
            while (_sampledRed == before && clock.ElapsedMilliseconds < 2000) await Task.Delay(1);
            if (_sampledRed != before) samples.Add(clock.ElapsedMilliseconds);
            await Task.Delay(300);
        }
        samples.Sort();
        bool ok = samples.Count == 12;
        SpikeLog.Write($"S10 click-to-frame latency over {samples.Count}/12 clicks: min={samples.FirstOrDefault()} ms median={(samples.Count > 0 ? samples[samples.Count / 2] : -1)} ms max={samples.LastOrDefault()} ms (frame sampled every 33 ms) {(ok ? "PASS" : "FAIL")}");
        return ok;
    }

    private void SampleBackground(byte[] pixels, int width, int height, int stride)
    {
        int offset = 700 * stride + 1000 * 4; // free background area, away from every control
        _sampledRed = pixels[offset + 2] > 150 && pixels[offset] < 100 ? 1 : 0;
    }

    private async Task<(double X, double Y, string Id)[]> WaitForClicksAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var clicks = (await ReadStateAsync()).GetProperty("clicks").EnumerateArray()
                .Select(c => (c[0].GetDouble(), c[1].GetDouble(), c[2].GetString() ?? "")).ToArray();
            if (clicks.Length > 0) return clicks;
            await Task.Delay(20);
        }
        return [];
    }

    private async Task<JsonElement> ReadStateAsync()
    {
        string json = JsonSerializer.Deserialize<string>(await _page.EvalAsync("window.state()"))!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    // Real operating-system keystrokes (unicode packets) sent to whatever has keyboard focus, like a person typing.
    private static void TypeText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (char c in text)
        {
            inputs.Add(KeyInput(c, 0x0004));          // KEYEVENTF_UNICODE
            inputs.Add(KeyInput(c, 0x0004 | 0x0002)); // ... | KEYEVENTF_KEYUP
        }
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyInput(char c, uint flags) => new() { type = 1, ki = new KEYBDINPUT { wScan = c, dwFlags = flags } };

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
