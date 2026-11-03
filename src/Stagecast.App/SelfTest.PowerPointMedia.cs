using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Tabs;
using Stagecast.Rendering;

namespace Stagecast.App;

// --selftest p1: sound and video in a PowerPoint deck. The test lets PowerPoint make its own media: a WAV tone goes into a deck that plays it by
// itself, and a two-slide deck (red, then green, 2 s each) is exported by PowerPoint as an MP4 that a third deck plays full slide.
internal sealed partial class SelfTest
{
    private static void WriteTone(string path, double hz, int seconds)
    {
        const int rate = 44100;
        int samples = rate * seconds;
        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(samples * 2);
        for (int i = 0; i < samples; i++) w.Write((short)(Math.Sin(2 * Math.PI * hz * i / rate) * 6000));
    }

    // A one-slide 4:3 deck whose slide has a media file that starts by itself (a "play" effect, 83, after the previous one, 3).
    private static void WriteMediaDeck(dynamic app, string path, string media, bool fullSlide, int background)
    {
        dynamic presentation = app.Presentations.Add(0);
        presentation.PageSetup.SlideWidth = 720f;
        presentation.PageSetup.SlideHeight = 540f;
        dynamic slide = presentation.Slides.AddSlide(1, presentation.SlideMaster.CustomLayouts[7]);
        slide.FollowMasterBackground = 0;
        slide.Background.Fill.ForeColor.RGB = background;
        dynamic shape = fullSlide
            ? slide.Shapes.AddMediaObject2(media, 0, -1, 0f, 0f, 720f, 540f)
            : slide.Shapes.AddMediaObject2(media, 0, -1, 20f, 20f, 64f, 64f);
        slide.TimeLine.MainSequence.AddEffect(shape, 83, 0, 3);
        File.Delete(path);
        presentation.SaveAs(path, 24);
        presentation.Close();
    }

    /// <summary>Makes the audio deck and the video deck with PowerPoint itself. False when PowerPoint cannot be driven.</summary>
    private bool WriteMediaDecks(string dir, string audioDeck, string videoDeck)
    {
        Type? type = Type.GetTypeFromProgID("PowerPoint.Application");
        if (type is null) return false;
        string tone = Path.Combine(dir, "nada.wav");
        string clip = Path.Combine(dir, "klip.mp4");
        string clipSource = Path.Combine(dir, "sumber klip.pptx");
        WriteTone(tone, 440, 20);
        File.Delete(clip);
        dynamic app = Activator.CreateInstance(type)!;
        try
        {
            WriteMediaDeck(app, audioDeck, tone, false, Rgb(40, 80, 200));

            dynamic source = app.Presentations.Add(0);
            source.PageSetup.SlideWidth = 720f;
            source.PageSetup.SlideHeight = 540f;
            foreach (int colour in new[] { Rgb(255, 0, 0), Rgb(0, 255, 0) })
            {
                dynamic slide = source.Slides.AddSlide(source.Slides.Count + 1, source.SlideMaster.CustomLayouts[7]);
                slide.FollowMasterBackground = 0;
                slide.Background.Fill.ForeColor.RGB = colour;
            }

            File.Delete(clipSource);
            source.SaveAs(clipSource, 24);
            source.CreateVideo(clip, 0, 2, 480, 30, 60);
            var wait = Stopwatch.StartNew();
            while ((int)source.CreateVideoStatus is 1 or 2 && wait.Elapsed < TimeSpan.FromSeconds(90)) Thread.Sleep(300);
            source.Close();
            if (!File.Exists(clip)) return false;
            WriteMediaDeck(app, videoDeck, clip, true, Rgb(0, 0, 0));
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            _logger.LogWarning(ex, "P1 could not write the test media decks");
            return false;
        }
        finally
        {
            if (!_pptWasRunning)
            {
                try { app.Quit(); } catch (System.Runtime.InteropServices.COMException) { }
                for (int i = 0; i < 200 && Process.GetProcessesByName("POWERPNT").Length > 0; i++) Thread.Sleep(100);
                foreach (Process left in Process.GetProcessesByName("POWERPNT")) left.Kill();
            }
        }
    }

    private bool _pptWasRunning;

    private async Task RunPowerPointMediaAsync(MainWindow window)
    {
        string dir = Path.Combine(Path.GetTempPath(), "stagecast-selftest-pptx");
        Directory.CreateDirectory(dir);
        string audioDeck = Path.Combine(dir, "bersuara.pptx");
        string videoDeck = Path.Combine(dir, "berklip.pptx");
        _pptWasRunning = Process.GetProcessesByName("POWERPNT").Length > 0;
        if (!WriteMediaDecks(dir, audioDeck, videoDeck))
        {
            _logger.LogInformation("P1 selftest ok (skipped: PowerPoint cannot make the test media on this PC)");
            return;
        }

        var audio = window.Audio!;
        double Peak() => Math.Max(audio.Levels.LeftDb, audio.Levels.RightDb);
        async Task<double> MaxPeakAsync(int ms)
        {
            double best = -120;
            for (int waited = 0; waited < ms; waited += 50)
            {
                best = Math.Max(best, Peak());
                await Task.Delay(50);
            }
            return best;
        }

        window.Confirm = _ => true;
        var probe = new ProbeSink();
        _engine.Hub.AddSink(probe);
        await _engine.NavigateAsync(Page("30B050"), TimeSpan.FromSeconds(10));
        var webTab = window.Tabs.Active!.Id;
        Expect(await WaitUntilAsync(() => probe.Row.Contains("other")), $"the web tab is on air first (saw {probe.Row})");
        Expect(await MaxPeakAsync(600) < -60, "and the output is silent");

        // ---- sound: heard while the deck is on air, silent while it is not, heard again when it returns ----
        Expect(await window.AddPowerPointTabAsync(audioDeck), "a deck with a sound can be added");
        var deckTab = window.Tabs.Active!.Id;
        var ppt = (_engine.Active as DocumentTab)?.PowerPoint;
        int pid = ppt?.ProcessId ?? 0;
        Expect(ppt is { ProcessId: > 0 }, $"the deck names the PowerPoint process that plays it (pid {ppt?.ProcessId})");
        Expect(_engine.AudioProcessIds.Contains(ppt?.ProcessId ?? -1), "and the audio capture is asked to listen to it");
        double heard = -120;
        for (int i = 0; i < 6 && heard < -40; i++) heard = Math.Max(heard, await MaxPeakAsync(800));
        Expect(heard is > -30 and < 0, $"the tone of the deck is heard at the output ({heard:0.0} dBFS)");

        await window.SelectTabAsync(webTab);
        await Task.Delay(2200); // the meter falls 30 dB a second: let the last of the tone pass
        double away = await MaxPeakAsync(800);
        Expect(away < -60, $"the deck is silent while the web tab is on air ({away:0.0})");
        await window.SelectTabAsync(deckTab);
        double back = -120;
        for (int i = 0; i < 4 && back < -40; i++) back = Math.Max(back, await MaxPeakAsync(600));
        Expect(back is > -30 and < 0, $"and heard again when it is back on air ({back:0.0})");

        window.SetGate(Stagecast.Core.Output.GateMode.Freeze);
        await Task.Delay(400);
        Expect(audio.IsSilenced && Peak() < -60, $"Freeze silences a deck's sound too ({Peak():0.0})");
        window.SetGate(Stagecast.Core.Output.GateMode.Live);

        // ---- video: a clip that is red for two seconds and then green plays by itself, and the output follows it ----
        Expect(await window.AddPowerPointTabAsync(videoDeck), "a deck with a video can be added");
        bool sawRed = await WaitUntilAsync(() => probe.Row.Split(' ') is { Length: 10 } c && c[5] == "red", 8000);
        bool sawGreen = await WaitUntilAsync(() => probe.Row.Split(' ') is { Length: 10 } c && c[5] == "green", 8000);
        Expect(sawRed && sawGreen, $"the video plays by itself: red, then green, at the output (red {sawRed}, green {sawGreen}, now {probe.Row})");

        // ---- closing ----
        await window.SelectTabAsync(webTab);
        foreach (var info in window.Tabs.Items.Where(t => t.Kind == TabKind.PowerPoint).ToList()) await window.CloseTabAsync(info.Id);
        await Task.Delay(1500);
        if (!_pptWasRunning) Expect(await WaitUntilAsync(() => Process.GetProcessesByName("POWERPNT").Length == 0, 15000), "PowerPoint, started by the tabs, is closed with them");
        Expect(pid > 0 && !_engine.AudioProcessIds.Contains(pid), "and its sound is no longer captured");

        _engine.Hub.RemoveSink(probe);
        _logger.LogInformation(_failures.Count == 0 ? "P1 selftest ok" : "P1 selftest FAILED: {Failures}", string.Join("; ", _failures));
    }
}
