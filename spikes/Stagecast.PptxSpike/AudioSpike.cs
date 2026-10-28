using System.Diagnostics;
using System.Runtime.InteropServices;
using Stagecast.Rendering.Audio;
using static Win32;

/// <summary>P1 spike: can the sound of a slide show be captured from the PowerPoint process, and does an embedded MP4 play in a captured show?</summary>
static class AudioSpike
{
    public static void Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        string wav = Path.Combine(outDir, "tone.wav");
        WriteTone(wav, 440, 8);
        string deckA = Path.Combine(outDir, "deck-audio.pptx");
        string deckV = Path.Combine(outDir, "deck-video-source.pptx");
        string mp4 = Path.Combine(outDir, "clip.mp4");
        string deckB = Path.Combine(outDir, "deck-video.pptx");

        dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application")!)!;
        bool wasRunning = Process.GetProcessesByName("POWERPNT").Length > 1; // our own start counts as one
        try
        {
            // 1. A deck whose first slide plays the tone by itself.
            MakeDeck(app, deckA, wav, null);
            // 2. The same deck exported by PowerPoint as an MP4 (video with sound).
            dynamic source = app.Presentations.Open(deckA, -1, 0, 0);
            Console.WriteLine("exporting MP4...");
            var clock = Stopwatch.StartNew();
            File.Delete(mp4);
            source.CreateVideo(mp4, -1, 6, 480, 30, 60);
            while ((int)source.CreateVideoStatus is 1 or 2 && clock.Elapsed < TimeSpan.FromSeconds(90)) Thread.Sleep(500);
            Console.WriteLine($"MP4 export status {(int)source.CreateVideoStatus} after {clock.Elapsed.TotalSeconds:F1} s, size {(File.Exists(mp4) ? new FileInfo(mp4).Length : 0)} bytes");
            source.Close();
            if (!File.Exists(mp4)) return;
            // 3. A deck with that MP4 on its first slide, playing by itself.
            MakeDeck(app, deckB, null, mp4);
        }
        finally
        {
            try { app.Quit(); } catch { }
            for (int i = 0; i < 100 && Process.GetProcessesByName("POWERPNT").Length > 0; i++) Thread.Sleep(100);
            foreach (var left in Process.GetProcessesByName("POWERPNT")) left.Kill();
        }

        Console.WriteLine("=== audio from a WAV in a slide");
        Play(deckA, Path.Combine(outDir, "frames-audio"));
        Console.WriteLine("=== audio and picture from an MP4 in a slide");
        Play(deckB, Path.Combine(outDir, "frames-video"));
    }

    static void MakeDeck(dynamic app, string path, string? wav, string? mp4)
    {
        dynamic pres = app.Presentations.Add(0);
        pres.PageSetup.SlideWidth = 960f;
        pres.PageSetup.SlideHeight = 540f;
        dynamic slide = pres.Slides.AddSlide(1, pres.SlideMaster.CustomLayouts[7]);
        slide.FollowMasterBackground = 0;
        slide.Background.Fill.ForeColor.RGB = 40 | (80 << 8) | (200 << 16);
        slide.SlideShowTransition.AdvanceOnTime = 0;
        dynamic? media = null;
        if (wav is not null) media = slide.Shapes.AddMediaObject2(wav, 0, -1, 40f, 40f, 64f, 64f);
        if (mp4 is not null) media = slide.Shapes.AddMediaObject2(mp4, 0, -1, 80f, 60f, 800f, 420f);
        if (media is not null)
        {
            // The media plays by itself once the slide appears: a "play" effect (83, msoAnimEffectMediaPlay) that starts after the previous one (3).
            slide.TimeLine.MainSequence.AddEffect(media, 83, 0, 3);
        }
        File.Delete(path);
        pres.SaveAs(path, 24);
        pres.Close();
    }

    static void Play(string deck, string frames)
    {
        Directory.CreateDirectory(frames);
        bool wasRunning = Process.GetProcessesByName("POWERPNT").Length > 0;
        dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application")!)!;
        dynamic? pres = null;
        WindowGrab? grab = null;
        var captures = new List<(int Pid, ProcessLoopbackCapture Capture, float[] Peak)>();
        try
        {
            pres = app.Presentations.Open(deck, -1, 0, 0);
            dynamic settings = pres.SlideShowSettings;
            settings.ShowType = 1; settings.ShowPresenterView = 0; settings.AdvanceMode = 1; settings.ShowWithAnimation = -1;

            // Capture the sound of every PowerPoint process before the show starts, so the first sound is not missed.
            foreach (var p in Process.GetProcessesByName("POWERPNT"))
            {
                var peak = new float[1];
                try
                {
                    var capture = new ProcessLoopbackCapture(p.Id, (samples, n) =>
                    {
                        float max = 0;
                        for (int i = 0; i < n * 2; i++) max = Math.Max(max, Math.Abs(samples[i]));
                        if (max > peak[0]) peak[0] = max;
                    });
                    captures.Add((p.Id, capture, peak));
                    Console.WriteLine($"loopback capture of POWERPNT pid {p.Id} started");
                }
                catch (Exception ex) { Console.WriteLine($"loopback capture of pid {p.Id} failed: {ex.Message}"); }
            }

            dynamic window = settings.Run();
            Thread.Sleep(1200);
            IntPtr hwnd = IntPtr.Zero;
            var pids = Process.GetProcessesByName("POWERPNT").Select(p => (uint)p.Id).ToHashSet();
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (!pids.Contains(pid) || !IsWindowVisible(h)) return true;
                var title = new System.Text.StringBuilder(256);
                GetWindowText(h, title, 256);
                if (title.ToString().StartsWith("PowerPoint Slide Show")) hwnd = h;
                return true;
            }, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) { Console.WriteLine("show window not found"); return; }
            GetWindowRect(hwnd, out RECT rect);
            SetWindowPos(hwnd, IntPtr.Zero, GetSystemMetrics(76) - (rect.Right - rect.Left) - 400, rect.Top, 0, 0, 0x0001 | 0x0004 | 0x0010);
            Thread.Sleep(500);
            grab = new WindowGrab(hwnd);

            var clock = Stopwatch.StartNew();
            for (int second = 0; second < 8; second++)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"t={clock.Elapsed.TotalSeconds:F1}s frames={grab.Frames} peaks=[{string.Join(", ", captures.Select(c => $"{c.Pid}:{c.Peak[0]:F3}"))}] centre={grab.Probe((480, 270), (480, 100))}");
                foreach (var c in captures) c.Peak[0] = 0;
                if (second is 1 or 5) grab.SavePng(Path.Combine(frames, $"t{second}.png"));
            }

            try { window.View.Exit(); } catch { }
        }
        finally
        {
            grab?.Dispose();
            foreach (var c in captures) c.Capture.Dispose();
            try { pres?.Close(); } catch { }
            if (!wasRunning)
            {
                try { app.Quit(); } catch { }
                for (int i = 0; i < 100 && Process.GetProcessesByName("POWERPNT").Length > 0; i++) Thread.Sleep(100);
                foreach (var left in Process.GetProcessesByName("POWERPNT")) left.Kill();
            }
        }
    }

    static void WriteTone(string path, double hz, int seconds)
    {
        const int rate = 44100;
        int samples = rate * seconds;
        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(samples * 2);
        for (int i = 0; i < samples; i++) w.Write((short)(Math.Sin(2 * Math.PI * hz * i / rate) * 12000));
    }
}
