using System.IO;
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
