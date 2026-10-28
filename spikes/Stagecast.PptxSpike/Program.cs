using System.Diagnostics;
using System.Runtime.InteropServices;
using static Win32;

// P0 spike (COM part): PowerPoint driven by late-bound COM, nothing from Office is referenced.
// Usage: Stagecast.PptxSpike make <outDir>          writes test decks (16:9 and 4:3, hidden slides at start, middle and end)
//        Stagecast.PptxSpike probe <deck.pptx> <outDir>   opens a temporary copy and reports what COM can do
args = args.Where(a => a != "--").ToArray();
SetProcessDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2, so window sizes are real pixels
if (args.Length >= 2 && args[0] == "make") Make(args[1]);
else if (args.Length >= 3 && args[0] == "probe") Probe(args[1], args[2]);
else if (args.Length >= 2 && args[0] == "audio") AudioSpike.Run(args[1]);
else if (args.Length >= 3 && args[0] == "grab") Grab(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 2);
else Console.WriteLine("usage: make <outDir> | probe <deck.pptx> <outDir> | grab <deck.pptx> <outDir>");

static int Rgb(int r, int g, int b) => r | (g << 8) | (b << 16);

// PowerPoint is one shared instance: when the user already runs it, this program borrows it and never calls Quit.
static dynamic NewPowerPoint()
{
    State.PowerPointWasRunning = Process.GetProcessesByName("POWERPNT").Length > 0;
    Console.WriteLine(State.PowerPointWasRunning ? "PowerPoint already running: borrowed, will not Quit" : "PowerPoint not running: started here, will Quit");
    Type type = Type.GetTypeFromProgID("PowerPoint.Application")!;
    return Activator.CreateInstance(type)!;
}

static void Make(string outDir)
{
    Directory.CreateDirectory(outDir);
    dynamic app = NewPowerPoint();
    try
    {
        foreach ((string name, float width) in new[] { ("deck-16x9", 960f), ("deck-4x3", 720f) })
        {
            dynamic pres = app.Presentations.Add(0);
            pres.PageSetup.SlideWidth = width;
            pres.PageSetup.SlideHeight = 540f;
            // Eight slides; 1, 4 and 8 are hidden. Slide 3 has a click-triggered fade and an "after previous" one.
            int[][] colours = { new[] { 200, 40, 40 }, new[] { 40, 160, 60 }, new[] { 40, 80, 200 }, new[] { 220, 180, 30 }, new[] { 160, 60, 180 }, new[] { 30, 160, 170 }, new[] { 240, 120, 30 }, new[] { 90, 90, 90 } };
            for (int i = 1; i <= 8; i++)
            {
                dynamic slide = pres.Slides.AddSlide(i, pres.SlideMaster.CustomLayouts[7]);
                slide.FollowMasterBackground = 0;
                slide.Background.Fill.ForeColor.RGB = Rgb(colours[i - 1][0], colours[i - 1][1], colours[i - 1][2]);
                dynamic box = slide.Shapes.AddTextbox(1, 40f, 150f, width - 80f, 200f);
                box.TextFrame.TextRange.Text = $"SLIDE {i}";
                box.TextFrame.TextRange.Font.Size = 96;
                box.TextFrame.TextRange.Font.Color.RGB = Rgb(255, 255, 255);
                if (i == 3)
                {
                    slide.TimeLine.MainSequence.AddEffect(box, 10, 0, 1); // fade, on click
                    dynamic second = slide.Shapes.AddTextbox(1, 40f, 380f, width - 80f, 80f);
                    second.TextFrame.TextRange.Text = "after previous";
                    second.TextFrame.TextRange.Font.Size = 40;
                    second.TextFrame.TextRange.Font.Color.RGB = Rgb(255, 255, 255);
                    slide.TimeLine.MainSequence.AddEffect(second, 10, 0, 3); // fade, after previous
                }
                if (i is 1 or 4 or 8) slide.SlideShowTransition.Hidden = -1;
            }
            string path = Path.Combine(outDir, name + ".pptx");
            pres.SaveAs(path, 24);
            pres.Close();
            Console.WriteLine($"wrote {path}");
        }
    }
    finally
    {
        if (!State.PowerPointWasRunning) QuitAndWait(app);
    }
}

static void Probe(string deck, string outDir)
{
    Directory.CreateDirectory(outDir);
    string temp = Path.Combine(Path.GetTempPath(), "pptxspike-" + Guid.NewGuid().ToString("N") + ".pptx");
    File.Copy(deck, temp);
    var clock = Stopwatch.StartNew();
    dynamic app = NewPowerPoint();
    dynamic? opened = null;
    Console.WriteLine($"PowerPoint {Retry(() => (string)app.Version)} started in {clock.ElapsedMilliseconds} ms; build {app.Build}");
    try
    {
        app.AutomationSecurity = 3; // msoAutomationSecurityForceDisable
        clock.Restart();
        dynamic pres = app.Presentations.Open(temp, -1, 0, 0); // read only, untitled, no window
        opened = pres;
        Console.WriteLine($"open {clock.ElapsedMilliseconds} ms: {pres.Slides.Count} slides, {pres.PageSetup.SlideWidth}x{pres.PageSetup.SlideHeight} pt");
        var hidden = new List<int>();
        for (int i = 1; i <= pres.Slides.Count; i++) if (pres.Slides[i].SlideShowTransition.Hidden != 0) hidden.Add(i);
        Console.WriteLine("hidden slides: " + string.Join(",", hidden));

        // Thumbnails before the show, hidden ones included.
        clock.Restart();
        var failed = new List<int>();
        for (int i = 1; i <= pres.Slides.Count; i++)
        {
            try { pres.Slides[i].Export(Path.Combine(outDir, $"thumb-{i}.png"), "PNG", 320, 180); }
            catch (Exception ex) { failed.Add(i); Console.WriteLine($"export slide {i} failed: {ex.Message}"); }
        }
        Console.WriteLine($"exported {pres.Slides.Count} thumbnails in {clock.ElapsedMilliseconds} ms, failed: [{string.Join(",", failed)}]");

        dynamic settings = pres.SlideShowSettings;
        settings.ShowType = 2;          // ppShowTypeWindow
        settings.ShowPresenterView = 0;
        settings.AdvanceMode = 1;       // ppSlideShowManualAdvance
        settings.ShowWithAnimation = -1;
        settings.LoopUntilStopped = 0;
        clock.Restart();
        dynamic window = settings.Run();
        Console.WriteLine($"show started in {clock.ElapsedMilliseconds} ms; windows: {app.SlideShowWindows.Count}");
        Thread.Sleep(1500);
        IntPtr hwnd = FindShowWindow();
        Console.WriteLine($"HWND 0x{hwnd.ToInt64():X}, left/top/width/height (pt) {window.Left}/{window.Top}/{window.Width}/{window.Height}, foreground={GetForegroundWindow() == hwnd}");
        Console.WriteLine("window " + Describe(hwnd));
        Console.WriteLine($"start position: slide {window.View.CurrentShowPosition} (first slide hidden: {hidden.Contains(1)})");

        // Set the window to the size of a fitted slide (1920x1080 target canvas, 1440x1080 px wanted for 4:3).
        float slideW = pres.PageSetup.SlideWidth, slideH = pres.PageSetup.SlideHeight;
        double dpi = GetDpiForWindow(hwnd);
        double ptPerPx = 72.0 / dpi;
        double fit = Math.Min(1920.0 / slideW, 1080.0 / slideH);
        int wantW = (int)Math.Round(slideW * fit), wantH = (int)Math.Round(slideH * fit);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("slide show window not found");
        Retry(() => { window.Width = (float)(wantW * ptPerPx); return 0; });
        Retry(() => { window.Height = (float)(wantH * ptPerPx); return 0; });
        Thread.Sleep(800);
        Console.WriteLine($"asked for {wantW}x{wantH} px at {dpi} dpi -> " + Describe(hwnd));

        // Walk forward with Next and log the positions, then backwards with Previous.
        var forward = new List<string>();
        for (int i = 0; i < 12; i++)
        {
            int before = SafePosition(app, window);
            try { Retry(() => { window.View.Next(); return 0; }); } catch (Exception ex) { forward.Add("ex:" + ex.GetType().Name); break; }
            Thread.Sleep(400);
            int windows;
            try { windows = Retry(() => (int)app.SlideShowWindows.Count); }
            catch (COMException ex) { forward.Add($"{before}->busy(0x{(uint)ex.HResult:X})"); Thread.Sleep(1500); continue; }
            if (windows == 0) { forward.Add($"{before}->END"); break; }
            int after = SafePosition(app, window);
            forward.Add($"{before}->{after}");
            if (after > pres.Slides.Count) { forward.Add("(end screen: stop here, one more Next closes the show)"); break; }
        }
        Console.WriteLine("Next walk: " + string.Join(" ", forward));

        foreach (int target in new[] { 1, 4, 8, 5 })
        {
            try
            {
                Retry(() => { window.View.GotoSlide(target); return 0; });
                Thread.Sleep(400);
                Console.WriteLine($"GotoSlide({target}) -> position {SafePosition(app, window)} (hidden: {hidden.Contains(target)})");
            }
            catch (Exception ex) { Console.WriteLine($"GotoSlide({target}) failed: {ex.Message}"); }
        }
        try { Retry(() => { window.View.First(); return 0; }); Thread.Sleep(300); Console.WriteLine($"First() -> {SafePosition(app, window)}"); } catch (Exception ex) { Console.WriteLine("First failed: " + ex.Message); }
        try { Retry(() => { window.View.Last(); return 0; }); Thread.Sleep(300); Console.WriteLine($"Last() -> {SafePosition(app, window)}"); } catch (Exception ex) { Console.WriteLine("Last failed: " + ex.Message); }

        // Export while a show is running.
        clock.Restart();
        try
        {
            for (int i = 1; i <= pres.Slides.Count; i++) pres.Slides[i].Export(Path.Combine(outDir, $"during-{i}.png"), "PNG", 320, 180);
            Console.WriteLine($"export during the show: ok in {clock.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) { Console.WriteLine("export during the show failed: " + ex.Message); }

        try { Retry(() => { window.View.Exit(); return 0; }); } catch { }
    }
    finally
    {
        try { if (opened is not null) opened.Close(); } catch { }
        if (!State.PowerPointWasRunning) QuitAndWait(app);
        File.Delete(temp);
    }
}

static void Grab(string deck, string outDir, int showType)
{
    Directory.CreateDirectory(outDir);
    string temp = Path.Combine(Path.GetTempPath(), "pptxspike-" + Guid.NewGuid().ToString("N") + ".pptx");
    File.Copy(deck, temp);
    dynamic app = NewPowerPoint();
    dynamic? opened = null;
    WindowGrab? grab = null;
    try
    {
        app.AutomationSecurity = 3;
        dynamic pres = app.Presentations.Open(temp, -1, 0, 0);
        opened = pres;
        dynamic settings = pres.SlideShowSettings;
        settings.ShowType = showType; settings.ShowPresenterView = 0; settings.AdvanceMode = 1; settings.ShowWithAnimation = -1;
        dynamic window = settings.Run();
        Thread.Sleep(1500);
        IntPtr hwnd = FindShowWindow();
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("slide show window not found");
        GetWindowRect(hwnd, out RECT w0); GetClientRect(hwnd, out RECT c0);
        Console.WriteLine($"show type {showType}: " + Describe(hwnd));
        if (showType == 2) {
        // Size the window so the client area is exactly 1440x1080 (a fitted 4:3 slide on a 1920x1080 canvas).
        int chromeW = (w0.Right - w0.Left) - (c0.Right - c0.Left), chromeH = (w0.Bottom - w0.Top) - (c0.Bottom - c0.Top);
        double ptPerPx = 72.0 / GetDpiForWindow(hwnd);
        Retry(() => { window.Width = (float)((1440 + chromeW) * ptPerPx); return 0; });
        Retry(() => { window.Height = (float)((1080 + chromeH) * ptPerPx); return 0; });
        Thread.Sleep(800);
        Console.WriteLine("sized: " + Describe(hwnd) + $" (chrome {chromeW}x{chromeH})");
        }

        // Optional: park the show window somewhere else before capturing (PPTXSPIKE_MOVE="x,y"), to see if it is still drawn.
        if (Environment.GetEnvironmentVariable("PPTXSPIKE_MOVE") is { Length: > 0 } move)
        {
            string[] xy = move.Split(',');
            SetWindowPos(hwnd, IntPtr.Zero, int.Parse(xy[0]), int.Parse(xy[1]), 0, 0, 0x0001 | 0x0004); // SWP_NOSIZE | SWP_NOZORDER
            Thread.Sleep(1000);
            Console.WriteLine("moved: " + Describe(hwnd));
        }
        grab = new WindowGrab(hwnd);
        Thread.Sleep(1500);
        Console.WriteLine($"frames in 1.5 s of a still slide: {grab.Frames}");
        grab.SavePng(Path.Combine(outDir, "grab-slide2.png"));
        Console.WriteLine("slide 2 probe: " + grab.Probe((5, 5), (960, 540), (1900, 1070)));

        // A mouse move over the window must not leave a cursor or toolbar in the frame: move it into the slide and look.
        SetCursorPos(w0.Left + 400, w0.Top + 400);
        Thread.Sleep(1200);
        grab.SavePng(Path.Combine(outDir, "grab-mouse.png"));

        // Cover the show completely with another window and check the capture still follows the slides.
        System.Windows.Forms.Form? cover = null;
        if (Environment.GetEnvironmentVariable("PPTXSPIKE_COVER") == "1")
        {
            var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                cover = new System.Windows.Forms.Form { FormBorderStyle = System.Windows.Forms.FormBorderStyle.None, TopMost = true, BackColor = System.Drawing.Color.Magenta, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Bounds = new System.Drawing.Rectangle(0, 0, 1920, 1080) };
                cover.Shown += (_, _) => ready.Set();
                System.Windows.Forms.Application.Run(cover);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait(5000);
            Thread.Sleep(1000);
            Console.WriteLine($"covered by a topmost magenta window: foreground is the show={GetForegroundWindow() == hwnd}");
        }
        int before = grab.Frames;
        for (int i = 0; i < 3; i++) { Retry(() => { window.View.Next(); return 0; }); Thread.Sleep(200); }
        Thread.Sleep(1500);
        Console.WriteLine($"after 3 Next on the animated slide: position {SafePosition(app, window)}, {grab.Frames - before} new frames");
        grab.SavePng(Path.Combine(outDir, "grab-slide3.png"));
        Retry(() => { window.View.Next(); return 0; });
        Thread.Sleep(1200);
        Console.WriteLine($"position {SafePosition(app, window)}");
        grab.SavePng(Path.Combine(outDir, "grab-slide5.png"));
        Console.WriteLine("slide 5 probe: " + grab.Probe((5, 5), (960, 540), (1900, 1070)));
        Retry(() => { window.View.Exit(); return 0; });
        try { cover?.Invoke(() => cover.Close()); } catch { }
    }
    finally
    {
        grab?.Dispose();
        try { if (opened is not null) opened.Close(); } catch { }
        if (!State.PowerPointWasRunning) QuitAndWait(app);
        File.Delete(temp);
    }
}

static void QuitAndWait(dynamic app)
{
    for (int i = 0; i < 10; i++)
    {
        try { app.Quit(); break; } catch (COMException) { Thread.Sleep(500); }
    }
    for (int i = 0; i < 50 && Process.GetProcessesByName("POWERPNT").Length > 0; i++) Thread.Sleep(100);
    // Only reached for an instance this program started itself, never for the user's own PowerPoint.
    foreach (Process left in Process.GetProcessesByName("POWERPNT")) { Console.WriteLine("PowerPoint did not quit: killed"); left.Kill(); }
}

// SlideShowWindow.HWND is refused over COM here. A windowed show is a framed PPTFrameClass window titled "PowerPoint Slide Show".
static IntPtr FindShowWindow()
{
    var pids = Process.GetProcessesByName("POWERPNT").Select(p => (uint)p.Id).ToHashSet();
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, _) =>
    {
        GetWindowThreadProcessId(h, out uint pid);
        if (!pids.Contains(pid) || !IsWindowVisible(h)) return true;
        var title = new System.Text.StringBuilder(256);
        GetWindowText(h, title, 256);
        var name = new System.Text.StringBuilder(64);
        GetClassName(h, name, 64);
        Console.WriteLine($"  top window 0x{h.ToInt64():X} class {name} title '{title}' " + Describe(h));
        if (title.ToString().StartsWith("PowerPoint Slide Show")) found = h;
        EnumChildWindows(h, (c, _) =>
        {
            var cn = new System.Text.StringBuilder(64);
            GetClassName(c, cn, 64);
            Console.WriteLine($"    child 0x{c.ToInt64():X} class {cn} " + Describe(c));
            return true;
        }, IntPtr.Zero);
        return true;
    }, IntPtr.Zero);
    return found;
}

// PowerPoint answers "busy" for a moment after it changes a window.
static T Retry<T>(Func<T> call)
{
    for (int i = 0; ; i++)
    {
        try { return call(); }
        catch (COMException ex) when ((uint)ex.HResult == 0x8001010A && i < 40) { Thread.Sleep(100); }
    }
}

static int SafePosition(dynamic app, dynamic window)
{
    try { return Retry(() => (int)window.View.CurrentShowPosition); } catch { return -1; }
}

static string Describe(IntPtr hwnd)
{
    GetWindowRect(hwnd, out RECT w);
    GetClientRect(hwnd, out RECT c);
    return $"window {w.Right - w.Left}x{w.Bottom - w.Top} px at ({w.Left},{w.Top}), client {c.Right - c.Left}x{c.Bottom - c.Top} px, dpi {GetDpiForWindow(hwnd)}";
}

static class State { public static bool PowerPointWasRunning; }

[StructLayout(LayoutKind.Sequential)]
struct RECT { public int Left, Top, Right, Bottom; }

static class Win32
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hwnd, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
}
