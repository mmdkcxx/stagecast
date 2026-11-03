using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Tabs;
using Stagecast.Rendering;

namespace Stagecast.App;

// --selftest p5: what goes wrong at an event. A PowerPoint of the operator with another document, PowerPoint killed in the middle of a show,
// a file that is gone, and a long run of slide turns. Steps that kill PowerPoint are skipped when the operator's own PowerPoint is running.
internal sealed partial class SelfTest
{
    private static void KillPowerPoint()
    {
        foreach (Process p in Process.GetProcessesByName("POWERPNT")) { try { p.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
        for (int i = 0; i < 100 && Process.GetProcessesByName("POWERPNT").Length > 0; i++) Thread.Sleep(100);
    }

    private async Task RunPowerPointRobustAsync(MainWindow window)
    {
        string dir = Path.Combine(Path.GetTempPath(), "stagecast-selftest-pptx");
        Directory.CreateDirectory(dir);
        string deck = Path.Combine(dir, "lima slide.pptx");
        File.Delete(deck);
        if (Process.GetProcessesByName("POWERPNT").Length > 0)
        {
            _logger.LogInformation("P5 selftest ok (skipped: the operator's PowerPoint is running and these steps would end it)");
            return;
        }

        string? written = null;
        try { written = WriteDeck(deck); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            _logger.LogWarning(ex, "P5 could not write the test deck");
        }

        if (written is null)
        {
            _logger.LogInformation("P5 selftest ok (skipped: PowerPoint cannot be driven on this PC)");
            return;
        }

        window.Confirm = _ => true;
        var probe = new ProbeSink();
        _engine.Hub.AddSink(probe);
        await _engine.NavigateAsync(Page("30B050"), TimeSpan.FromSeconds(10));
        var webTab = window.Tabs.Active!.Id;

        // ---- a PowerPoint of the operator with a document that is not saved: the show comes and goes, the document stays ----
        Type type = Type.GetTypeFromProgID("PowerPoint.Application")!;
        dynamic guest = Activator.CreateInstance(type)!;
        dynamic guestDocument = guest.Presentations.Add(0);
        guestDocument.Slides.AddSlide(1, guestDocument.SlideMaster.CustomLayouts[7]);
        int guestPid = Process.GetProcessesByName("POWERPNT").First().Id;
        Expect(await window.AddPowerPointTabAsync(deck), "a deck can be added to a PowerPoint that is already in use");
        var tab = _engine.Active as DocumentTab;
        Expect(tab?.PowerPoint is { Borrowed: true, PageCount: 5 }, $"that PowerPoint is borrowed (borrowed {tab?.PowerPoint?.Borrowed})");
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("green", wide: false), 10000), $"and the deck shows (saw {probe.Row})");
        await window.SelectTabAsync(webTab);
        Expect(await window.CloseTabAsync(tab!.Id), "the tab closes");
        await Task.Delay(2000);
        int documents = -1;
        try { documents = (int)guest.Presentations.Count; } catch (System.Runtime.InteropServices.COMException) { }
        Expect(Process.GetProcessesByName("POWERPNT").Any(p => p.Id == guestPid) && documents == 1, $"the operator's PowerPoint and its document are untouched (documents {documents})");
        try { guestDocument.Saved = -1; guestDocument.Close(); guest.Quit(); } catch (System.Runtime.InteropServices.COMException) { }
        for (int i = 0; i < 100 && Process.GetProcessesByName("POWERPNT").Length > 0; i++) await Task.Delay(100);
        KillPowerPoint();

        // ---- PowerPoint killed in the middle of a show ----
        Expect(await window.AddPowerPointTabAsync(deck), "a deck can be added");
        var current = _engine.Active as DocumentTab;
        var ppt = current?.PowerPoint;
        if (current is null || ppt is null) { FinishRobust(probe); return; }
        tab = current;
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("green", wide: false), 10000), $"it shows (saw {probe.Row})");
        KillPowerPoint();
        Expect(await WaitUntilAsync(() => current.Problem is not null, 8000), $"a PowerPoint that is gone is reported on the tab (problem {current.Problem})");
        var asked = Stopwatch.StartNew();
        bool moved = await ppt.NextAsync();
        Expect(!moved && asked.Elapsed < TimeSpan.FromSeconds(20), $"turning a slide with no PowerPoint fails without hanging (moved {moved}, {asked.ElapsedMilliseconds} ms)");
        Expect(probe.Row == Pattern("green", wide: false), $"and the output keeps its last picture (saw {probe.Row})");

        // ---- Reload brings it back ----
        Expect(await window.ReloadPdfAsync() && ppt.Problem is null, $"Reload starts PowerPoint again (problem {ppt.Problem})");
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("green", wide: false), 15000), $"and the slide shows again (saw {probe.Row})");
        Expect(await ppt.NextAsync() && ppt.PageNumber == 3, "which turns");

        // ---- progress while a file opens, and a give-up when PowerPoint stays silent ----
        var seen = new List<double>();
        var reopened = await ppt.OpenAsync(deck, 1, (fraction, _) => { lock (seen) seen.Add(fraction); });
        bool rising;
        lock (seen) rising = seen.Count >= 4 && seen.Zip(seen.Skip(1), (a, b) => b >= a).All(x => x) && seen[^1] == 1;
        Expect(reopened == PowerPointOpenResult.Opened && rising, $"opening reports progress that only rises and ends at 100% ({reopened}, reports {string.Join(" ", seen.Select(x => x.ToString("0.00")))})");
        var silent = await ppt.OpenAsync(deck, 1, null, TimeSpan.FromMilliseconds(1));
        Expect(silent is PowerPointOpenResult.TimedOut or PowerPointOpenResult.Opened, $"an open that reports nothing in time is given up on (got {silent})");
        if (silent == PowerPointOpenResult.TimedOut) Expect(ppt.Problem?.Contains("tidak menjawab") == true, $"and says why (problem {ppt.Problem})");
        await Task.Delay(1500); // the cancelled call stops at its next step
        Expect(await ppt.NextAsync() || await ppt.PreviousAsync(), "the deck that was showing still turns after that");

        // ---- the file is gone ----
        File.Delete(deck);
        Expect(!await window.ReloadPdfAsync() || current.Problem is not null, "a file that is gone is refused or reported");
        Expect(current.Problem is not null && ppt.PageCount == 5, $"with the old deck kept (problem {current.Problem}, slides {ppt.PageCount})");
        Expect(await ppt.NextAsync() && ppt.PageNumber == 4, "and it still turns");

        // ---- a long run of turns ----
        var watch = Stopwatch.StartNew();
        int steps = 0;
        for (int i = 0; i < 150; i++)
        {
            if (i % 3 == 2) await ppt.PreviousAsync(); else await ppt.NextAsync();
            steps++;
        }

        Expect(ppt.PageIndex is >= 1 and <= 3, $"{steps} turns in a row end on a real slide (slide {ppt.PageNumber}, {watch.ElapsedMilliseconds} ms)");
        string[] colours = ["red", "green", "blue", "orange", "grey"];
        Expect(await WaitUntilAsync(() => probe.Row == Pattern(colours[ppt.PageIndex], wide: false)), $"and the output shows the slide the counter says (slide {ppt.PageNumber}, saw {probe.Row})");

        await window.SelectTabAsync(webTab);
        await window.CloseTabAsync(current.Id);
        await Task.Delay(1500);
        Expect(await WaitUntilAsync(() => Process.GetProcessesByName("POWERPNT").Length == 0, 15000), "PowerPoint is closed with the last tab");
        FinishRobust(probe);
    }

    private void FinishRobust(ProbeSink probe)
    {
        _engine.Hub.RemoveSink(probe);
        _logger.LogInformation(_failures.Count == 0 ? "P5 selftest ok" : "P5 selftest FAILED: {Failures}", string.Join("; ", _failures));
    }
}
