using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
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
internal sealed class SelfTest
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
        if (name == "a2b") { await RunControlsAsync(window); return; }
        if (name is "a3c-set" or "a3c-check") { await RunLoginsAsync(window, name == "a3c-set"); return; }
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
            Expect(await CookieNamesAsync(_engine.Active) == "sess", "the shared tab holds the cookie its login set");

            Expect(await window.AddTabAsync($"{LoginSite}/page"), "a second tab can be added");
            var second = window.Tabs.Active;
            window.Confirm = _ => true;
            Expect(await window.SetSeparateProfileAsync(second.Id, true), "the second tab can get its own profile");
            await Task.Delay(1500);
            var secondTab = _engine.TabById(second.Id)!;
            Expect(secondTab.ProfileId is { Length: 32 } && _engine.Active.Id == second.Id, "the second tab runs on its own profile and is still the active one");
            Expect(await CookieNamesAsync(secondTab) == "", "a separate profile does not see the shared login");

            await _engine.NavigateAsync($"{LoginSite}/dologin2", TimeSpan.FromSeconds(10));
            Expect(await CookieNamesAsync(secondTab) == "sess2", "the separate tab holds only its own login");
            Expect(await CookieNamesAsync(_engine.TabById(shared.Id)!) == "sess", "the shared tab does not see the separate login");

            await _engine.NavigateAsync($"{LoginSite}/page", TimeSpan.FromSeconds(10));
            await window.CheckSessionsAsync();
            Expect(window.Sessions[shared.Id] == SessionState.LoggedIn, "the shared tab (a page with a session cookie) is guessed as logged in");
            Expect(window.Sessions[second.Id] == SessionState.LoggedIn, "the separate tab is guessed as logged in");
            await _engine.NavigateAsync(_engine.TabById(shared.Id)!, $"{LoginSite}/login", TimeSpan.FromSeconds(10));
            await window.CheckSessionsAsync();
            Expect(window.Sessions[shared.Id] == SessionState.NeedsLogin, "a login page is guessed as needing login");

            await Task.Delay(1800); // let the tabs reach config.json
            var saved = window.ReadConfigFromDisk();
            Expect(saved.Tabs.Count == 2 && saved.Tabs[0].ProfileId is null && saved.Tabs[1].ProfileId == secondTab.ProfileId, "the settings file knows which tab has its own profile (and holds no cookie)");
            _logger.LogInformation(_failures.Count == 0 ? "A3c set selftest ok" : "A3c set selftest FAILED: {Failures}", string.Join("; ", _failures));
            return;
        }

        // Second launch: everything was closed and opened again with the same data folder.
        Expect(window.Tabs.Items.Count == 2 && window.Tabs.Items[1].ProfileId is { Length: 32 }, "both tabs and the separate profile were restored");
        Expect(!Directory.Exists(Path.Combine(profiles, "tab-" + new string('f', 32))), "a profile folder no tab refers to was removed at startup");
        var tab0 = _engine.TabById(window.Tabs.Items[0].Id)!;
        var tab1 = _engine.TabById(window.Tabs.Items[1].Id)!;
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
        Expect(_engine.Tabs.Count == 2 && _engine.TabById(window.Tabs.Items[1].Id)?.ProfileId is null, "the engine and the list agree afterwards");
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
        var activeBefore = window.Tabs.Active.Id;
        Expect(window.MoveTab(activeBefore, +1) && window.Tabs.Active.Id == activeBefore, "moving a tab keeps the same tab active");
        Expect(_engine.Tabs.Select(t => t.Id).SequenceEqual(window.Tabs.Items.Select(t => t.Id)), "the engine follows the list order");

        // Closing.
        var closing = window.Tabs.Items[2].Id;
        await window.SelectTabAsync(closing);
        await Task.Delay(400);
        Expect(await window.CloseTabAsync(closing) && window.Tabs.Items.Count == 4 && _engine.Tabs.Count == 4, "closing the active tab removes it");
        await Task.Delay(500);
        Expect(window.Tabs.Active.Id != closing && _engine.Active.Id == window.Tabs.Active.Id, "a neighbour became active and is on the stage");
        Expect(sink.Seen is { Length: 6 } && sink.Black == blackAtSwitchStart, "closing the active tab did not flash the output");

        // The limit and the last tab.
        while (window.Tabs.CanAdd) await window.AddTabAsync();
        Expect(window.Tabs.Items.Count == TabRules.MaxTabs && !await window.AddTabAsync(), $"at most {TabRules.MaxTabs} tabs");
        while (window.Tabs.CanClose) await window.CloseTabAsync(window.Tabs.Items[^1].Id);
        Expect(window.Tabs.Items.Count == 1 && !await window.CloseTabAsync(window.Tabs.Items[0].Id), "the last tab cannot be closed");

        // The saved file.
        await Task.Delay(1500);
        var saved = window.ReadConfigFromDisk();
        Expect(saved.Tabs.Count == 1 && saved.Tabs[0].Name == window.Tabs.Items[0].Name, "the settings file holds the tabs");
        _engine.Hub.RemoveSink(sink);
        _logger.LogInformation(_failures.Count == 0 ? "A3 selftest ok" : "A3 selftest FAILED: {Failures}", string.Join("; ", _failures));
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
        Expect(window.Tabs.ProgramId == programId && window.Tabs.Active.Id == window.Tabs.Items[1].Id, "the next tab became the preview");
        Expect(sink.Seen == colours[0], $"the output still shows the program (saw {sink.Seen})");

        await window.SelectTabAsync(window.Tabs.Items[2].Id);
        await Task.Delay(800);
        Expect(window.Tabs.Active.Id == window.Tabs.Items[2].Id && window.Tabs.ProgramId == programId, "another tab can be prepared in the preview");
        Expect(sink.Seen == colours[0], $"preparing a tab does not reach the output (saw {sink.Seen})");
        Expect(_engine.Tabs.All(t => t.Browser.IsMuted == !ReferenceEquals(t, _engine.Program)), "only the program is unmuted");

        await window.SelectTabAsync(programId);
        Expect(window.Tabs.Active.Id == window.Tabs.Items[2].Id, "clicking the program tab is ignored");
        Expect(!await window.CloseTabAsync(programId) && window.Tabs.Items.Count == 3, "the program tab cannot be closed");

        Expect(await window.TakeAsync(), "TAKE works");
        await Task.Delay(800);
        Expect(sink.Seen == colours[2], $"the output shows the taken tab (saw {sink.Seen})");
        Expect(window.Tabs.ProgramId == window.Tabs.Items[2].Id && window.Tabs.Active.Id == programId, "program and preview swapped");
        Expect(_engine.Tabs.All(t => t.Browser.IsMuted == !ReferenceEquals(t, _engine.Program)), "audio follows the program after TAKE");

        window.ApplyFromCode(new Resolution(1920, 1080), 60);
        await Task.Delay(1500);
        Expect(sink.Seen == colours[2], $"a resolution change in the mode keeps the program (saw {sink.Seen})");
        Expect(await window.TakeAsync(), "TAKE works again");
        await Task.Delay(800);
        Expect(sink.Seen == colours[0], $"the output shows the first tab again (saw {sink.Seen})");

        Expect(await window.SetPreviewModeAsync(false) && !_engine.PreviewMode, "back to Langsung");
        await Task.Delay(500);
        Expect(window.Tabs.Active.Id == window.Tabs.Items[0].Id && sink.Seen == colours[0], "the tab on air stays on air");
        Expect(_tabsAudioOk(window), "only the active tab is unmuted after leaving the mode");
        Expect(Interlocked.Read(ref sink.Black) - black == 0, $"no black frame reached the output ({Interlocked.Read(ref sink.Black) - black} did)");
        _engine.Hub.RemoveSink(sink);
        _logger.LogInformation(_failures.Count == 0 ? "A3b selftest ok" : "A3b selftest FAILED: {Failures}", string.Join("; ", _failures));
    }

    private bool _tabsAudioOk(MainWindow window)
        => _engine.Tabs.All(t => t.Browser.IsMuted == !ReferenceEquals(t, _engine.Active));

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
            Expect(hits >= 2 && worst <= 2.0 && wrong == 0 && missed == 0, $"{label}: clicks land on the intended page point");
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
