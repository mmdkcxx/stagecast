using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Display;
using Stagecast.Core.Rendering;
using Stagecast.Core.Frames;
using Stagecast.Core.Input;
using Stagecast.Core.Tabs;
using Stagecast.Rendering;

namespace Stagecast.App;

// --selftest d5: PDF tabs, with PDFs the test writes itself (one solid colour per page) so the pictures can be checked pixel by pixel.
internal sealed partial class SelfTest
{
    // Columns sampled on the middle row of a 2520 px wide frame (scaled for other widths): 16:9 pages leave 300 px bars, 4:3 pages 540 px.
    private static readonly int[] ProbeColumns = [50, 294, 306, 530, 550, 1260, 1970, 1990, 2213, 2226];

    /// <summary>Names the colour of the picture at a few columns of every frame: "bk" black, "red", "green", "blue", "orange", "other".</summary>
    private sealed class ProbeSink : IFrameSink
    {
        public string Name => "pdf-probe";

        public volatile string Row = "";

        /// <summary>Alpha of the left-most pixel of the middle row (a bar around a 4:3 page), -1 before the first frame.</summary>
        public volatile int BarAlpha = -1;

        public void Deliver(Frame frame, bool repeated)
        {
            var names = new string[ProbeColumns.Length];
            for (int i = 0; i < ProbeColumns.Length; i++)
            {
                int x = Math.Min(frame.Width - 1, ProbeColumns[i] * frame.Width / 2520);
                int offset = frame.Height / 2 * frame.Stride + x * 4;
                names[i] = ColourName(frame.Pixels[offset + 2], frame.Pixels[offset + 1], frame.Pixels[offset]);
            }
            Row = string.Join(" ", names);
            BarAlpha = frame.Pixels[frame.Height / 2 * frame.Stride + 3];
        }

        private static string ColourName(int r, int g, int b)
        {
            if (r < 40 && g < 40 && b < 40) return "bk";
            if (r > 200 && g < 70 && b < 70) return "red";
            if (g > 200 && r < 70 && b < 70) return "green";
            if (b > 200 && r < 70 && g < 70) return "blue";
            if (r > 200 && g is > 100 and < 200 && b < 70) return "orange";
            return "other";
        }
    }

    // What the probe shows for a page of one colour: wide (16:9, bars of 300) or 4:3 (bars of 540).
    private static string Pattern(string colour, bool wide)
    {
        string[] mask = wide ? ["bk", "bk", "P", "P", "P", "P", "P", "P", "P", "bk"] : ["bk", "bk", "bk", "bk", "P", "P", "P", "bk", "bk", "bk"];
        return string.Join(" ", mask.Select(m => m == "P" ? colour : m));
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int milliseconds = 4000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    /// <summary>Writes a PDF whose pages are filled with one colour each: (width, height in points, PDF colour operands).</summary>
    private static void WritePdf(string path, params (int Width, int Height, string Rgb)[] pages)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "" };
        var kids = new StringBuilder();
        foreach (var (width, height, rgb) in pages)
        {
            int pageObject = objects.Count + 1;
            kids.Append($"{pageObject} 0 R ");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Contents {pageObject + 1} 0 R >>");
            string content = $"{rgb} rg 0 0 {width} {height} re f";
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        objects[1] = $"<< /Type /Pages /Kids [{kids}] /Count {pages.Length} >>";

        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(text.Length);
            text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = text.Length;
        text.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, text.ToString(), Encoding.ASCII);
    }

    private async Task RunPdfAsync(MainWindow window)
    {
        string dir = Path.Combine(Path.GetTempPath(), "stagecast-selftest-pdf");
        Directory.CreateDirectory(dir);
        string three = Path.Combine(dir, "tiga halaman.pdf");
        string single = Path.Combine(dir, "satu.pdf");
        string broken = Path.Combine(dir, "rusak.pdf");
        string missing = Path.Combine(dir, "tidak-ada.pdf");
        WritePdf(three, (960, 540, "1 0 0"), (800, 600, "0 1 0"), (960, 540, "0 0 1"));
        WritePdf(single, (800, 600, "1 0.6 0"));
        File.WriteAllText(broken, "this is not a pdf at all");
        File.Delete(missing);
        window.Confirm = _ => true; // the in-app confirmations of reload and change file

        var probe = new ProbeSink();
        _engine.Hub.AddSink(probe);
        await _engine.NavigateAsync(Page("30B050"), TimeSpan.FromSeconds(10));
        Expect(await WaitUntilAsync(() => probe.Row.Contains("other")), $"the web tab is on air first (saw {probe.Row})");

        // ---- open ----
        Expect(await window.AddPdfTabAsync(three), "a PDF tab can be added");
        var info = window.Tabs.Active!;
        Expect(info.Kind == TabKind.Pdf && info.Source == three && info.Name == "tiga halaman", $"the list records the file and names the tab after it (name {info.Name})");
        var tab = _engine.Active as DocumentTab;
        var pdf = tab?.Pdf;
        Expect(pdf is { PageCount: 3, PageNumber: 1 }, $"three pages, on the first (count {pdf?.PageCount}, page {pdf?.PageNumber})");
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("red", wide: true)), $"page 1 is red with 300 px bars (saw {probe.Row})");
        if (pdf is null) { Finish(probe); return; }

        // ---- turning pages ----
        Expect(await pdf.NextAsync() && pdf.PageNumber == 2, "next goes to page 2");
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("green", wide: false)), $"page 2 is a green 4:3 page with 540 px bars (saw {probe.Row})");
        Expect(await window.JumpToTypedPageAsync("3") && pdf.PageNumber == 3, "typing 3 goes to page 3");
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("blue", wide: true)), $"page 3 is blue (saw {probe.Row})");
        Expect(!await window.JumpToTypedPageAsync("9") && !await window.JumpToTypedPageAsync("abc") && !await window.JumpToTypedPageAsync("0") && pdf.PageNumber == 3, "a page number out of range changes nothing");
        Expect(!await pdf.NextAsync() && pdf.PageNumber == 3, "next at the last page stays");
        Expect(await window.GoToPageAsync(0) && pdf.PageNumber == 1, "first page");
        Expect(!await pdf.PreviousAsync() && pdf.PageNumber == 1, "previous at the first page stays");

        // Two quick clicks while a page is still drawing move two pages.
        Task<bool> first = pdf.NextAsync(), second = pdf.NextAsync();
        await Task.WhenAll(first, second);
        Expect(await WaitUntilAsync(() => pdf.PageNumber == 3), $"two quick Next clicks reach page 3 (page {pdf.PageNumber})");
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("blue", wide: true)), $"and the output shows page 3 (saw {probe.Row})");

        // ---- clicker commands ----
        Expect(await window.RunClickerAsync(ClickerCommand.Previous) && await WaitUntilAsync(() => pdf.PageNumber == 2), "clicker previous");
        Expect(await window.RunClickerAsync(ClickerCommand.Restart) && await WaitUntilAsync(() => pdf.PageNumber == 1), "clicker start over");
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("red", wide: true)), $"back on the red page (saw {probe.Row})");

        // ---- a new render size ----
        window.ApplyFromCode(new Resolution(1920, 1080), 60);
        Expect(await WaitUntilAsync(() => probe.Row.StartsWith("red red red red")), $"at 1920x1080 a 16:9 page fills the frame (saw {probe.Row})");
        window.ApplyFromCode(new Resolution(2520, 1080), 60);
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("red", wide: true)), $"back at 2520x1080 the bars return (saw {probe.Row})");

        // ---- Preview + Take: the page on air turns while another tab is in the preview ----
        await window.SelectTabAsync(window.Tabs.Items[0].Id);
        Expect(await window.SetPreviewModeAsync(true), "Preview + Take starts");
        await window.SelectTabAsync(info.Id);
        Expect(await window.TakeAsync() && _engine.Program is DocumentTab, "the PDF is taken to air");
        await window.SelectTabAsync(window.Tabs.Items[0].Id); // the web tab goes back to the preview
        Expect(_engine.Active is PageTab && _engine.Program is DocumentTab, "the web tab is in the preview, the PDF on air");
        Expect(window.ClickerTarget() == pdf, "the clicker turns the page of the tab on air");
        Expect(await window.RunClickerAsync(ClickerCommand.Next) && await WaitUntilAsync(() => probe.Row == Pattern("green", wide: false)), $"the program page turned while the preview is another tab (saw {probe.Row})");
        Expect(await window.SetPreviewModeAsync(false), "leaving Preview + Take");
        await window.SelectTabAsync(info.Id);

        // ---- files that cannot be opened leave what is on screen alone ----
        Expect(!await window.ChangePdfAsync(broken), "a file that is not a PDF is refused");
        Expect(window.Tabs.Active!.Source == three && tab!.Source == three, "the tab keeps its file after a refused one");
        Expect(pdf.PageCount == 3 && await WaitUntilAsync(() => probe.Row == Pattern("green", wide: false)), $"the old document keeps showing (saw {probe.Row})");
        Expect(tab!.Problem is not null, "the tab reports a problem");
        Expect(!await window.ChangePdfAsync(missing), "a file that does not exist is refused");
        Expect(pdf.PageCount == 3 && probe.Row == Pattern("green", wide: false), $"still the old document (saw {probe.Row})");
        Expect(await window.ReloadPdfAsync() && pdf.PageCount == 3 && await WaitUntilAsync(() => pdf.PageNumber == 2), $"reload returns to the same page (page {pdf.PageNumber})");
        Expect(tab.Problem is null, "a good reload clears the problem");
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("green", wide: false)), $"and shows it (saw {probe.Row})");

        // ---- rapid paging, then a tab closed while pages are drawing ----
        var jumps = new List<Task<bool>>();
        for (int i = 0; i < 60; i++) jumps.Add(i % 3 == 0 ? pdf.PreviousAsync() : pdf.NextAsync());
        try { await Task.WhenAll(jumps); }
        catch (Exception ex) { Expect(false, $"rapid paging threw {ex.GetType().Name}"); }
        Expect(pdf.PageIndex is >= 0 and <= 2, $"rapid paging ends on a real page (index {pdf.PageIndex})");
        string expectedColour = new[] { "red", "green", "blue" }[pdf.PageIndex];
        Expect(await WaitUntilAsync(() => probe.Row == Pattern(expectedColour, pdf.PageIndex != 1)), $"the output shows the page the counter says (page {pdf.PageNumber}, saw {probe.Row})");

        // ---- change the file ----
        Expect(await window.ChangePdfAsync(single), "another PDF can be chosen");
        Expect(window.Tabs.Active!.Source == single && pdf.PageCount == 1, "the tab now plays the new file");
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("orange", wide: false)), $"the new file is on screen (saw {probe.Row})");

        // ---- the settings file ----
        await Task.Delay(1500);
        var entry = window.ReadConfigFromDisk().Tabs.FirstOrDefault(t => t.Kind == "pdf");
        Expect(entry is not null && entry.Source == single && entry.Url == TabRules.BlankUrl, "the settings file records the PDF tab and its file");

        // ---- close during a render, and a PDF tab can be added again ----
        _ = pdf.GoToAsync(0);
        await window.SelectTabAsync(window.Tabs.Items[0].Id);
        Expect(await window.CloseTabAsync(info.Id) && _engine.TabById(info.Id) is null, "a PDF tab closes while a page is drawing");
        await Task.Delay(500);
        Expect(await window.AddPdfTabAsync(three) && _engine.Active is DocumentTab { Pdf.PageCount: 3 }, "another PDF tab can be added afterwards");
        Expect(await WaitUntilAsync(() => probe.Row == Pattern("red", wide: true)), $"and shows its first page (saw {probe.Row})");

        // ---- an optional real document ----
        if (Environment.GetEnvironmentVariable("STAGECAST_TEST_PDF") is { Length: > 0 } real && File.Exists(real))
        {
            Expect(await window.AddPdfTabAsync(real), "the real PDF tab can be added");
            var realPdf = (_engine.Active as DocumentTab)?.Pdf;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool wentLast = realPdf is not null && await realPdf.GoToAsync(int.MaxValue);
            _logger.LogInformation("D5 real PDF {File}: {Pages} pages, last page drawn in {Ms} ms", Path.GetFileName(real), realPdf?.PageCount, watch.ElapsedMilliseconds);
            Expect(realPdf is { PageCount: > 0 } && wentLast, "the real PDF's last page draws");
        }

        Finish(probe);
    }

    private void Finish(ProbeSink probe)
    {
        _engine.Hub.RemoveSink(probe);
        _logger.LogInformation(_failures.Count == 0 ? "D5 selftest ok" : "D5 selftest FAILED: {Failures}", string.Join("; ", _failures));
    }
}
