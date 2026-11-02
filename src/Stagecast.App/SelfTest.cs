using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Stagecast.Core.Audio;
using Stagecast.Core.Output;
using Stagecast.Rendering.Audio;
using Stagecast.Rendering.Ndi;
using Stagecast.Rendering.Omt;
using Stagecast.Core.Configuration;
using Stagecast.Core.Display;
using Stagecast.Core.Frames;
using Stagecast.Core.Tabs;
using Stagecast.Core.Presets;
using Stagecast.Core.Sessions;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Rendering;
using Stagecast.Rendering;

namespace Stagecast.App;

/// <summary>
/// Scripted check of the render engine, run with <c>--selftest a2</c>: steady 60 fps, switching to 30 fps while running, changing
/// the resolution while running, and a page that never changes (the hub must keep the output rate by repeating the last frame).
/// </summary>
internal sealed partial class SelfTest
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
        if (name == "d5") { await RunPdfAsync(window); return; }
        if (name == "p2") { await RunPowerPointAsync(window); return; }
        if (name == "shot") { await RunShotAsync(window); return; }
        if (name == "e1") { await RunPatternAsync(window); return; }
        if (name == "p5") { await RunPowerPointRobustAsync(window); return; }
        if (name == "p4") { await RunPowerPointPreviewAsync(window); return; }
        if (name == "p3") { await RunRealDeckAsync(window); return; }
        if (name == "p1") { await RunPowerPointMediaAsync(window); return; }
        if (name == "d1a") { await RunDocumentTabsAsync(window); return; }
        if (name == "a2b") { await RunControlsAsync(window); return; }
        if (name is "a3c-set" or "a3c-check") { await RunLoginsAsync(window, name == "a3c-set"); return; }
        if (name == "a8") { await RunRecoveryAsync(window); return; }
        if (name == "soak") { await RunSoakAsync(window); return; }
        if (name == "a7") { await RunOmtAsync(window); return; }
        if (name == "a6") { await RunNdiAsync(window); return; }
        if (name == "a5") { await RunGateAndAudioAsync(window); return; }
        if (name == "a4") { await RunScreenOutputAsync(window); return; }
        if (name == "a3b") { await RunPreviewTakeAsync(window); return; }
        if (name == "a3") { await RunTabsAsync(window); return; }
        if (name == "a2c") { await RunChangesAndPresetsAsync(window); return; }
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

    private const int LoginPort = 47831;
    private static readonly string LoginSite = $"http://localhost:{LoginPort}";

    /// <summary>A tiny local site: /dologin and /dologin2 set a session cookie the way a login does, every other path is a page.</summary>
    private static System.Net.HttpListener StartLoginSite()
    {
        var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"{LoginSite}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    string path = context.Request.Url!.AbsolutePath;
                    if (path == "/dologin") context.Response.AddHeader("Set-Cookie", "sess=one; Path=/; HttpOnly; Max-Age=86400");
                    if (path == "/dologin2") context.Response.AddHeader("Set-Cookie", "sess2=two; Path=/; HttpOnly; Max-Age=86400");
                    byte[] body = System.Text.Encoding.UTF8.GetBytes($"<html><body style='background:#223'>page {path}</body></html>");
                    context.Response.ContentType = "text/html";
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
                catch (Exception) when (!listener.IsListening)
                {
                    return;
                }
                catch (Exception)
                {
                    // one bad request must not stop the site
                }
            }
        });
        return listener;
    }

    private async Task<string> CookieNamesAsync(Rendering.PageTab tab)
        => string.Join(",", (await _engine.CookiesAsync(tab, LoginSite)).Select(c => c.Name).OrderBy(n => n));

    /// <summary>
    /// Logins: a cookie set by a site stays in the profile; a tab with its own profile does not share it; the session guess; the
    /// logins survive a restart (second launch); clearing removes them everywhere; profile folders do not pile up.
    /// </summary>
    private async Task RunLoginsAsync(MainWindow window, bool first)
    {
        using var site = StartLoginSite();
        string profiles = window.ProfilesDirectory;
        if (first)
        {
            Directory.CreateDirectory(Path.Combine(profiles, "tab-" + new string('f', 32))); // an orphan left by "an earlier session"

            var shared = window.Tabs.Items[0];
            await _engine.NavigateAsync($"{LoginSite}/dologin", TimeSpan.FromSeconds(10));
            await Task.Delay(500);
            Expect(await CookieNamesAsync(_engine.ActivePage) == "sess", "the shared tab holds the cookie its login set");

            Expect(await window.AddTabAsync($"{LoginSite}/page"), "a second tab can be added");
            var second = window.Tabs.Active!;
            window.Confirm = _ => true;
            Expect(await window.SetSeparateProfileAsync(second.Id, true), "the second tab can get its own profile");
            await Task.Delay(1500);
            var secondTab = _engine.PageTabById(second.Id)!;
            Expect(secondTab.ProfileId is { Length: 32 } && _engine.Active!.Id == second.Id, "the second tab runs on its own profile and is still the active one");
            Expect(await CookieNamesAsync(secondTab) == "", "a separate profile does not see the shared login");

            await _engine.NavigateAsync($"{LoginSite}/dologin2", TimeSpan.FromSeconds(10));
            Expect(await CookieNamesAsync(secondTab) == "sess2", "the separate tab holds only its own login");
            Expect(await CookieNamesAsync(_engine.PageTabById(shared.Id)!) == "sess", "the shared tab does not see the separate login");

            await _engine.NavigateAsync($"{LoginSite}/page", TimeSpan.FromSeconds(10));
            await window.CheckSessionsAsync();
            Expect(window.Sessions[shared.Id] == SessionState.LoggedIn, "the shared tab (a page with a session cookie) is guessed as logged in");
            Expect(window.Sessions[second.Id] == SessionState.LoggedIn, "the separate tab is guessed as logged in");
            await _engine.NavigateAsync(_engine.PageTabById(shared.Id)!, $"{LoginSite}/login", TimeSpan.FromSeconds(10));
            await window.CheckSessionsAsync();
            Expect(window.Sessions[shared.Id] == SessionState.NeedsLogin, "a login page is guessed as needing login");

            await Task.Delay(1800); // let the tabs reach config.json
            var saved = window.ReadConfigFromDisk();
            Expect(saved.Tabs.Count == 0 && Directory.Exists(Path.Combine(profiles, "tab-" + secondTab.ProfileId)), "the settings file holds no tab, and the separate profile is on disk");
            _logger.LogInformation(_failures.Count == 0 ? "A3c set selftest ok" : "A3c set selftest FAILED: {Failures}", string.Join("; ", _failures));
            return;
        }

        // Second launch: everything was closed and opened again with the same data folder.
        Expect(window.Tabs.Items.Count == 1, "a start has no tab of the last run (only the first one the test adds)");
        Expect(Directory.Exists(Path.Combine(profiles, "tab-" + new string('f', 32))), "profile folders are kept at startup, also one no tab refers to");
        string? kept = Directory.GetDirectories(profiles, "tab-*").Select(Path.GetFileName).Select(n => n![4..]).FirstOrDefault(id => id != new string('f', 32));
        Expect(kept is { Length: 32 }, "the separate profile of the last run is still on disk");
        Expect(await window.AddTabAsync($"{LoginSite}/page", kept) && window.Tabs.Active!.ProfileId == kept, "a tab can be opened on that profile again");
        var tab0 = _engine.PageTabById(window.Tabs.Items[0].Id)!;
        var tab1 = _engine.PageTabById(window.Tabs.Items[1].Id)!;
        await Task.Delay(1000);
        Expect(await CookieNamesAsync(tab0) == "sess", "the shared login survived the restart");
        Expect(await CookieNamesAsync(tab1) == "sess2", "the separate login survived the restart");

        window.Confirm = _ => false;
        Expect(!await window.ClearLoginDataAsync() && await CookieNamesAsync(tab0) == "sess", "declining the confirmation keeps the logins");
        window.Confirm = _ => true;
        Expect(await window.ClearLoginDataAsync(), "Hapus data login runs after confirmation");
        await Task.Delay(800);
        Expect(await CookieNamesAsync(tab0) == "" && await CookieNamesAsync(tab1) == "", "no login is left in any profile");

        string folder = Path.Combine(profiles, "tab-" + window.Tabs.Items[1].ProfileId);
        Expect(Directory.Exists(folder), "the separate profile folder exists while its tab does");
        Expect(await window.SetSeparateProfileAsync(window.Tabs.Items[1].Id, false), "the tab can go back to the shared profile");
        for (int i = 0; i < 40 && Directory.Exists(folder); i++) await Task.Delay(500);
        Expect(!Directory.Exists(folder), "the profile folder of a tab that went back to the shared profile is deleted");
        Expect(_engine.Tabs.Count == 2 && _engine.PageTabById(window.Tabs.Items[1].Id)?.ProfileId is null, "the engine and the list agree afterwards");
        _logger.LogInformation(_failures.Count == 0 ? "A3c check selftest ok" : "A3c check selftest FAILED: {Failures}", string.Join("; ", _failures));
    }

    /// <summary>Counts what the output would show: frames whose centre is not one of the expected colours (black = a flash).</summary>
    private sealed class ColourSink : IFrameSink
    {
        public string Name => "colour-check";

        public long Frames, Black;
        public volatile string Seen = "";
        private long _lastSequence;

        public void Deliver(Frame frame, bool repeated)
        {
            int offset = frame.Height / 2 * frame.Stride + frame.Width / 2 * 4;
            byte b = frame.Pixels[offset], g = frame.Pixels[offset + 1], r = frame.Pixels[offset + 2];
            Interlocked.Increment(ref Frames);
            if (r < 12 && g < 12 && b < 12) Interlocked.Increment(ref Black);
            Seen = $"{r:X2}{g:X2}{b:X2}";
            _lastSequence = frame.Sequence;
        }
    }

    // STAGECAST_TEST_ANIM=1 adds a tiny flickering corner so the page keeps producing frames (to tell "not drawn" from "not re-sent").
    private static string Page(string rgb) => Environment.GetEnvironmentVariable("STAGECAST_TEST_ANIM") == "1"
        ? $"data:text/html,<style>@keyframes b%7Bfrom%7Bopacity:.5%7Dto%7Bopacity:1%7D%7D</style><body style='margin:0;background:%23{rgb}'><div style='position:fixed;right:0;bottom:0;width:6px;height:6px;background:%23fff;animation:b .1s infinite alternate'></div></body>"
        : $"data:text/html,<body style='margin:0;background:%23{rgb}'></body>";

    /// <summary>Five tabs on the stage one after another: no black flashes, the right tab each time, names, order, closing, limit, saved file.</summary>
    private async Task RunTabsAsync(MainWindow window)
    {
        string[] colours = ["E03030", "3050E0", "30B050", "E0C020", "B030C0"];
        var sink = new ColourSink();
        _engine.Hub.AddSink(sink);
        await Task.Delay(300);

        // The first tab already exists (blank). Give it the first colour and add four more.
        await _engine.NavigateAsync(Page(colours[0]), TimeSpan.FromSeconds(10));
        await Task.Delay(500);
        long blackAfterNavigation = Interlocked.Read(ref sink.Black);
        _logger.LogInformation("A3 black frames up to the first page being loaded (not a tab switch): {Black}", blackAfterNavigation);
        for (int i = 1; i < colours.Length; i++)
        {
            long before = Interlocked.Read(ref sink.Black);
            Expect(await window.AddTabAsync(Page(colours[i])), $"tab {i + 1} can be added");
            await Task.Delay(500);
            _logger.LogInformation("A3 adding tab {N}: {Black} black frames", i + 1, Interlocked.Read(ref sink.Black) - before);
        }
        Expect(window.Tabs.Items.Count == 5 && _engine.Tabs.Count == 5, "five tabs exist");
        Expect(sink.Seen == colours[4], $"the newest tab is on the stage (saw {sink.Seen})");

        long blackWhileAdding = Interlocked.Read(ref sink.Black) - blackAfterNavigation;
        _logger.LogInformation("A3 black frames while the first five tabs were created and shown: {Black}", blackWhileAdding);

        // Switch around a lot and check what the output shows after each one.
        var order = new[] { 0, 1, 2, 3, 4, 0, 3, 1, 4, 2, 0, 4, 1, 3, 2, 0 };
        var wrong = new List<string>();
        var latencies = new List<long>();
        foreach (int index in order)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var trace = new List<string>();
            var selecting = window.SelectTabAsync(window.Tabs.Items[index].Id);
            while (!selecting.IsCompleted || (sink.Seen != colours[index] && clock.ElapsedMilliseconds < 3000))
            {
                string seen = sink.Seen;
                if (trace.Count == 0 || trace[^1].Split('@')[0] != seen) trace.Add($"{seen}@{clock.ElapsedMilliseconds}");
                await Task.Delay(5);
            }
            await selecting;
            if (trace.Count > 1 || sink.Seen != colours[index]) _logger.LogInformation("A3 trace to tab {Index} ({Want}): {Trace}", index, colours[index], string.Join(" ", trace));
            latencies.Add(clock.ElapsedMilliseconds);
            await Task.Delay(250);
            if (sink.Seen != colours[index]) wrong.Add($"{index}:{sink.Seen}");
            Expect(_tabsAudioOk(window), "only the active tab is unmuted");
        }
        latencies.Sort();
        _logger.LogInformation("A3 {Count} switches: output frames {Frames}, black frames {Black}, wrong colours {Wrong}, switch latency median {Median} ms max {Max} ms",
            order.Length, sink.Frames, sink.Black, wrong.Count, latencies[latencies.Count / 2], latencies[^1]);
        Expect(wrong.Count == 0, $"every switch ends on the right tab ({string.Join(", ", wrong)})");
        long blackAtSwitchStart = blackAfterNavigation + blackWhileAdding;
        Expect(sink.Black - blackAtSwitchStart == 0, $"no black frame reached the output while switching ({sink.Black - blackAtSwitchStart} did)");
        Expect(blackWhileAdding == 0, $"no black frame reached the output while tabs were created ({blackWhileAdding} did)");

        // Names.
        var first = window.Tabs.Items[0].Id;
        Expect(window.RenameTab(first, "") == TabNameError.Empty, "an empty tab name is refused");
        Expect(window.RenameTab(first, new string('n', 41)) == TabNameError.TooLong, "a tab name over 40 characters is refused");
        Expect(window.RenameTab(first, "  Kuis Kahoot  ") == TabNameError.None && window.Tabs.Items[0].Name == "Kuis Kahoot", "a valid name is trimmed and kept");

        // Order: moving does not change which tab is on the stage.
        var activeBefore = window.Tabs.Active!.Id;
        Expect(window.MoveTab(activeBefore, +1) && window.Tabs.Active!.Id == activeBefore, "moving a tab keeps the same tab active");
        Expect(_engine.Tabs.Select(t => t.Id).SequenceEqual(window.Tabs.Items.Select(t => t.Id)), "the engine follows the list order");

        // Closing.
        var closing = window.Tabs.Items[2].Id;
        await window.SelectTabAsync(closing);
        await Task.Delay(400);
        Expect(await window.CloseTabAsync(closing) && window.Tabs.Items.Count == 4 && _engine.Tabs.Count == 4, "closing the active tab removes it");
        await Task.Delay(500);
        Expect(window.Tabs.Active!.Id != closing && _engine.Active!.Id == window.Tabs.Active!.Id, "a neighbour became active and is on the stage");
        Expect(sink.Seen is { Length: 6 } && sink.Black == blackAtSwitchStart, "closing the active tab did not flash the output");

        // The limit and the last tab.
        while (window.Tabs.CanAdd) await window.AddTabAsync();
        Expect(window.Tabs.Items.Count == TabRules.MaxTabs && !await window.AddTabAsync(), $"at most {TabRules.MaxTabs} tabs");
        while (window.Tabs.Items.Count > 1) await window.CloseTabAsync(window.Tabs.Items[^1].Id);
        Expect(window.Tabs.Items.Count == 1, "closing down to one tab");

        // The saved file.
        await Task.Delay(1500);
        var saved = window.ReadConfigFromDisk();
        Expect(saved.Tabs.Count == 0, "the settings file holds no tab (every start is empty)");

        // The last tab can be closed too: nothing is on air then, and the outputs would show the test pattern.
        Expect(await window.CloseTabAsync(window.Tabs.Items[0].Id), "the last tab can be closed");
        Expect(window.Tabs.Items.Count == 0 && _engine.Active is null && window.NoLive, "no tab is left, none is on the stage, and the pattern takes over");
        await Task.Delay(1500);
        Expect(window.ReadConfigFromDisk().Tabs.Count == 0, "the settings file may hold no tab");
        Expect(await window.AddTabAsync() && window.Tabs.Items.Count == 1 && _engine.Active is not null && !window.NoLive, "a new tab goes on air again");
        _engine.Hub.RemoveSink(sink);
        _logger.LogInformation(_failures.Count == 0 ? "A3 selftest ok" : "A3 selftest FAILED: {Failures}", string.Join("; ", _failures));
    }

    /// <summary>
    /// A tab that is a file instead of a web page (D1a): it goes on the stage, takes part in Preview + Take, follows a resolution change,
    /// ignores the page-only controls, is closed like any tab, and the settings file records its kind and path.
    /// </summary>
    private async Task RunDocumentTabsAsync(MainWindow window)
    {
        const string pdfPath = @"C:\Acara\Gala.pdf";
        var red = Windows.UI.Color.FromArgb(255, 0xE0, 0x30, 0x30);
        var sink = new ColourSink();
        _engine.Hub.AddSink(sink);
        await _engine.NavigateAsync(Page("30B050"), TimeSpan.FromSeconds(10));
        await Task.Delay(800);
        Expect(sink.Seen == "30B050", $"the web tab is on air (saw {sink.Seen})");

        Expect(await window.AddDocumentTabAsync(TabKind.Pdf, pdfPath, red), "a file tab can be added");
        await Task.Delay(800);
        var docInfo = window.Tabs.Active!;
        Expect(docInfo.Kind == TabKind.Pdf && docInfo.Source == pdfPath && docInfo.Url == TabRules.BlankUrl, "the list records the kind and the path");
        Expect(_engine.Active is DocumentTab { Kind: TabKind.Pdf } && _engine.Active!.Id == docInfo.Id, "the engine has a file tab on the stage");
        Expect(sink.Seen == "E03030", $"the file tab is on air (saw {sink.Seen})");

        // Page-only controls do nothing (and do not throw) while a file is on the stage.
        try
        {
            _engine.Reload();
            _engine.GoBack();
            _engine.FocusPage();
            _engine.SendMouse(CoreWebView2MouseEventKind.Move, CoreWebView2MouseEventVirtualKeys.None, 0, new System.Drawing.Point(10, 10));
            _engine.PageZoom = 2.0;
            Expect(!_engine.CanGoBack && !_engine.CanGoForward && _engine.CurrentUrl.Length == 0 && _engine.PageZoom == 1.0, "page-only state is neutral for a file tab");
        }
        catch (Exception ex)
        {
            Expect(false, $"page-only controls threw {ex.GetType().Name}");
        }
        Expect(await window.SetSeparateProfileAsync(docInfo.Id, true) is false, "a file tab has no login profile to separate");

        // Back to the web tab, then Preview + Take with the file tab as the preview.
        await window.SelectTabAsync(window.Tabs.Items[0].Id);
        await Task.Delay(800);
        Expect(sink.Seen == "30B050", $"selecting the web tab brings it back (saw {sink.Seen})");
        Expect(await window.SetPreviewModeAsync(true) && _engine.PreviewMode, "Preview + Take can start with a file tab as the preview");
        await Task.Delay(800);
        Expect(_engine.Active is DocumentTab && sink.Seen == "30B050", $"the file tab is only the preview (saw {sink.Seen})");
        Expect(await window.TakeAsync(), "TAKE works");
        await Task.Delay(800);
        Expect(_engine.Program is DocumentTab && sink.Seen == "E03030", $"the file tab is on air after TAKE (saw {sink.Seen})");
        Expect(await window.SetPreviewModeAsync(false) && !_engine.PreviewMode && _engine.Active is DocumentTab, "leaving the mode keeps the file tab on air");
        await Task.Delay(800);
        Expect(sink.Seen == "E03030", $"still on air (saw {sink.Seen})");

        window.ApplyFromCode(new Resolution(1920, 1080), 60);
        await Task.Delay(1500);
        Expect(sink.Seen == "E03030", $"a resolution change keeps the file tab on air (saw {sink.Seen})");

        // The settings file.
        await Task.Delay(1500);
        var saved = window.ReadConfigFromDisk();
        Expect(saved.Tabs.Count == 0, "the settings file holds no file tab either");

        // Closing: select the web tab first, then close the file tab.
        await window.SelectTabAsync(window.Tabs.Items[0].Id);
        await Task.Delay(500);
        Expect(await window.CloseTabAsync(docInfo.Id) && _engine.TabById(docInfo.Id) is null, "a file tab can be closed");
        Expect(_engine.Tabs.Count == window.Tabs.Items.Count, "the engine and the list agree");
        _engine.Hub.RemoveSink(sink);
        _logger.LogInformation(_failures.Count == 0 ? "D1a selftest ok" : "D1a selftest FAILED: {Failures}", string.Join("; ", _failures));
    }

    /// <summary>Preview + Take: the output keeps the program colour while another tab is prepared, TAKE swaps without a black frame, audio follows the program.</summary>
    private async Task RunPreviewTakeAsync(MainWindow window)
    {
        string[] colours = ["E03030", "3050E0", "30B050"];
        var sink = new ColourSink();
        _engine.Hub.AddSink(sink);
        await _engine.NavigateAsync(Page(colours[0]), TimeSpan.FromSeconds(10));
        Expect(!window.Tabs.CanEnterPreviewMode && !await window.SetPreviewModeAsync(true), "Preview + Take needs two tabs");
        await window.AddTabAsync(Page(colours[1]));
        await window.AddTabAsync(Page(colours[2]));
        await window.SelectTabAsync(window.Tabs.Items[0].Id);
        await Task.Delay(800);
        Expect(sink.Seen == colours[0], $"the first tab is on air (saw {sink.Seen})");
        long black = Interlocked.Read(ref sink.Black);

        var programId = window.Tabs.Items[0].Id;
        Expect(await window.SetPreviewModeAsync(true) && _engine.PreviewMode, "Preview + Take can be entered");
        await Task.Delay(800);
        Expect(window.Tabs.ProgramId == programId && window.Tabs.Active!.Id == window.Tabs.Items[1].Id, "the next tab became the preview");
        Expect(sink.Seen == colours[0], $"the output still shows the program (saw {sink.Seen})");

        await window.SelectTabAsync(window.Tabs.Items[2].Id);
        await Task.Delay(800);
        Expect(window.Tabs.Active!.Id == window.Tabs.Items[2].Id && window.Tabs.ProgramId == programId, "another tab can be prepared in the preview");
        Expect(sink.Seen == colours[0], $"preparing a tab does not reach the output (saw {sink.Seen})");
        Expect(_engine.Tabs.OfType<PageTab>().All(t => t.Browser.IsMuted == !ReferenceEquals(t, _engine.Program)), "only the program is unmuted");

        await window.SelectTabAsync(programId);
        Expect(window.Tabs.Active!.Id == window.Tabs.Items[2].Id, "clicking the program tab is ignored");
        window.SetSimulatedLive(true);
        window.Confirm = _ => false;
        Expect(!await window.CloseTabAsync(programId) && window.Tabs.Items.Count == 3, "closing the program tab while an output is on asks first, and no keeps it");
        window.SetSimulatedLive(false);

        Expect(await window.TakeAsync(), "TAKE works");
        await Task.Delay(800);
        Expect(sink.Seen == colours[2], $"the output shows the taken tab (saw {sink.Seen})");
        Expect(window.Tabs.ProgramId == window.Tabs.Items[2].Id && window.Tabs.Active!.Id == programId, "program and preview swapped");
        Expect(_engine.Tabs.OfType<PageTab>().All(t => t.Browser.IsMuted == !ReferenceEquals(t, _engine.Program)), "audio follows the program after TAKE");

        long blackBeforeResize = Interlocked.Read(ref sink.Black) - black;
        window.ApplyFromCode(new Resolution(1920, 1080), 60);
        await Task.Delay(1500);
        // A resolution change restarts the capture, so black frames while it settles are not a Preview + Take problem; only log them.
        long blackByResize = Interlocked.Read(ref sink.Black) - black - blackBeforeResize;
        _logger.LogInformation("A3b black frames before the resolution change: {Before}, caused by it: {Resize}", blackBeforeResize, blackByResize);
        black += blackByResize;
        Expect(sink.Seen == colours[2], $"a resolution change in the mode keeps the program (saw {sink.Seen})");
        Expect(await window.TakeAsync(), "TAKE works again");
        await Task.Delay(800);
        Expect(sink.Seen == colours[0], $"the output shows the first tab again (saw {sink.Seen})");

        Expect(await window.SetPreviewModeAsync(false) && !_engine.PreviewMode, "back to Langsung");
        await Task.Delay(500);
        Expect(window.Tabs.Active!.Id == window.Tabs.Items[0].Id && sink.Seen == colours[0], "the tab on air stays on air");
        Expect(_tabsAudioOk(window), "only the active tab is unmuted after leaving the mode");
        Expect(Interlocked.Read(ref sink.Black) - black == 0, $"no black frame reached the output ({Interlocked.Read(ref sink.Black) - black} did)");
        _engine.Hub.RemoveSink(sink);
        _logger.LogInformation(_failures.Count == 0 ? "A3b selftest ok" : "A3b selftest FAILED: {Failures}", string.Join("; ", _failures));
    }

    /// <summary>Screen output on the available monitor: the guard question, window placement, scale numbers, frames arriving, stopping.</summary>
    private async Task RunScreenOutputAsync(MainWindow window)
    {
        // A moving page: the window skips repeated frames, so a still page would only be drawn once.
        string animation = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "selftest-animation.html")).AbsoluteUri;
        await _engine.NavigateAsync(animation, TimeSpan.FromSeconds(20));
        window.RefreshMonitors();
        var monitors = window.Monitors;
        Expect(monitors.Count >= 1, $"at least one monitor is listed ({monitors.Count})");
        bool single = monitors.Count == 1;
        _logger.LogInformation("A4 monitors: {Monitors}", string.Join(" | ", monitors.Select(m => $"{m.Id} {m.X},{m.Y} {m.Width}x{m.Height}")));

        window.ApplyFromCode(new Resolution(2520, 1080), 60);
        if (single)
        {
            bool asked = false;
            window.Confirm = _ => { asked = true; return false; };
            Expect(!window.StartOutput() && asked && window.Output is null, "with one monitor the operator is asked, and refusing keeps the output off");
        }
        window.Confirm = _ => true;
        Expect(window.StartOutput() && window.Output is { } output, "the output can be switched on");
        output = window.Output!;
        await Task.Delay(1500);

        var monitor = output.Monitor;
        var bounds = output.PlacementInPixels();
        Expect(bounds == (monitor.X, monitor.Y, monitor.Width, monitor.Height), $"the window covers the monitor exactly (window {bounds}, monitor {monitor.X},{monitor.Y} {monitor.Width}x{monitor.Height})");
        Expect(output.FramesShown > 10, $"frames reach the window ({output.FramesShown})");
        Expect(window.OutputsLive, "an output being on counts as live for the change confirmation");
        Expect(window.SleepHeldOff, "and Windows is kept from sleeping while it is on");

        var squeeze = output.Scale;
        Expect(Math.Abs(squeeze.RestoreX - 2520.0 / monitor.Width) < 1e-9 && squeeze.Cropped == false, $"squeeze restores width by {squeeze.RestoreX:0.0000}");
        window.SetScale(ScaleMode.Fit);
        await Task.Delay(300);
        Expect(Math.Abs(output.Scale.ScaleX - output.Scale.ScaleY) < 1e-9, "fit keeps the aspect ratio");
        window.SetScale(ScaleMode.OneToOne);
        await Task.Delay(300);
        Expect(output.Scale.DestWidth == 2520 && output.Scale.DestHeight == 1080, "1:1 draws one pixel per pixel");
        window.SetScale(ScaleMode.Squeeze);

        window.ApplyFromCode(new Resolution(1920, 1080), 60);
        await Task.Delay(1500);
        Expect(output.Scale.DestWidth == monitor.Width, "a resolution change keeps the output covering the monitor");

        window.SetAlwaysOnTop(false);
        Expect(!output.Topmost, "always-on-top can be switched off while running");
        window.SetAlwaysOnTop(true);
        Expect(output.Topmost, "and on again");

        window.StopOutput(null);
        Expect(window.Output is null && !window.OutputsLive, "the output can be switched off");
        await Task.Delay(500);
        Expect(!window.SleepHeldOff, "and sleep is allowed again once no output is on");
        var saved = window.ReadConfigFromDisk();
        Expect(saved.Output.Scale == ScaleMode.Squeeze && saved.Output.AlwaysOnTop, "the screen settings are saved");
        _logger.LogInformation(_failures.Count == 0 ? "A4 selftest ok" : "A4 selftest FAILED: {Failures}", string.Join("; ", _failures));
    }

    /// <summary>Freeze and Blackout on the picture, then the audio path: capture of the browser, meter, mute, Freeze silence, tab muting, tone.</summary>
    private async Task RunGateAndAudioAsync(MainWindow window)
    {
        string[] colours = ["E03030", "3050E0"];
        var sink = new ColourSink();
        _engine.Hub.AddSink(sink);
        await _engine.NavigateAsync(Page(colours[0]), TimeSpan.FromSeconds(10));
        await Task.Delay(800);
        Expect(sink.Seen == colours[0], $"the page is live (saw {sink.Seen})");

        window.SetGate(GateMode.Freeze);
        await _engine.NavigateAsync(Page(colours[1]), TimeSpan.FromSeconds(10));
        await Task.Delay(800);
        Expect(sink.Seen == colours[0], $"Freeze holds the picture while the page changes (saw {sink.Seen})");
        window.SetGate(GateMode.Blackout);
        await Task.Delay(400);
        Expect(sink.Seen == "000000", $"Blackout sends black (saw {sink.Seen})");
        window.SetGate(GateMode.Live);
        await Task.Delay(400);
        Expect(sink.Seen == colours[1], $"releasing shows the newest picture at once (saw {sink.Seen})");
        _engine.Hub.RemoveSink(sink);

        // Audio.
        string tone = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "selftest-tone.html")).AbsoluteUri;
        await _engine.NavigateAsync(tone, TimeSpan.FromSeconds(20));
        await Task.Delay(1500);
        window.SyncAudioSources();
        var audio = window.Audio!;
        Expect(window.AudioCapture!.Count >= 1, $"a browser process is being captured ({window.AudioCapture.Count}, failed {window.AudioCapture.FailedCount})");

        double Peak() => Math.Max(audio.Levels.LeftDb, audio.Levels.RightDb);
        async Task<double> MaxPeakAsync(int ms)
        {
            double best = LevelMeterFloor;
            for (int waited = 0; waited < ms; waited += 50)
            {
                best = Math.Max(best, Peak());
                await Task.Delay(50);
            }
            return best;
        }
        double heard = await MaxPeakAsync(1000);
        Expect(heard is > -23 and < -17, $"the page tone is heard at about -20 dBFS ({heard:0.0})");

        window.SetGate(GateMode.Freeze);
        await Task.Delay(300);
        Expect(audio.IsSilenced && Peak() < -60, $"Freeze makes the audio silent ({Peak():0.0})");
        window.SetGate(GateMode.Blackout);
        double during = await MaxPeakAsync(600);
        Expect(during is > -23 and < -17, $"Blackout does not touch the audio ({during:0.0})");
        window.SetGate(GateMode.Live);
        window.SetMuted(true);
        await Task.Delay(400);
        Expect(Peak() < -60, $"Mute silences the output ({Peak():0.0})");
        window.SetMuted(false);
        Expect(await MaxPeakAsync(800) is > -23 and < -17, "unmuting brings the audio back");

        window.SetVolume(0.5);
        await Task.Delay(500); // the meter holds its peak and falls 30 dB per second; let the old level pass
        double half = await MaxPeakAsync(800);
        Expect(half is > -29 and < -23, $"half volume is about 6 dB lower ({half:0.0})");
        window.SetVolume(1);

        // Only the tab on air is heard.
        var toneTab = window.Tabs.Active!.Id;
        await window.AddTabAsync(Page(colours[0]));
        await Task.Delay(1500);
        double other = await MaxPeakAsync(800);
        Expect(other < -60, $"the tone tab is muted when another tab is on air ({other:0.0})");
        await window.SelectTabAsync(toneTab);
        await Task.Delay(800);
        Expect(await MaxPeakAsync(800) is > -23 and < -17, "and heard again when it is back on air");

        // Test tone.
        window.StartTone(ToneChannel.Left);
        double toneLevel = await MaxPeakAsync(600);
        Expect(audio.ToneActive && toneLevel is > -21.5 and < -18.5, $"the test tone is -20 dBFS ({toneLevel:0.0})");
        window.StopTone();
        Expect(!audio.ToneActive, "the tone can be stopped");

        // The local device.
        var devices = LocalAudioOutput.Devices();
        _logger.LogInformation("A5 audio devices: {Devices}", string.Join(" | ", devices.Select(d => $"{d.Name}{(d.IsDefault ? " (default)" : string.Empty)}")));
        if (devices.Count > 0) Expect(window.ChooseAudioDevice(devices[0].Id) && window.LocalAudio!.IsPlaying, "a chosen device plays");
        else _logger.LogInformation("A5 no audio device on this machine; local playback not checked");
        window.ChooseAudioDevice(null);
        await Task.Delay(1500);
        var saved = window.ReadConfigFromDisk();
        Expect(saved.Audio.Volume == 1 && !saved.Audio.Muted, "the audio settings are saved");
        _logger.LogInformation(_failures.Count == 0 ? "A5 selftest ok" : "A5 selftest FAILED: {Failures}", string.Join("; ", _failures));
    }

    /// <summary>Our own NDI source, looked at with an NDI receiver the way vMix would: name, picture size and colour, audio level, Freeze, Blackout, tone, rename.</summary>
    private async Task RunNdiAsync(MainWindow window)
    {
        if (NdiOutput.CheckRuntime() is { } missing)
        {
            Expect(false, missing);
            _logger.LogInformation("A6 selftest FAILED: {Failures}", string.Join("; ", _failures));
            return;
        }
        window.Confirm = _ => true;
        window.ApplyFromCode(new Resolution(2520, 1080), 60);
        string tone = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "selftest-tone.html")).AbsoluteUri;
        await _engine.NavigateAsync(tone, TimeSpan.FromSeconds(20));
        await Task.Delay(1500);
        window.SyncAudioSources();

        Expect(!window.RenameNdi("   "), "an empty NDI name is refused");
        Expect(window.RenameNdi("Stagecast Uji") && window.NdiSettings.Name == "Stagecast Uji", "the NDI name can be changed while off");
        Expect(window.StartNdi() && window.Ndi is { IsRunning: true }, $"NDI can be switched on ({window.NdiMessage})");
        await Task.Delay(1500);

        var sources = NdiProbe.Sources(TimeSpan.FromSeconds(8));
        _logger.LogInformation("A6 NDI sources: {Sources}", string.Join(" | ", sources));
        Expect(sources.Any(n => n.Contains("(Stagecast Uji)")), "the source shows up under the chosen name");

        using (var probe = NdiProbe.Connect("(Stagecast Uji)", TimeSpan.FromSeconds(8)))
        {
            Expect(probe is not null, "a receiver can connect");
            if (probe is not null)
            {
                var video = probe.ReadVideo(TimeSpan.FromSeconds(5));
                Expect(video is { Width: 2520, Height: 1080 }, $"the picture arrives at the native size ({video?.Width}x{video?.Height})");
                Expect(video is { } v && Math.Abs(v.Red - 0x10) <= 4 && Math.Abs(v.Green - 0x14) <= 4 && Math.Abs(v.Blue - 0x18) <= 4,
                    $"the page colour is right ({video?.Red:X2}{video?.Green:X2}{video?.Blue:X2})");
                Expect(window.Ndi!.Connections >= 1, $"the sender sees the receiver ({window.Ndi.Connections})");

                var heard = probe.ReadAudioPeaks(TimeSpan.FromSeconds(1.5));
                Expect(heard is { } h && h.Left is > -23 and < -17 && h.Right is > -23 and < -17, $"the page tone arrives on both channels ({heard?.Left:0.0} / {heard?.Right:0.0} dBFS)");

                window.SetGate(Stagecast.Core.Output.GateMode.Freeze);
                await Task.Delay(500);
                var frozen = probe.ReadAudioPeaks(TimeSpan.FromSeconds(1));
                Expect(frozen is { } f && f.Left < -60 && f.Right < -60, $"Freeze sends silence ({frozen?.Left:0.0} / {frozen?.Right:0.0})");
                window.SetGate(Stagecast.Core.Output.GateMode.Blackout);
                await Task.Delay(500);
                var black = probe.ReadLatestVideo(TimeSpan.FromSeconds(1));
                Expect(black is { Red: < 6, Green: < 6, Blue: < 6 }, $"Blackout sends black ({black?.Red:X2}{black?.Green:X2}{black?.Blue:X2})");
                var duringBlackout = probe.ReadAudioPeaks(TimeSpan.FromSeconds(1));
                Expect(duringBlackout is { } d && d.Left is > -23 and < -17, $"Blackout keeps the audio ({duringBlackout?.Left:0.0})");
                window.SetGate(Stagecast.Core.Output.GateMode.Live);
                await Task.Delay(500);
                var back = probe.ReadLatestVideo(TimeSpan.FromSeconds(1));
                Expect(back is { } b && Math.Abs(b.Red - 0x10) <= 4, "releasing brings the picture back");

                window.StartTone(ToneChannel.Left);
                await Task.Delay(600);
                var left = probe.ReadAudioPeaks(TimeSpan.FromSeconds(1));
                Expect(left is { } l && l.Left is > -21.5 and < -18.5 && l.Right < -60, $"the left-only tone reaches only the left channel ({left?.Left:0.0} / {left?.Right:0.0})");
                window.StopTone();

                window.ApplyFromCode(new Resolution(1920, 1080), 60);
                Expect(window.PendingChange is not null, "with NDI on, a resolution change asks for confirmation");
                window.ConfirmPending();
                await Task.Delay(1500);
                var resized = probe.ReadLatestVideo(TimeSpan.FromSeconds(1));
                Expect(resized is { Width: 1920, Height: 1080 }, $"a resolution change reaches the receiver ({resized?.Width}x{resized?.Height})");
            }
        }

        window.Confirm = _ => false;
        Expect(!window.RenameNdi("Lain") && window.NdiSettings.Name == "Stagecast Uji", "refusing the rename warning keeps the old name");
        window.Confirm = _ => true;
        Expect(window.RenameNdi("Stagecast Dua") && window.Ndi is { IsRunning: true, SourceName: "Stagecast Dua" }, "renaming while on recreates the sender");
        await Task.Delay(1500);
        var renamed = NdiProbe.Sources(TimeSpan.FromSeconds(8));
        Expect(renamed.Any(n => n.Contains("(Stagecast Dua)")), $"the new name is visible ({string.Join(" | ", renamed)})");

        window.StopNdi(null);
        Expect(window.Ndi is null, "NDI can be switched off");
        await Task.Delay(1500);
        var saved = window.ReadConfigFromDisk();
        Expect(saved.Ndi.Name == "Stagecast Dua", "the NDI name is saved");
        _logger.LogInformation(_failures.Count == 0 ? "A6 selftest ok" : "A6 selftest FAILED: {Failures}", string.Join("; ", _failures));
    }

    /// <summary>Our own OMT source, looked at with an OMT receiver the way vMix would (same checks as NDI).</summary>
    private async Task RunOmtAsync(MainWindow window)
    {
        window.Confirm = _ => true;
        window.ApplyFromCode(new Resolution(2520, 1080), 60);
        string tone = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "selftest-tone.html")).AbsoluteUri;
        await _engine.NavigateAsync(tone, TimeSpan.FromSeconds(20));
        await Task.Delay(1500);
        window.SyncAudioSources();

        Expect(!window.RenameOmt("   "), "an empty OMT name is refused");
        Expect(window.RenameOmt("Stagecast Uji") && window.OmtSettings.Name == "Stagecast Uji", "the OMT name can be changed while off");
        Expect(window.StartOmt() && window.Omt is { IsRunning: true }, $"OMT can be switched on ({window.OmtMessage})");
        await Task.Delay(1500);

        _logger.LogInformation("A7 OMT sources: {Sources}", string.Join(" | ", OmtProbe.Sources()));
        using (var probe = OmtProbe.Connect("(Stagecast Uji)", TimeSpan.FromSeconds(10)))
        {
            Expect(probe is not null, "the source shows up under the chosen name and a receiver can connect");
            if (probe is not null)
            {
                var video = probe.ReadLatestVideo(TimeSpan.FromSeconds(3));
                Expect(video is { Width: 2520, Height: 1080 }, $"the picture arrives at the native size ({video?.Width}x{video?.Height})");
                // The VMX codec is lossy, so allow a wider margin than for NDI.
                Expect(video is { } v && Math.Abs(v.Red - 0x10) <= 8 && Math.Abs(v.Green - 0x14) <= 8 && Math.Abs(v.Blue - 0x18) <= 8,
                    $"the page colour is right ({video?.Red:X2}{video?.Green:X2}{video?.Blue:X2})");
                Expect(window.Omt!.Receivers >= 1, $"the sender sees the receiver ({window.Omt.Receivers})");

                var heard = probe.ReadAudioPeaks(TimeSpan.FromSeconds(1.5));
                Expect(heard is { } h && h.Left is > -23 and < -17 && h.Right is > -23 and < -17, $"the page tone arrives on both channels ({heard?.Left:0.0} / {heard?.Right:0.0} dBFS)");

                window.SetGate(Stagecast.Core.Output.GateMode.Freeze);
                await Task.Delay(500);
                var frozen = probe.ReadAudioPeaks(TimeSpan.FromSeconds(1));
                Expect(frozen is { } f && f.Left < -60 && f.Right < -60, $"Freeze sends silence ({frozen?.Left:0.0} / {frozen?.Right:0.0})");
                window.SetGate(Stagecast.Core.Output.GateMode.Blackout);
                await Task.Delay(500);
                var black = probe.ReadLatestVideo(TimeSpan.FromSeconds(1));
                Expect(black is { Red: < 8, Green: < 8, Blue: < 8 }, $"Blackout sends black ({black?.Red:X2}{black?.Green:X2}{black?.Blue:X2})");
                var duringBlackout = probe.ReadAudioPeaks(TimeSpan.FromSeconds(1));
                Expect(duringBlackout is { } d && d.Left is > -23 and < -17, $"Blackout keeps the audio ({duringBlackout?.Left:0.0})");
                window.SetGate(Stagecast.Core.Output.GateMode.Live);
                await Task.Delay(500);
                var back = probe.ReadLatestVideo(TimeSpan.FromSeconds(1));
                Expect(back is { } b && Math.Abs(b.Red - 0x10) <= 8, "releasing brings the picture back");

                window.StartTone(ToneChannel.Left);
                await Task.Delay(600);
                var left = probe.ReadAudioPeaks(TimeSpan.FromSeconds(1));
                Expect(left is { } l && l.Left is > -21.5 and < -18.5 && l.Right < -60, $"the left-only tone reaches only the left channel ({left?.Left:0.0} / {left?.Right:0.0})");
                window.StopTone();

                window.ApplyFromCode(new Resolution(1920, 1080), 60);
                Expect(window.PendingChange is not null, "with OMT on, a resolution change asks for confirmation");
                window.ConfirmPending();
                await Task.Delay(1500);
                var resized = probe.ReadLatestVideo(TimeSpan.FromSeconds(1));
                Expect(resized is { Width: 1920, Height: 1080 }, $"a resolution change reaches the receiver ({resized?.Width}x{resized?.Height})");
            }
        }

        window.Confirm = _ => false;
        Expect(!window.RenameOmt("Lain") && window.OmtSettings.Name == "Stagecast Uji", "refusing the rename warning keeps the old name");
        window.Confirm = _ => true;
        Expect(window.RenameOmt("Stagecast Dua") && window.Omt is { IsRunning: true, SourceName: "Stagecast Dua" }, "renaming while on recreates the sender");
        await Task.Delay(1500);
        var renamed = OmtProbe.Sources();
        Expect(renamed.Any(n => n.Contains("(Stagecast Dua)")), $"the new name is visible ({string.Join(" | ", renamed)})");

        window.StopOmt(null);
        Expect(window.Omt is null, "OMT can be switched off");
        await Task.Delay(1500);
        var saved = window.ReadConfigFromDisk();
        Expect(saved.Omt.Name == "Stagecast Dua", "the OMT name is saved");
        _logger.LogInformation(_failures.Count == 0 ? "A7 selftest ok" : "A7 selftest FAILED: {Failures}", string.Join("; ", _failures));
    }

    /// <summary>A site that goes away and comes back, and browser processes that are killed: the pages must come back by themselves.</summary>
    private async Task RunRecoveryAsync(MainWindow window)
    {
        const int port = 47832;
        string url = $"http://localhost:{port}/";
        var problems = new List<string>();
        int recovered = 0;
        _engine.TabProblem += (_, message) => problems.Add(message);
        _engine.TabRecovered += _ => recovered++;
        var sink = new ColourSink();
        _engine.Hub.AddSink(sink);

        System.Net.HttpListener StartSite()
        {
            var listener = new System.Net.HttpListener();
            listener.Prefixes.Add(url);
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    try
                    {
                        var context = await listener.GetContextAsync();
                        var body = System.Text.Encoding.UTF8.GetBytes("<!doctype html><body style='margin:0;background:#30B050'></body>");
                        context.Response.ContentType = "text/html";
                        context.Response.OutputStream.Write(body, 0, body.Length);
                        context.Response.Close();
                    }
                    catch (Exception ex) when (ex is System.Net.HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        break;
                    }
                }
            });
            return listener;
        }

        async Task<bool> WaitAsync(Func<bool> condition, int seconds)
        {
            for (int i = 0; i < seconds * 10 && !condition(); i++) await Task.Delay(100);
            return condition();
        }

        var site = StartSite();
        Expect(await _engine.NavigateAsync(url, TimeSpan.FromSeconds(15)), "the local site loads");
        await Task.Delay(800);
        Expect(sink.Seen == "30B050", $"the site's page is on the output (saw {sink.Seen})");

        // The network (the site) goes away: the tab says so and keeps trying, then the page comes back by itself.
        site.Stop();
        site.Close();
        Expect(!await _engine.NavigateAsync(url, TimeSpan.FromSeconds(15)), "loading fails while the site is away");
        Expect(await WaitAsync(() => _engine.Active!.Problem is not null, 5), "the tab reports a problem");
        _logger.LogInformation("A8 problem message: {Message}", _engine.Active!.Problem);
        await Task.Delay(6000); // at least two failed retries
        Expect(_engine.Active!.Problem is not null, "it is still reported while the site is away");
        site = StartSite();
        Expect(await WaitAsync(() => _engine.Active!.Problem is null, 40), "the page loads again by itself once the site is back");
        await Task.Delay(800);
        Expect(sink.Seen == "30B050", $"and shows on the output (saw {sink.Seen})");
        Expect(recovered >= 1, "a recovery was announced");

        // A browser process dies while its tab is on air (a new tab goes on air at once, here with its own profile).
        await window.AddTabAsync(url);
        var otherId = window.Tabs.Items[^1].Id;
        Expect(await window.SetSeparateProfileAsync(otherId, true), "a second tab gets its own profile");
        await Task.Delay(2500);
        Expect(window.Tabs.Active!.Id == otherId, "the new tab is the one on air");
        int before = recovered;
        int deadPid = (int)_engine.PageTabById(otherId)!.Browser.BrowserProcessId;
        System.Diagnostics.Process.GetProcessById(deadPid).Kill(entireProcessTree: true);
        Expect(await WaitAsync(() => recovered > before, 30), "the tab on air is recovered after its browser died");
        await Task.Delay(1500);
        var again = _engine.PageTabById(otherId);
        Expect(again is not null && (int)again.Browser.BrowserProcessId != deadPid && again.Problem is null, "it has a new browser process and no problem");
        Expect(_engine.Active!.Id == otherId && window.Tabs.Active!.Id == otherId, "and is still the active one");
        Expect(sink.Seen == "30B050", $"the output shows the page again (saw {sink.Seen})");
        Expect(_engine.Tabs.Count == window.Tabs.Items.Count, "the engine and the list still agree");

        // The shared browser dies while its tab is not on air.
        var sharedId = window.Tabs.Items[0].Id;
        int sharedPid = (int)_engine.PageTabById(sharedId)!.Browser.BrowserProcessId;
        before = recovered;
        System.Diagnostics.Process.GetProcessById(sharedPid).Kill(entireProcessTree: true);
        Expect(await WaitAsync(() => recovered > before, 30), "the tab on the shared profile is recovered after its browser died");
        await Task.Delay(1500);
        Expect((int)_engine.PageTabById(sharedId)!.Browser.BrowserProcessId != sharedPid && _engine.Active!.Id == otherId, "it has a new browser and the tab on air did not change");
        Expect(sink.Seen == "30B050", $"the output was not disturbed (saw {sink.Seen})");
        window.SyncAudioSources();

        site.Stop();
        site.Close();
        _engine.Hub.RemoveSink(sink);
        _logger.LogInformation(_failures.Count == 0 ? "A8 selftest ok" : "A8 selftest FAILED: {Failures}", string.Join("; ", _failures));
    }

    /// <summary>
    /// A long run with the outputs on: samples memory and the frame counters every 10 seconds into a CSV in the log folder and logs a summary.
    /// STAGECAST_SOAK_MINUTES sets the length (default 3). It has no pass or fail except that nothing may crash; a person reads the trend.
    /// </summary>
    private async Task RunSoakAsync(MainWindow window)
    {
        int minutes = int.TryParse(Environment.GetEnvironmentVariable("STAGECAST_SOAK_MINUTES"), out int m) && m > 0 ? m : 3;
        window.Confirm = _ => true;
        window.ApplyFromCode(new Resolution(2520, 1080), 60);
        string page = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "selftest-soak.html")).AbsoluteUri;
        await _engine.NavigateAsync(page, TimeSpan.FromSeconds(20));
        await window.AddTabAsync(page);
        await Task.Delay(2000);
        window.SyncAudioSources();
        bool ndi = window.StartNdi();
        bool omt = window.StartOmt();
        bool screen = window.Monitors.Count > 1 && window.StartOutput();
        _logger.LogInformation("SOAK start: {Minutes} min, 2520x1080@60, NDI {Ndi} ({NdiMsg}), OMT {Omt} ({OmtMsg}), screen {Screen} ({Monitors} monitors)",
            minutes, ndi, window.NdiMessage, omt, window.OmtMessage, screen, window.Monitors.Count);

        string csv = Path.Combine(window.LogDirectory, $"soak-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        Directory.CreateDirectory(window.LogDirectory);
        await File.WriteAllTextAsync(csv, "seconds,app_private_mb,app_working_set_mb,app_handles,gc_heap_mb,browser_private_mb,browser_processes,hub_delivered,hub_repeated,hub_dropped,sink_errors,ndi_sent,ndi_skipped,omt_sent,omt_skipped,tick_p99_ms,cpu_percent\n");
        var me = System.Diagnostics.Process.GetCurrentProcess();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var samples = new List<(double Seconds, double App, double Browser)>();
        var lastCpu = me.TotalProcessorTime;
        var lastClock = clock.Elapsed;
        _engine.ResetStats();
        int tabFlip = 0;
        while (clock.Elapsed < TimeSpan.FromMinutes(minutes))
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            me.Refresh();
            var browsers = System.Diagnostics.Process.GetProcessesByName("msedgewebview2");
            double browserMb = browsers.Sum(p => { try { return p.PrivateMemorySize64; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; } }) / 1048576.0;
            foreach (var p in browsers) p.Dispose();
            var stats = _engine.Stats().Hub;
            double cpu = (me.TotalProcessorTime - lastCpu).TotalSeconds / Math.Max(0.001, (clock.Elapsed - lastClock).TotalSeconds) / Environment.ProcessorCount * 100;
            lastCpu = me.TotalProcessorTime;
            lastClock = clock.Elapsed;
            double appMb = me.PrivateMemorySize64 / 1048576.0;
            samples.Add((clock.Elapsed.TotalSeconds, appMb, browserMb));
            await File.AppendAllTextAsync(csv, FormattableString.Invariant(
                $"{clock.Elapsed.TotalSeconds:0},{appMb:0.0},{me.WorkingSet64 / 1048576.0:0.0},{me.HandleCount},{GC.GetTotalMemory(false) / 1048576.0:0.0},{browserMb:0.0},{browsers.Length},{stats.Delivered},{stats.Repeated},{stats.Dropped},{stats.SinkErrors},{window.Ndi?.VideoSent ?? 0},{window.Ndi?.VideoSkipped ?? 0},{window.Omt?.VideoSent ?? 0},{window.Omt?.VideoSkipped ?? 0},{stats.TickP99Ms:0.0},{cpu:0.0}\n"));
            // Every minute the other tab goes on air, as an operator would.
            if (samples.Count % 6 == 0) await window.SelectTabAsync(window.Tabs.Items[++tabFlip % 2].Id);
        }

        static double Slope(IEnumerable<(double X, double Y)> points)
        {
            var list = points.ToList();
            if (list.Count < 3) return 0;
            double mx = list.Average(p => p.X), my = list.Average(p => p.Y);
            double denominator = list.Sum(p => (p.X - mx) * (p.X - mx));
            return denominator == 0 ? 0 : list.Sum(p => (p.X - mx) * (p.Y - my)) / denominator;
        }
        // The first minute is warm-up (caches, JIT); the trend is measured after it.
        var settled = samples.Where(s => s.Seconds >= 60).ToList();
        double appPerHour = Slope(settled.Select(s => (s.Seconds, s.App))) * 3600;
        double browserPerHour = Slope(settled.Select(s => (s.Seconds, s.Browser))) * 3600;
        var final = _engine.Stats().Hub;
        _logger.LogInformation("SOAK summary: {Seconds:0} s, app memory {AppFirst:0}->{AppLast:0} MB (trend {AppTrend:+0;-0} MB/h), browsers {BrFirst:0}->{BrLast:0} MB (trend {BrTrend:+0;-0} MB/h), hub delivered {Delivered} repeated {Repeated} dropped {Dropped} sink errors {Errors}, NDI skipped {NdiSkipped}, OMT skipped {OmtSkipped}, csv {Csv}",
            clock.Elapsed.TotalSeconds, samples[0].App, samples[^1].App, appPerHour, samples[0].Browser, samples[^1].Browser, browserPerHour,
            final.Delivered, final.Repeated, final.Dropped, final.SinkErrors, window.Ndi?.VideoSkipped ?? 0, window.Omt?.VideoSkipped ?? 0, csv);
        Expect(final.SinkErrors == 0, $"no sink errors ({final.SinkErrors})");
        window.StopNdi(null);
        window.StopOmt(null);
        window.StopOutput(null);
        _logger.LogInformation(_failures.Count == 0 ? "SOAK finished ok" : "SOAK finished with problems: {Failures}", string.Join("; ", _failures));
    }

    private const double LevelMeterFloor = -90;

    private bool _tabsAudioOk(MainWindow window)
        => _engine.Tabs.OfType<PageTab>().All(t => t.Browser.IsMuted == !ReferenceEquals(t, _engine.Active));

    /// <summary>Confirmation of resolution/rate changes while an output is on (simulated), and the resolution presets.</summary>
    private async Task RunChangesAndPresetsAsync(MainWindow window)
    {
        var hd = new Resolution(1920, 1080);
        var wide = new Resolution(2520, 1080);
        await Task.Delay(500);

        window.ApplyFromCode(hd, 60);
        Expect(_engine.Resolution == hd && !window.BannerVisible, "with no output on, a new resolution applies at once, without a banner");

        window.SetSimulatedLive(true);
        window.ApplyFromCode(wide, 30);
        await Task.Delay(300);
        Expect(_engine.Resolution == hd && _engine.Fps == 60, "with an output on, nothing changes before confirmation");
        Expect(window.BannerVisible && window.PendingChange == new Stagecast.Core.Output.RenderChange(wide, 30), "the banner waits with the requested resolution and rate");

        window.CancelPending();
        Expect(_engine.Resolution == hd && _engine.Fps == 60 && !window.BannerVisible && window.PendingChange is null, "Batal keeps everything as it was");

        window.ApplyFromCode(wide, 30);
        window.ConfirmPending();
        await Task.Delay(1500);
        Expect(_engine.Resolution == wide && _engine.Fps == 30 && !window.BannerVisible, "Terapkan applies the waiting change");
        var stats = _engine.Stats();
        Expect(stats.Capture is { Width: 2520, Height: 1080 }, "frames follow the confirmed resolution");

        window.SetSimulatedLive(false);
        window.ApplyFromCode(hd, 60);
        Expect(_engine.Resolution == hd && _engine.Fps == 60 && !window.BannerVisible, "with the output off again, changes apply at once");

        // Presets.
        window.Confirm = _ => true;
        Expect(window.SavePreset("") == PresetError.NameEmpty, "an empty preset name is refused");
        Expect(window.SavePreset("Full HD baru") == PresetError.ResolutionInUse, "a resolution that already has a preset is refused");
        window.ApplyFromCode(new Resolution(1600, 900), 60);
        Expect(window.SavePreset(new string('x', 25)) == PresetError.NameTooLong, "a name over 24 characters is refused");
        Expect(window.SavePreset("led 7×3") == PresetError.NameInUse, "a name already used (ignoring case) is refused");
        Expect(window.SavePreset("  Panggung  ") == PresetError.None, "a valid preset is saved");
        Expect(window.SavePreset("Lagi") == PresetError.ResolutionInUse, "the same resolution cannot be saved twice");

        var reloaded = PresetCatalog.FromConfig(window.ReadConfigFromDisk());
        Expect(reloaded.All.Any(p => p.Name == "Panggung" && p.Resolution == new Resolution(1600, 900)), "the preset is in the settings file, so it survives a restart");

        Expect(!window.DeletePreset(PresetRules.BuiltIns[0]), "a built-in preset cannot be deleted");
        var mine = window.Presets.All.First(p => p.Name == "Panggung");
        bool asked = false;
        window.Confirm = _ => { asked = true; return false; };
        Expect(!window.DeletePreset(mine) && asked && window.Presets.All.Contains(mine), "declining the confirmation keeps the preset");
        window.Confirm = _ => true;
        Expect(window.DeletePreset(mine) && !window.Presets.All.Contains(mine), "deleting asks and then removes the preset");
        Expect(_engine.Resolution == new Resolution(1600, 900), "deleting the preset in use does not change the active resolution");
        Expect(!PresetCatalog.FromConfig(window.ReadConfigFromDisk()).All.Any(p => p.Name == "Panggung"), "the deleted preset is gone from the settings file");

        window.ApplyFromCode(wide, 60);
        _logger.LogInformation(_failures.Count == 0 ? "A2c selftest ok" : "A2c selftest FAILED: {Failures}", string.Join("; ", _failures));
    }

    /// <summary>Page controls: pointer mapping in Fit and 100%, preview region, panning, wheel, page zoom, keyboard and Esc, history.</summary>
    private async Task RunControlsAsync(MainWindow window)
    {
        var preview = window.Preview;
        var render = _engine.Resolution;
        string page = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "selftest-controls.html")).AbsoluteUri;
        await _engine.NavigateAsync(page, TimeSpan.FromSeconds(20));
        await Task.Delay(1000);
        preview.Interactive = true;
        var size = preview.SurfaceSize;
        _logger.LogInformation("A2b preview area {W:F0}x{H:F0} for a {Render} page", size.Width, size.Height, render);

        (double X, double Y, string? Button)[] targets =
        [
            (60, 60, "t1"), (1260, 60, "t2"), (2460, 60, "t3"), (60, 540, "t4"), (1260, 540, "t5"),
            (2460, 540, "t6"), (60, 1020, "t7"), (1260, 1020, "t8"), (2460, 1020, "t9"),
            (917.3, 411.9, null), (1801.6, 733.4, null),
            (1100, 450, null), (1400, 640, null), (1500, 500, null), (2300, 880, null), (2380, 960, null), (2000, 800, null),
        ];
        foreach (var (label, setup) in new (string, Action)[]
        {
            ("Fit", preview.ShowFit),
            ("100% centre", () => { preview.ShowActual(); preview.ShowActualAtForTest(1260, 540); }),
            ("100% right-bottom", () => preview.ShowActualAtForTest(2400, 1000)),
        })
        {
            setup();
            await Task.Delay(700);
            double worst = 0;
            int hits = 0, wrong = 0, missed = 0;
            foreach (var (x, y, button) in targets)
            {
                var at = preview.ControlPointOf(x, y);
                if (at.X < 0 || at.Y < 0 || at.X >= size.Width || at.Y >= size.Height || !preview.PageContainsControlPoint(at)) continue;
                await _engine.EvaluateAsync("window.resetClicks()");
                preview.InjectClick(at);
                var click = await WaitForClickAsync();
                if (click is null) { missed++; continue; }
                hits++;
                worst = Math.Max(worst, Math.Sqrt(Math.Pow(click.Value.X - x, 2) + Math.Pow(click.Value.Y - y, 2)));
                if (button is not null && click.Value.Id != button) wrong++;
            }
            var shown = preview.ShownPicture;
            var wantedRegion = preview.View.Region(render, size.Width, size.Height);
            _logger.LogInformation("A2b {Label}: {Hits} clicks, worst error {Worst:F2} px, wrong buttons {Wrong}, missed {Missed}; picture {PW}x{PH} at ({PX},{PY}) mip {Mip}",
                label, hits, worst, wrong, missed, shown.Width, shown.Height, shown.X, shown.Y, shown.Mip);
            // A shrunk view cannot place a click closer than its own pixel size: when the page is drawn at 1/n, one control pixel is n page pixels.
            double pagePixelsPerControlPixel = label == "Fit" && shown.Width > 0 ? (double)render.Width / shown.Width : 1.0;
            double tolerance = Math.Max(2.0, 1.5 * pagePixelsPerControlPixel);
            Expect(hits >= 2 && worst <= tolerance && wrong == 0 && missed == 0, $"{label}: clicks land on the intended page point (within {tolerance:F1} px)");
            Expect(shown.Mip == wantedRegion.Mip, $"{label}: preview picture uses the requested level (got {shown.Mip}, wanted {wantedRegion.Mip})");
            if (label != "Fit") Expect(shown.X == wantedRegion.X && shown.Y == wantedRegion.Y, $"{label}: preview picture starts at the requested corner");
        }

        // Dragging the page in 100% moves the visible part and cannot leave the page.
        preview.ShowActualAtForTest(1260, 540);
        double before = preview.View.CenterX;
        preview.PanByForTest(-100, 0);
        Expect(Math.Abs(preview.View.CenterX - (before + 100)) < 0.5, "dragging 100 px left moves the view 100 px right");
        preview.PanByForTest(100000, 100000);
        Expect(preview.View.CenterX <= render.Width / 2.0 && preview.View.CenterX >= 0, "dragging far cannot leave the page");

        _engine.SendMouse(CoreWebView2MouseEventKind.Wheel, CoreWebView2MouseEventVirtualKeys.None, unchecked((uint)-360), new System.Drawing.Point(1300, 350));
        await Task.Delay(500);
        Expect((await ReadStateAsync()).GetProperty("scrollTop").GetDouble() > 0, "mouse wheel scrolls the page");

        foreach (double zoom in new[] { 2.0, 0.5, 1.0 })
        {
            window.SetPageZoom(zoom);
            await Task.Delay(500);
            int w = (await ReadStateAsync()).GetProperty("w").GetInt32();
            _logger.LogInformation("A2b page zoom {Zoom}: page viewport width {W}", zoom, w);
            Expect(Math.Abs(w - render.Width / zoom) <= 1, $"page zoom {zoom} changes the page viewport to {render.Width / zoom}");
        }

        // Keyboard to the page, then Esc takes it back.
        preview.ShowFit();
        await Task.Delay(500);
        // Real keystrokes go to whatever window has the focus on the desktop, which another program (or a person) can change
        // at any moment; try a few times before calling it a failure.
        string typed = "";
        for (int attempt = 1; attempt <= 3 && typed != "stagecast"; attempt++)
        {
            Expect(await window.BringToFrontAsync(), "the window can be brought to the front for the keyboard test");
            await _engine.EvaluateAsync("document.getElementById('input').value=''");
            preview.InjectClick(preview.ControlPointOf(500, 230));
            _engine.FocusPage();
            await Task.Delay(400);
            TypeText("stagecast");
            await Task.Delay(400);
            typed = (await ReadStateAsync()).GetProperty("input").GetString() ?? "";
            _logger.LogInformation("A2b keyboard attempt {Attempt}: typed '{Typed}', page has focus {Focus}", attempt, typed, _engine.PageHasFocus);
        }
        Expect(typed == "stagecast", "keystrokes reach the page");
        Expect(_engine.PageHasFocus, "page reports keyboard focus");
        PressKey(0x1B);
        for (int i = 0; i < 20 && _engine.PageHasFocus; i++) await Task.Delay(50);
        Expect(!_engine.PageHasFocus, "Esc releases the keyboard from the page");
        TypeText("zzz");
        await Task.Delay(400);
        string after = (await ReadStateAsync()).GetProperty("input").GetString() ?? "";
        Expect(after == "stagecast", $"keys no longer reach the page after Esc (input is '{after}')");

        // History.
        await _engine.NavigateAsync("data:text/html,<body>second</body>", TimeSpan.FromSeconds(10));
        Expect(_engine.CanGoBack, "back is available after a second page");
        var back = new TaskCompletionSource();
        void Done(object? s, CoreWebView2NavigationCompletedEventArgs e) => back.TrySetResult();
        _engine.Browser.NavigationCompleted += Done;
        _engine.GoBack();
        await Task.WhenAny(back.Task, Task.Delay(5000));
        _engine.Browser.NavigationCompleted -= Done;
        Expect(_engine.CurrentUrl.EndsWith("selftest-controls.html"), "back returns to the first page");
        Expect(_engine.CanGoForward, "forward is available after going back");

        preview.Interactive = false;
        _logger.LogInformation(_failures.Count == 0 ? "A2b selftest ok" : "A2b selftest FAILED: {Failures}", string.Join("; ", _failures));
    }

    private async Task<(double X, double Y, string Id)?> WaitForClickAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var clicks = (await ReadStateAsync()).GetProperty("clicks").EnumerateArray().ToArray();
            if (clicks.Length > 0) return (clicks[^1][0].GetDouble(), clicks[^1][1].GetDouble(), clicks[^1][2].GetString() ?? "");
            await Task.Delay(20);
        }
        return null;
    }

    private async Task<JsonElement> ReadStateAsync()
        => JsonDocument.Parse(JsonSerializer.Deserialize<string>(await _engine.EvaluateAsync("window.state()"))!).RootElement.Clone();

    // Real keystrokes to whatever has the keyboard focus, like a person typing.
    private static void TypeText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (char c in text)
        {
            inputs.Add(Key(0, c, 0x0004));
            inputs.Add(Key(0, c, 0x0004 | 0x0002));
        }
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private static void PressKey(ushort virtualKey)
    {
        INPUT[] inputs = [Key(virtualKey, 0, 0), Key(virtualKey, 0, 0x0002)];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, ushort scan, uint flags) => new() { type = 1, ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } };

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

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
