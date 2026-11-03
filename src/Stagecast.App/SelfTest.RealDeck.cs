using System.IO;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Tabs;
using Stagecast.Rendering;

namespace Stagecast.App;

// --selftest p3 [path]: a real deck of the operator, through the app (not in check.sh: the file is theirs). Walks every slide, makes every thumbnail, closes.
internal sealed partial class SelfTest
{
    private async Task RunRealDeckAsync(MainWindow window)
    {
        string path = Environment.GetEnvironmentVariable("STAGECAST_DECK") ?? @"C:\Users\DOH\Downloads\PPT Komunikasi Pemasaran Sesi 7.pptx";
        if (!File.Exists(path))
        {
            _logger.LogInformation("P3 selftest ok (skipped: no deck at {Path})", path);
            return;
        }

        window.Confirm = _ => true;
        var probe = new ProbeSink();
        _engine.Hub.AddSink(probe);
        Expect(await window.AddPowerPointTabAsync(path), "the real deck can be added");
        var ppt = (_engine.Active as DocumentTab)?.PowerPoint;
        if (ppt is null) { FinishReal(probe); return; }
        _logger.LogInformation("P3 deck: {Count} slides, warnings: {Warnings}", ppt.PageCount, string.Join(" | ", ppt.Warnings));
        Expect(await WaitUntilAsync(() => probe.Row.Length > 0, 10000), "its first slide reaches the output");

        int moved = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (await ppt.NextAsync()) moved++;
        // a next also steps the animations of a slide, so it can move more often than there are slides
        Expect(moved >= ppt.LastVisibleIndex - ppt.FirstVisible && ppt.PageIndex == ppt.LastVisibleIndex, $"next walks to the last slide and stays there (moved {moved}, now {ppt.PageIndex}, last {ppt.LastVisibleIndex})");
        _logger.LogInformation("P3 walked {Moved} slides in {Ms} ms", moved, watch.ElapsedMilliseconds);
        await Task.Delay(1000);
        Expect(ppt.Problem is null, $"no problem is reported (problem {ppt.Problem})");

        int made = 0;
        for (int i = 0; i < ppt.PageCount; i++) if (await ppt.RenderThumbnailAsync(i, 256, 144) is not null) made++;
        Expect(made == ppt.PageCount, $"a thumbnail for every slide ({made} of {ppt.PageCount})");

        await window.SelectTabAsync(window.Tabs.Items[0].Id);
        foreach (var info in window.Tabs.Items.Where(t => t.Kind == TabKind.PowerPoint).ToList()) await window.CloseTabAsync(info.Id);
        FinishReal(probe);
    }

    private void FinishReal(ProbeSink probe)
    {
        _engine.Hub.RemoveSink(probe);
        _logger.LogInformation(_failures.Count == 0 ? "P3 selftest ok" : "P3 selftest FAILED: {Failures}", string.Join("; ", _failures));
    }
}
