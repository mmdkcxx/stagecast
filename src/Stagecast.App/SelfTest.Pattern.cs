using Microsoft.Extensions.Logging;
using Stagecast.Core.Frames;
using Stagecast.Core.Output;

namespace Stagecast.App;

// --selftest e1: the test pattern on one unit while the others stay live.
internal sealed partial class SelfTest
{
    private sealed class TopLeftSink : IFrameSink
    {
        public string Name => "pattern-probe";

        public volatile string Colour = "";

        public void Deliver(Frame frame, bool repeated)
        {
            int o = (frame.Height / 5) * frame.Stride + (frame.Width / 28) * 4; // inside the first colour bar of the pattern
            int b = frame.Pixels[o], g = frame.Pixels[o + 1], r = frame.Pixels[o + 2];
            Colour = r > 200 && g > 200 && b > 200 ? "white" : g > 150 && r < 100 && b < 120 ? "green" : r < 30 && g < 30 && b < 30 ? "black" : $"{r}/{g}/{b}";
        }
    }

    private async Task RunPatternAsync(MainWindow window)
    {
        var live = new TopLeftSink();
        var shown = new TopLeftSink();
        bool pattern = false;
        _engine.Hub.AddSink(live);
        _engine.Hub.AddSink(shown, () => pattern);
        await _engine.NavigateAsync(Page("30B050"), TimeSpan.FromSeconds(10));
        Expect(await WaitUntilAsync(() => live.Colour == "green" && shown.Colour == "green"), $"both units show the live page first (live {live.Colour}, unit {shown.Colour})");

        pattern = true;
        Expect(await WaitUntilAsync(() => shown.Colour == "white"), $"the unit that asked for it shows the pattern (white bar, saw {shown.Colour})");
        Expect(live.Colour == "green", $"while the other unit stays on the live page (saw {live.Colour})");

        window.SetGate(GateMode.Blackout);
        Expect(await WaitUntilAsync(() => shown.Colour == "black" && live.Colour == "black"), $"Blackout beats the pattern (unit {shown.Colour}, other {live.Colour})");
        window.SetGate(GateMode.Live);
        Expect(await WaitUntilAsync(() => shown.Colour == "white"), $"and the pattern returns after it (saw {shown.Colour})");

        pattern = false;
        Expect(await WaitUntilAsync(() => shown.Colour == "green"), $"switching it off returns to the live page (saw {shown.Colour})");

        window.SetPattern("ndi", true);
        Expect(window.NdiPattern && !window.ScreenPattern && !window.OmtPattern, "the switches of the units are separate");
        window.SetPattern("ndi", false);
        Expect(!window.NdiPattern, "and can be switched off");

        // No tab on air (as after a start): every unit shows the pattern until a tab is chosen.
        window.SetNoLive(true);
        Expect(await WaitUntilAsync(() => live.Colour == "white" && shown.Colour == "white"), $"with no tab on air every unit shows the pattern (saw {live.Colour}, {shown.Colour})");
        window.SetGate(GateMode.Blackout);
        Expect(await WaitUntilAsync(() => live.Colour == "black"), $"Blackout still wins (saw {live.Colour})");
        window.SetGate(GateMode.Live);
        await window.SelectTabAsync(window.Tabs.Active.Id);
        Expect(!window.NoLive && await WaitUntilAsync(() => live.Colour == "green" && shown.Colour == "green"), $"choosing a tab puts it on air (saw {live.Colour}, {shown.Colour})");

        _engine.Hub.RemoveSink(live);
        _engine.Hub.RemoveSink(shown);
        _logger.LogInformation(_failures.Count == 0 ? "E1 selftest ok" : "E1 selftest FAILED: {Failures}", string.Join("; ", _failures));
    }
}
