using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Tabs;
using Stagecast.Rendering;

namespace Stagecast.App;

// --selftest p4: PowerPoint tabs in Preview + Take: a deck in the preview, Take without a restart, the clicker on the program deck, two decks at once.
internal sealed partial class SelfTest
{
    private async Task RunPowerPointPreviewAsync(MainWindow window)
    {
        string dir = Path.Combine(Path.GetTempPath(), "stagecast-selftest-pptx");
        Directory.CreateDirectory(dir);
        string deck = Path.Combine(dir, "lima slide.pptx");
        File.Delete(deck);
        bool wasRunning = Process.GetProcessesByName("POWERPNT").Length > 0;
        string? written = null;
        try { written = WriteDeck(deck); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            _logger.LogWarning(ex, "P4 could not write the test deck");
        }

        if (written is null)
        {
            _logger.LogInformation("P4 selftest ok (skipped: PowerPoint cannot be driven on this PC)");
            return;
        }

        window.Confirm = _ => true;
        var probe = new ProbeSink();
        _engine.Hub.AddSink(probe);
        await _engine.NavigateAsync(Page("30B050"), TimeSpan.FromSeconds(10));
        var webTab = window.Tabs.Active.Id;
        Expect(await WaitUntilAsync(() => probe.Row.Contains("other")), $"the web tab is on air first (saw {probe.Row})");

        // ---- a deck in the preview: it runs there, nothing of it reaches the output ----
        Expect(await window.AddPowerPointTabAsync(deck), "a deck can be added");
        var deckA = window.Tabs.Active.Id;
        var pptA = (_engine.Active as DocumentTab)?.PowerPoint;
        if (pptA is null) { FinishPreview(probe); return; }
        int pid = pptA.ProcessId;
        await window.SelectTabAsync(webTab);
        Expect(await window.SetPreviewModeAsync(true), "Preview + Take starts with the web tab on air");
        await window.SelectTabAsync(deckA);
        Expect(_engine.Active == _engine.TabById(deckA) && _engine.Program is PageTab, "the deck is in the preview, the web tab on air");
        Expect(await pptA.NextAsync() && pptA.PageNumber == 3, $"the deck can be turned in the preview (slide {pptA.PageNumber})");
        await Task.Delay(800);
        Expect(probe.Row.Contains("other") && !probe.Row.Contains("blue"), $"and nothing of it is on the output (saw {probe.Row})");
        Expect(_engine.SilentAudioProcessIds.Contains(pid), "its sound is not passed on while it is in the preview");

        // ---- Take: the slide that was prepared goes on air, the show is not restarted ----
        Expect(await window.TakeAsync() && _engine.Program is DocumentTab, "Take puts the deck on air");
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("blue", wide: false)), $"the prepared slide 3 is on the output (saw {probe.Row})");
        Expect(pptA.PageNumber == 3 && pptA.ProcessId == pid, $"the show was not restarted (slide {pptA.PageNumber})");
        Expect(!_engine.SilentAudioProcessIds.Contains(pid), "and its sound is passed on now");

        // ---- the program deck is turned by the clicker and the buttons while another tab is the preview ----
        Expect(window.ClickerTarget() == pptA, "the clicker turns the deck on air");
        Expect(await window.RunClickerAsync(Stagecast.Core.Input.ClickerCommand.Next) && await WaitUntilAsync(() => probe.Row == Pattern("orange", wide: false)), $"slide 4 reaches the output (saw {probe.Row})");
        Expect(await window.RunClickerAsync(Stagecast.Core.Input.ClickerCommand.Previous) && await window.RunClickerAsync(Stagecast.Core.Input.ClickerCommand.Previous) && await WaitUntilAsync(() => probe.Row == Pattern("green", wide: false)), $"two previous take it back to slide 2 (saw {probe.Row})");

        // ---- the program bar turns the deck on air, and asks before a hidden slide goes on air ----
        int asked = 0;
        window.Confirm = _ => { asked++; return false; };
        Expect(window.ProgramPdf == pptA, "the program bar belongs to the deck on air");
        Expect(!await window.GoToPageAsync(0, window.ProgramPdf) && asked == 1 && pptA.PageNumber == 2, $"a hidden slide asks first, and no stays on slide 2 (asked {asked}, slide {pptA.PageNumber})");
        window.Confirm = _ => { asked++; return true; };
        Expect(await window.GoToPageAsync(0, window.ProgramPdf) && await WaitUntilAsync(() => probe.Row == Pattern("red", wide: false)), $"yes puts the hidden slide on air (asked {asked}, saw {probe.Row})");
        Expect(await window.GoToPageAsync(1, window.ProgramPdf) && await WaitUntilAsync(() => probe.Row == Pattern("green", wide: false)), $"and back to slide 2 (saw {probe.Row})");
        window.Confirm = _ => true;

        // ---- a second deck in the preview while the first is on air ----
        Expect(await window.AddPowerPointTabAsync(deck), "a second deck can be added in Preview + Take");
        var deckB = window.Tabs.Active.Id;
        var pptB = (_engine.TabById(deckB) as DocumentTab)?.PowerPoint;
        Expect(pptB is { PageCount: 5 }, $"it opens (slides {pptB?.PageCount})");
        _logger.LogInformation("P4 second deck: pid {Pid} borrowed {Borrowed}; first pid {First} borrowed {FirstBorrowed}", pptB?.ProcessId, pptB?.Borrowed, pid, pptA.Borrowed);
        if (pptB is not null)
        {
            Expect(await pptB.NextAsync() && pptB.PageNumber == 3, "the second deck turns in the preview");
            await Task.Delay(800);
            Expect(probe.Row == Pattern("green", wide: false), $"while the first stays on the output (saw {probe.Row})");
            Expect(await window.TakeAsync(), "Take swaps them");
            Expect(await WaitUntilAsync(() => probe.Row == Pattern("blue", wide: false)), $"the second deck's slide 3 is on the output (saw {probe.Row})");
            Expect(await pptA.NextAsync() && pptA.PageNumber == 3, "the first deck, now in the preview, still turns");

            // The deck that started PowerPoint is closed first: the other show has to survive it.
            await window.CloseTabAsync(deckA);
            await Task.Delay(1500);
            Expect(await pptB.NextAsync() && pptB.PageNumber == 4, $"the other deck keeps working after the first closed (slide {pptB.PageNumber})");
            Expect(await WaitUntilAsync(() => probe.Row == Pattern("orange", wide: false)), $"and reaches the output (saw {probe.Row})");
        }

        // ---- closing ----
        await window.SetPreviewModeAsync(false);
        await window.SelectTabAsync(webTab);
        foreach (var info in window.Tabs.Items.Where(t => t.Kind == TabKind.PowerPoint).ToList()) await window.CloseTabAsync(info.Id);
        await Task.Delay(1500);
        if (!wasRunning) Expect(await WaitUntilAsync(() => Process.GetProcessesByName("POWERPNT").Length == 0, 15000), "PowerPoint, started by the tabs, is closed with the last one");
        FinishPreview(probe);
    }

    private void FinishPreview(ProbeSink probe)
    {
        _engine.Hub.RemoveSink(probe);
        _logger.LogInformation(_failures.Count == 0 ? "P4 selftest ok" : "P4 selftest FAILED: {Failures}", string.Join("; ", _failures));
    }
}
