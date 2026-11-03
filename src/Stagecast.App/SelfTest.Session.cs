using System.IO;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Configuration;
using Stagecast.Core.Rendering;
using Stagecast.Core.Sessions;
using Stagecast.Core.Tabs;
using Stagecast.Rendering;

namespace Stagecast.App;

// --selftest f1: saving and opening sessions in the running application, with PDFs the test writes itself. "Another computer" is the same
// folder moved elsewhere with one document left behind.
internal sealed partial class SelfTest
{
    private async Task RunSessionAsync(MainWindow window)
    {
        string root = Path.Combine(Path.GetTempPath(), "stagecast-selftest-session", Guid.NewGuid().ToString("N")[..8]);
        string home = Path.Combine(root, "asal"), other = Path.Combine(root, "komputer-lain"), spare = Path.Combine(root, "cadangan");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(spare);
        string a = Path.Combine(home, "a.pdf"), b = Path.Combine(home, "b.pdf");
        WritePdf(a, (960, 540, "1 0 0"), (800, 600, "0 1 0"), (960, 540, "0 0 1"));
        WritePdf(b, (800, 600, "1 0.6 0"));
        string sessionPath = Path.Combine(home, "Misa.stagecast");

        // A deck too, when PowerPoint can be driven on this PC (the file is written by the test, like the one of P2).
        string deck = Path.Combine(home, "Pembuka.pptx");
        string? written = null;
        try { written = WriteDeck(deck); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            _logger.LogWarning(ex, "F1 could not write the test deck; the session is tested with PDFs only");
        }
        bool ppt = written is not null;
        int count = ppt ? 5 : 4;

        var messages = new List<string>();
        var asked = new List<string>();
        window.Notify = messages.Add;
        window.Confirm = text => { asked.Add(text); return true; };
        window.AskSaveChanges = _ => SaveAnswer.Discard;
        window.PickSessionToSave = () => sessionPath;
        int relinkAsked = 0;
        SessionPlan? lastPlan = null;
        Func<SessionPlan, ISessionFiles, SessionPlan> resolver = (plan, _) => plan;
        window.AskRelink = (_, plan, files) => { relinkAsked++; lastPlan = plan; return resolver(plan, files); };
        (int Current, int InSession)? openAsked = null;
        bool replace = true;
        window.AskOpenChoice = (_, current, inSession, _, _, _, _) => { openAsked = (current, inSession); return new OpenChoice(replace, false, false); };

        DocumentTab? Doc(int index) => window.Tabs.Items.Count > index ? _engine.TabById(window.Tabs.Items[index].Id) as DocumentTab : null;

        // ---- build a small show ----
        Expect(window.Tabs.Items.Count == 1 && window.SessionPath is null, "a start has one test tab and no session file");
        Expect(await window.AddTabAsync(), "a second web tab");
        window.Confirm = _ => true;
        Expect(await window.SetSeparateProfileAsync(window.Tabs.Active!.Id, true), "the second tab gets its own profile");
        await Task.Delay(1500);
        string profile = window.Tabs.Items[1].ProfileId!;
        Expect(await window.AddPdfTabAsync(a) && await window.AddPdfTabAsync(b), "two PDF tabs");
        Expect(await WaitUntilAsync(() => Doc(2) is { Pdf.PageCount: 3 } && Doc(3) is { Pdf.PageCount: 1 }), "both PDFs are open");
        if (ppt) Expect(await window.AddPowerPointTabAsync(deck) && Doc(4) is { PowerPoint.PageCount: 5 }, "and a PowerPoint tab with five slides");
        window.RenameTab(window.Tabs.Items[0].Id, "Awal");
        window.RenameTab(window.Tabs.Items[2].Id, "Khotbah");
        await window.SelectTabAsync(window.Tabs.Items[2].Id);
        var names = window.Tabs.Items.Select(t => t.Name).ToList();

        // ---- save ----
        Expect(window.SaveSession(), "the session can be saved");
        var saved = SessionStore.Load(sessionPath);
        var file = saved.Session;
        Expect(saved.Status == SessionLoadStatus.Loaded && file is { ActiveTab: 2 } && file.Tabs.Count == count, $"the file holds {count} tabs and the live one");
        Expect(file is not null && file.Tabs[1].ProfileId == profile && file.Tabs[0].Url == TabRules.BlankUrl, "a web tab keeps its profile id and address");
        Expect(file is not null && file.Tabs[2].Source == a && file.Tabs[2].RelativeSource == "a.pdf" && file.Tabs[3].Kind == "pdf", "a document keeps its path, also relative to the file");
        Expect(file?.Render is { } r && r.Width == _engine.Resolution.Width && r.Height == _engine.Resolution.Height && r.Fps == _engine.Fps, "and the render size");
        Expect(window.SessionPath == sessionPath && !window.SessionDirty, "the window now knows the file, with nothing changed");
        Expect(window.ReadConfigFromDisk().RecentSessions.FirstOrDefault() == sessionPath, "the recent list in the settings has it first");
        if (ppt) Expect(file is not null && file.Tabs[4].Kind == "powerpoint" && file.Tabs[4].Source == deck && file.Tabs[4].RelativeSource == "Pembuka.pptx", "a deck is saved the same way");
        Expect(!File.ReadAllText(sessionPath).Contains("password", StringComparison.OrdinalIgnoreCase), "the file holds no password");

        window.RenameTab(window.Tabs.Items[0].Id, "Awal");
        Expect(!window.SessionDirty, "naming a tab what it is called already changes nothing");
        window.RenameTab(window.Tabs.Items[0].Id, "Berubah");
        Expect(window.SessionDirty, "a change marks the session");
        window.AskSaveChanges = _ => SaveAnswer.Cancel;
        Expect(!await window.NewSessionAsync() && window.Tabs.Items.Count == count, "cancelling the question keeps everything");
        window.AskSaveChanges = _ => SaveAnswer.Save;
        window.RenameTab(window.Tabs.Items[0].Id, "Awal");
        names[0] = "Awal";
        Expect(window.SaveSession() && !window.SessionDirty, "saving clears the mark");

        // ---- new session ----
        window.AskSaveChanges = _ => SaveAnswer.Discard;
        Expect(await window.NewSessionAsync(), "a new session");
        Expect(window.Tabs.Items.Count == 0 && _engine.Active is null && window.NoLive && window.SessionPath is null, "no tab is left and the test pattern takes over");
        Expect(Directory.Exists(Path.Combine(window.ProfilesDirectory, "tab-" + profile)), "closing for a session keeps the login folder");

        // ---- open it again here ----
        Expect(await window.OpenSessionAsync(sessionPath), "the session opens");
        Expect(openAsked is null && relinkAsked == 0, "with no tabs open and every file in place nothing is asked");
        Expect(window.Tabs.Items.Select(t => t.Name).SequenceEqual(names), $"the tabs are back with their names ({string.Join(", ", window.Tabs.Items.Select(t => t.Name))})");
        Expect(window.Tabs.Items[1].ProfileId == profile, "and the web tab is on its own profile again");
        Expect(await WaitUntilAsync(() => Doc(2) is { Pdf.PageCount: 3 } && Doc(3) is { Pdf.PageCount: 1 }), "the PDFs are open again");
        if (ppt) Expect(await WaitUntilAsync(() => Doc(4) is { PowerPoint.PageCount: 5 }, 25000), "and the deck is open again");
        Expect(window.Tabs.ActiveIndex == 2 && window.NoLive, "the live tab is selected but nothing goes on air by itself");
        Expect(window.SessionPath == sessionPath && !window.SessionDirty && window.MissingDocumentCount == 0, "the session is the open one, unchanged, nothing missing");

        // ---- add to the tabs that exist ----
        string extra = Path.Combine(root, "tambahan.stagecast");
        SessionStore.Save(extra, new SessionFile
        {
            Tabs = [new SessionTab { Name = "Web ekstra", Url = TabRules.BlankUrl, ProfileId = profile }, new SessionTab { Name = "PDF ekstra", Kind = "pdf", Url = TabRules.BlankUrl, Source = b }],
        });
        replace = false;
        Expect(await window.OpenSessionAsync(extra), "a session can be added to the tabs that exist");
        Expect(openAsked == (count, 2) && window.Tabs.Items.Count == count + 2, $"the question knew the counts and the tabs were added ({window.Tabs.Items.Count})");
        Expect(window.Tabs.Items[1].ProfileId == profile && window.Tabs.Items[count].ProfileId is null, "a profile in use is not given to a second tab");
        Expect(window.SessionDirty && window.SessionPath == sessionPath, "adding changes the open session");
        replace = true;
        Expect(await window.OpenSessionAsync(sessionPath) && window.Tabs.Items.Count == count && !window.SessionDirty, "replacing goes back to the saved tabs");
        if (ppt) Expect(await WaitUntilAsync(() => Doc(4) is { PowerPoint.PageCount: 5 }, 25000), "with the deck open");

        // ---- another computer: the folder moved, b.pdf left behind ----
        Expect(await window.NewSessionAsync(), "a new session before moving");
        await Task.Delay(500);
        Directory.Move(home, other);
        File.Move(Path.Combine(other, "b.pdf"), Path.Combine(spare, "b.pdf"));
        string moved = Path.Combine(other, "Misa.stagecast");
        Expect(await window.OpenSessionAsync(moved), "the moved session opens");
        Expect(relinkAsked == 1 && lastPlan is { MissingCount: 1 }, "one document is asked about");
        Expect(lastPlan?.Tabs[2].Match == SourceMatch.RelativeToSession && lastPlan.Tabs[3].IsMissing, "a.pdf was found through the path relative to the session file, b.pdf was not");
        if (ppt) Expect(lastPlan?.Tabs[4].Match == SourceMatch.RelativeToSession && await WaitUntilAsync(() => Doc(4) is { PowerPoint.PageCount: 5 }, 25000), "the deck was found the same way and plays");
        Expect(window.Tabs.Items[2].Source == Path.Combine(other, "a.pdf") && await WaitUntilAsync(() => Doc(2) is { Pdf.PageCount: 3 }), "and a.pdf plays from its new place");
        Expect(window.MissingDocumentCount == 1 && Doc(3) is { Pdf: null or { PageCount: 0 } }, "the tab for b.pdf exists without a file, and the banner counts it");
        resolver = (plan, files) => plan.WithFolder(spare, files);
        Expect(await window.RelinkMissingAsync() == 1, "choosing a folder later fills it in");
        Expect(window.MissingDocumentCount == 0 && window.Tabs.Items[3].Source == Path.Combine(spare, "b.pdf") && await WaitUntilAsync(() => Doc(3) is { Pdf.PageCount: 1 }), "and b.pdf plays");

        // ---- a session that points at the network ----
        string netPath = Path.Combine(root, "jaringan.stagecast");
        SessionStore.Save(netPath, new SessionFile { Tabs = [new SessionTab { Name = "Jauh", Kind = "pdf", Url = TabRules.BlankUrl, Source = @"\\selftest-nohost\share\x.pdf" }] });
        Expect(await window.NewSessionAsync(), "a new session before the network one");
        asked.Clear();
        window.Confirm = text => { asked.Add(text); return false; };
        resolver = (plan, _) => plan;
        relinkAsked = 0;
        var started = DateTime.UtcNow;
        Expect(await window.OpenSessionAsync(netPath), "the network session opens");
        Expect(asked.Count == 1 && asked[0].Contains(@"\\selftest-nohost\share\x.pdf"), "the operator is asked first, with the path");
        Expect(relinkAsked == 1 && lastPlan is { MissingCount: 1 } && window.MissingDocumentCount == 1, "declined, the document counts as missing without being looked for");
        Expect(DateTime.UtcNow - started < TimeSpan.FromSeconds(15), "and nothing waited for a server");
        window.Confirm = _ => true;

        // ---- the resolution of the session ----
        string sized = Path.Combine(root, "ukuran.stagecast");
        var current = _engine.Resolution;
        int fps = _engine.Fps == 30 ? 60 : 30;
        SessionStore.Save(sized, new SessionFile { Render = new RenderConfig { Width = 1280, Height = 720, Fps = fps } });
        Expect(await window.NewSessionAsync(), "a new session before the sized one");
        Expect(await window.OpenSessionAsync(sized), "a session with a render size opens");
        Expect(_engine.Resolution == new Resolution(1280, 720) && _engine.Fps == fps, $"and the size is applied ({_engine.Resolution.Width}x{_engine.Resolution.Height} @ {_engine.Fps})");
        window.ApplyFromCode(current, fps == 30 ? 60 : 30);

        // ---- a file that is not a session ----
        string junk = Path.Combine(root, "rusak.stagecast");
        File.WriteAllText(junk, "bukan sesi");
        int before = messages.Count;
        Expect(!await window.OpenSessionAsync(junk) && messages.Count == before + 1 && File.ReadAllText(junk) == "bukan sesi", "a file that is not a session is reported and left alone");
        Expect(!await window.OpenSessionAsync(Path.Combine(root, "tidak-ada.stagecast")) && messages.Count == before + 2, "and so is a missing one");

        _logger.LogInformation(_failures.Count == 0 ? "F1 selftest ok" : "F1 selftest FAILED: {Failures}", string.Join("; ", _failures));
    }
}
