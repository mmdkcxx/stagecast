using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Display;
using Stagecast.Core.Rendering;
using Stagecast.Core.Tabs;
using Stagecast.Rendering;

namespace Stagecast.App;

// --selftest p2: PowerPoint tabs, with a deck the test writes itself (one solid colour per slide) so the pictures can be checked pixel by pixel.
internal sealed partial class SelfTest
{
    private static int Rgb(int r, int g, int b) => r | (g << 8) | (b << 16);

    /// <summary>
    /// Writes a 4:3 deck of five slides, one colour each: red (hidden), green, blue, orange, grey (hidden). Null when PowerPoint cannot be driven.
    /// A PowerPoint the operator already runs is used and left running.
    /// </summary>
    private static string? WriteDeck(string path)
    {
        Type? type = Type.GetTypeFromProgID("PowerPoint.Application");
        if (type is null) return null;
        bool wasRunning = Process.GetProcessesByName("POWERPNT").Length > 0;
        dynamic app = Activator.CreateInstance(type)!;
        try
        {
            dynamic presentation = app.Presentations.Add(0);
            presentation.PageSetup.SlideWidth = 720f;
            presentation.PageSetup.SlideHeight = 540f;
            int[] colours = [Rgb(255, 0, 0), Rgb(0, 255, 0), Rgb(0, 0, 255), Rgb(255, 150, 0), Rgb(128, 128, 128)];
            for (int i = 1; i <= colours.Length; i++)
            {
                dynamic slide = presentation.Slides.AddSlide(i, presentation.SlideMaster.CustomLayouts[7]);
                slide.FollowMasterBackground = 0;
                slide.Background.Fill.ForeColor.RGB = colours[i - 1];
                if (i is 1 or 5) slide.SlideShowTransition.Hidden = -1;
            }

            presentation.SaveAs(path, 24);
            presentation.Close();
            return path;
        }
        finally
        {
            if (!wasRunning)
            {
                app.Quit();
                // PowerPoint is slow to leave, and a source that attaches while it is leaving loses its presentation: wait for it, and end the one this test started.
                for (int i = 0; i < 200 && Process.GetProcessesByName("POWERPNT").Length > 0; i++) Thread.Sleep(100);
                foreach (Process left in Process.GetProcessesByName("POWERPNT")) left.Kill();
            }
        }
    }

    private async Task RunPowerPointAsync(MainWindow window)
    {
        string dir = Path.Combine(Path.GetTempPath(), "stagecast-selftest-pptx");
        Directory.CreateDirectory(dir);
        string deck = Path.Combine(dir, "lima slide.pptx");
        string broken = Path.Combine(dir, "rusak.pptx");
        File.Delete(deck);
        bool wasRunning = Process.GetProcessesByName("POWERPNT").Length > 0; // the operator's own PowerPoint, before the test writes its deck
        string? written = null;
        try { written = WriteDeck(deck); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            _logger.LogWarning(ex, "P2 could not write the test deck");
        }

        if (written is null)
        {
            _logger.LogInformation("P2 selftest ok (skipped: PowerPoint cannot be driven on this PC)");
            return;
        }

        File.WriteAllText(broken, "this is not a pptx at all");
        window.Confirm = _ => true;

        var probe = new ProbeSink();
        _engine.Hub.AddSink(probe);
        await _engine.NavigateAsync(Page("30B050"), TimeSpan.FromSeconds(10));
        Expect(await WaitUntilAsync(() => probe.Row.Contains("other")), $"the web tab is on air first (saw {probe.Row})");

        // ---- open: the show starts on the first slide that is not hidden ----
        Expect(await window.AddPowerPointTabAsync(deck), "a PowerPoint tab can be added");
        var info = window.Tabs.Active;
        Expect(info.Kind == TabKind.PowerPoint && info.Source == deck && info.Name == "lima slide", $"the list records the file and names the tab after it (name {info.Name})");
        var tab = _engine.Active as DocumentTab;
        var ppt = tab?.PowerPoint;
        Expect(ppt is { PageCount: 5, PageNumber: 2 }, $"five slides, on the second because the first is hidden (count {ppt?.PageCount}, slide {ppt?.PageNumber})");
        Expect(ppt is { } d && d.IsHidden(0) && !d.IsHidden(1) && d.IsHidden(4) && d.FirstVisible == 1 && d.LastVisibleIndex == 3, "slides 1 and 5 are hidden");
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("green", wide: false), 10000), $"slide 2 is green, 4:3 with 540 px bars (saw {probe.Row})");
        if (ppt is null) { FinishPowerPoint(probe); return; }
        Expect(ppt.Borrowed == wasRunning, $"a PowerPoint that was already running is borrowed, otherwise it is ours (borrowed {ppt.Borrowed}, was running {wasRunning})");

        // ---- transparent bars: only when a network output sends alpha, and never in a blackout ----
        Expect(probe.BarAlpha == 255, $"the bars are opaque by default (alpha {probe.BarAlpha})");
        window.SetNdiAlpha(true);
        Expect(await WaitUntilAsync(() => probe.BarAlpha == 0 && probe.Row == Pattern("green", wide: false), 6000), $"with NDI alpha on the bars are transparent and the slide is untouched (alpha {probe.BarAlpha}, saw {probe.Row})");
        window.SetGate(Stagecast.Core.Output.GateMode.Blackout);
        Expect(await WaitUntilAsync(() => probe.BarAlpha == 255, 3000), $"a blackout is opaque black (alpha {probe.BarAlpha})");
        window.SetGate(Stagecast.Core.Output.GateMode.Live);
        window.SetNdiAlpha(false);
        Expect(await WaitUntilAsync(() => probe.BarAlpha == 255 && probe.Row == Pattern("green", wide: false), 6000), $"and opaque again when it is off (alpha {probe.BarAlpha}, saw {probe.Row})");

        // ---- turning slides: hidden ones are skipped, and the last slide is not left ----
        Expect(await ppt.NextAsync() && ppt.PageNumber == 3, $"next goes to slide 3 (slide {ppt.PageNumber})");
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("blue", wide: false)), $"slide 3 is blue (saw {probe.Row})");
        Expect(await ppt.NextAsync() && ppt.PageNumber == 4, $"next goes to slide 4 (slide {ppt.PageNumber})");
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("orange", wide: false)), $"slide 4 is orange (saw {probe.Row})");
        Expect(!await ppt.NextAsync() && ppt.PageNumber == 4, $"next at the last slide shown stays and skips the hidden slide 5 (slide {ppt.PageNumber})");
        await Task.Delay(1000);
        Expect(probe.Row == Pattern("orange", wide: false), $"no end-of-show screen appears (saw {probe.Row})");
        Expect(await ppt.PreviousAsync() && ppt.PageNumber == 3, "previous goes back to slide 3");
        Expect(await ppt.PreviousAsync() && ppt.PageNumber == 2, "and to slide 2");
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("green", wide: false)), $"slide 2 is green again (saw {probe.Row})");

        // ---- jumping, hidden slides included ----
        int asked = 0;
        window.Confirm = _ => { asked++; return true; };
        Expect(await window.GoToPageAsync(0) && ppt.PageNumber == 1 && asked == 1, $"a hidden slide can be asked for after a confirmation (slide {ppt.PageNumber}, asked {asked})");
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("red", wide: false)), $"the hidden slide 1 shows (saw {probe.Row})");
        Expect(await window.GoToPageAsync(MainWindow.LastIndex(ppt)) && ppt.PageNumber == 4, $"the end goes to the last slide that is not hidden (slide {ppt.PageNumber})");
        Expect(await window.GoToPageAsync(MainWindow.FirstIndex(ppt)) && ppt.PageNumber == 2, $"the start goes to the first slide that is not hidden (slide {ppt.PageNumber})");
        Expect(await window.JumpToTypedPageAsync("3") && ppt.PageNumber == 3, "typing 3 goes to slide 3");
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("blue", wide: false)), $"slide 3 is blue (saw {probe.Row})");

        // ---- thumbnails of every slide, hidden ones included ----
        var thumbs = new List<PageThumbnail?>();
        for (int i = 0; i < 5; i++) thumbs.Add(await ppt.RenderThumbnailAsync(i, 256, 144));
        Expect(thumbs.All(t => t is not null), "a thumbnail exists for all five slides");
        Expect(thumbs[0] is { Width: 192, Height: 144 }, $"a 4:3 slide's thumbnail keeps its shape (size {thumbs[0]?.Width}x{thumbs[0]?.Height})");
        Expect(thumbs[1] is { } green && green.Pixels[(72 * green.Width + 96) * 4 + 1] > 200, "the thumbnail of slide 2 is green");

        // ---- a new render size ----
        window.ApplyFromCode(new Resolution(1920, 1080), 60);
        Expect(await WaitUntilAsync(() => probe.Row.Split(' ') is { Length: 10 } cells && cells[2] == "bk" && cells[3] == "blue" && cells[5] == "blue"), $"at 1920x1080 the 4:3 slide has 240 px bars (saw {probe.Row})");
        window.ApplyFromCode(new Resolution(2520, 1080), 60);
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("blue", wide: false)), $"back at 2520x1080 the 540 px bars return (saw {probe.Row})");

        // ---- reload comes back to the same slide; a bad file changes nothing ----
        Expect(await window.ReloadPdfAsync() && ppt.PageNumber == 3 && ppt.PageCount == 5, $"reload opens the file again on the same slide (slide {ppt.PageNumber})");
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("blue", wide: false), 10000), $"and slide 3 shows again (saw {probe.Row})");
        Expect(!await window.ChangePdfAsync(broken) && ppt.PageCount == 5, "a file that is not a deck is refused");
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("blue", wide: false)), $"and the slide on screen stays (saw {probe.Row})");
        Expect(window.Tabs.Active.Source == deck, "and the tab keeps its file");

        // ---- closing the tab ends the show and leaves PowerPoint as it was ----
        await window.SelectTabAsync(window.Tabs.Items[0].Id);
        Expect(await window.CloseTabAsync(info.Id) && _engine.TabById(info.Id) is null, "a PowerPoint tab closes");
        await Task.Delay(1500);
        Expect(!ShowWindowExists(), "its slide show window is gone");
        if (!wasRunning) Expect(await WaitUntilAsync(() => Process.GetProcessesByName("POWERPNT").Length == 0, 15000), "PowerPoint, started by the tab, is closed with it");
        else Expect(Process.GetProcessesByName("POWERPNT").Length > 0, "PowerPoint that was already running is left running");

        FinishPowerPoint(probe);
    }

    private static bool ShowWindowExists() =>
        Process.GetProcessesByName("POWERPNT").Any(p => p.MainWindowTitle.StartsWith("PowerPoint Slide Show", StringComparison.Ordinal) && p.MainWindowTitle.Contains("stagecast-pptx-", StringComparison.Ordinal));

    private void FinishPowerPoint(ProbeSink probe)
    {
        _engine.Hub.RemoveSink(probe);
        _logger.LogInformation(_failures.Count == 0 ? "P2 selftest ok" : "P2 selftest FAILED: {Failures}", string.Join("; ", _failures));
    }
}
