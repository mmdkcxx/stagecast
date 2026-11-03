using Stagecast.Core.Configuration;
using Stagecast.Core.Sessions;
using Stagecast.Core.Tabs;

namespace Stagecast.Core.Tests;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stagecast-tests-" + Guid.NewGuid().ToString("N"));

    private string SessionPath => Path.Combine(_dir, "Misa" + SessionFile.Extension);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private SessionLoadResult LoadRaw(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SessionPath, json);
        return SessionStore.Load(SessionPath);
    }

    private static string Id(char c) => new(c, 32);

    [Fact]
    public void SaveThenLoad_KeepsEverything()
    {
        var session = new SessionFile
        {
            Render = new RenderConfig { Width = 1920, Height = 1080, Fps = 30 },
            PreviewMode = true,
            ActiveTab = 1,
            Tabs =
            [
                new SessionTab { Name = "Web", Url = "https://example.com/a", ProfileId = Id('a') },
                new SessionTab { Name = "Deck", Kind = "powerpoint", Url = "about:blank", Source = @"D:\Gereja\Deck.pptx", RelativeSource = @"..\Gereja\Deck.pptx" },
            ],
        };
        SessionStore.Save(SessionPath, session);
        var result = SessionStore.Load(SessionPath);

        Assert.Equal(SessionLoadStatus.Loaded, result.Status);
        var loaded = result.Session!;
        Assert.Equal((1920, 1080, 30), (loaded.Render!.Width, loaded.Render.Height, loaded.Render.Fps));
        Assert.True(loaded.PreviewMode);
        Assert.Equal(1, loaded.ActiveTab);
        Assert.Equal("https://example.com/a", loaded.Tabs[0].Url);
        Assert.Equal(Id('a'), loaded.Tabs[0].ProfileId);
        Assert.Equal(@"D:\Gereja\Deck.pptx", loaded.Tabs[1].Source);
        Assert.Equal(@"..\Gereja\Deck.pptx", loaded.Tabs[1].RelativeSource);
        Assert.False(File.Exists(SessionPath + ".tmp"));
    }

    [Fact]
    public void SaveOverAnExistingFile_ReplacesIt()
    {
        SessionStore.Save(SessionPath, new SessionFile { Tabs = [new SessionTab { Name = "Satu" }] });
        SessionStore.Save(SessionPath, new SessionFile { Tabs = [new SessionTab { Name = "Dua" }] });
        Assert.Equal("Dua", SessionStore.Load(SessionPath).Session!.Tabs.Single().Name);
    }

    [Fact]
    public void ANeededFolderIsCreatedOnSave()
    {
        string nested = Path.Combine(_dir, "a", "b", "s" + SessionFile.Extension);
        SessionStore.Save(nested, new SessionFile());
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void NoFile_IsNotFound()
        => Assert.Equal(SessionLoadStatus.NotFound, SessionStore.Load(Path.Combine(_dir, "nope.stagecast")).Status);

    [Theory]
    [InlineData("this is not json")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("[1, 2]")]
    public void AFileThatIsNotASession_IsUnreadable_AndLeftAlone(string content)
    {
        var result = LoadRaw(content);
        Assert.Equal(SessionLoadStatus.Unreadable, result.Status);
        Assert.False(result.Ok);
        Assert.Equal(content, File.ReadAllText(SessionPath)); // never moved or rewritten
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void ATooLargeFile_IsUnreadable()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SessionPath, new string(' ', (int)SessionStore.MaxFileBytes + 1));
        Assert.Equal(SessionLoadStatus.Unreadable, SessionStore.Load(SessionPath).Status);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<b>x</b>")]
    [InlineData("ftp://example.com/x")]
    [InlineData("not an address")]
    public void AWebAddressThatIsNotHttp_OpensBlank(string url)
    {
        var result = LoadRaw($$"""{ "tabs": [ { "name": "A", "url": {{System.Text.Json.JsonSerializer.Serialize(url)}} } ] }""");
        Assert.Equal(SessionLoadStatus.Repaired, result.Status);
        Assert.Equal(TabRules.BlankUrl, result.Session!.Tabs.Single().Url);
    }

    [Fact]
    public void HttpAndHttpsAndBlank_AreKept()
    {
        var result = LoadRaw("""{ "tabs": [ { "name": "A", "url": "http://a.test/" }, { "name": "B", "url": " https://b.test/x?y=1 " }, { "name": "C", "url": "" } ] }""");
        Assert.Equal(SessionLoadStatus.Loaded, result.Status);
        Assert.Equal(["http://a.test/", "https://b.test/x?y=1", TabRules.BlankUrl], result.Session!.Tabs.Select(t => t.Url));
    }

    [Fact]
    public void ProfileIds_MustBeValidAndUnique()
    {
        var result = LoadRaw($$"""{ "tabs": [ { "name": "A", "profileId": "{{Id('a')}}" }, { "name": "B", "profileId": "{{Id('a')}}" }, { "name": "C", "profileId": "..\\..\\evil" } ] }""");
        Assert.Equal(SessionLoadStatus.Repaired, result.Status);
        Assert.Equal(new string?[] { Id('a'), null, null }, result.Session!.Tabs.Select(t => t.ProfileId));
    }

    [Fact]
    public void ADocumentTab_HasNoAddressAndNoProfile_AndARootedRelativePathIsDropped()
    {
        var result = LoadRaw($$"""{ "tabs": [ { "name": "D", "kind": "pdf", "url": "https://x.test", "profileId": "{{Id('b')}}", "source": "C:\\a.pdf", "relativeSource": "C:\\evil.pdf" } ] }""");
        var tab = result.Session!.Tabs.Single();
        Assert.Equal(("pdf", TabRules.BlankUrl, @"C:\a.pdf"), (tab.Kind, tab.Url, tab.Source));
        Assert.Null(tab.ProfileId);
        Assert.Null(tab.RelativeSource);
    }

    [Fact]
    public void APathWithANullCharacter_IsDropped()
    {
        var result = LoadRaw("""{ "tabs": [ { "name": "D", "kind": "pdf", "source": "C:\\a\u0000.pdf", "relativeSource": "a\u0000.pdf" } ] }""");
        var tab = result.Session!.Tabs.Single();
        Assert.Null(tab.Source);
        Assert.Null(tab.RelativeSource);
    }

    [Fact]
    public void NamesAreRepaired()
    {
        string longName = new('x', 60);
        var result = LoadRaw($$"""{ "tabs": [ { "name": "" }, { "name": "  " }, { "name": "{{longName}}" } ] }""");
        Assert.Equal(["Tab 1", "Tab 2", new string('x', TabRules.MaxNameLength)], result.Session!.Tabs.Select(t => t.Name));
    }

    [Fact]
    public void AnUnknownKind_DropsThatTab_AndTheActiveTabStillPointsAtItsOwn()
    {
        var result = LoadRaw("""{ "activeTab": 2, "tabs": [ { "name": "A" }, { "name": "B", "kind": "hologram" }, { "name": "C" } ] }""");
        var session = result.Session!;
        Assert.Equal(["A", "C"], session.Tabs.Select(t => t.Name));
        Assert.Equal(1, session.ActiveTab);
    }

    [Fact]
    public void ADroppedActiveTab_FallsBackToTheTabBeforeIt()
    {
        var result = LoadRaw("""{ "activeTab": 1, "tabs": [ { "name": "A" }, { "name": "B", "kind": "hologram" }, { "name": "C" } ] }""");
        Assert.Equal(0, result.Session!.ActiveTab);
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(99, 1)]
    public void TheActiveTab_StaysInsideTheList(int active, int expected)
    {
        var result = LoadRaw($$"""{ "activeTab": {{active}}, "tabs": [ { "name": "A" }, { "name": "B" } ] }""");
        Assert.Equal(expected, result.Session!.ActiveTab);
    }

    [Fact]
    public void MoreThanTheLimit_IsCut()
    {
        string tabs = string.Join(",", Enumerable.Range(1, TabRules.MaxTabs + 4).Select(i => $$"""{ "name": "T{{i}}" }"""));
        var result = LoadRaw($$"""{ "tabs": [ {{tabs}} ] }""");
        Assert.Equal(SessionLoadStatus.Repaired, result.Status);
        Assert.Equal(TabRules.MaxTabs, result.Session!.Tabs.Count);
    }

    [Theory]
    [InlineData(1920, 1080, 30, true)]
    [InlineData(2520, 1080, 60, true)]
    [InlineData(1920, 1080, 25, false)]
    [InlineData(10, 1080, 60, false)]
    [InlineData(1920, 99999, 60, false)]
    public void TheRenderSize_IsKeptOnlyWhenUsable(int width, int height, int fps, bool kept)
    {
        var result = LoadRaw($$"""{ "render": { "width": {{width}}, "height": {{height}}, "fps": {{fps}} } }""");
        Assert.Equal(kept, result.Session!.Render is not null);
        Assert.Equal(kept ? SessionLoadStatus.Loaded : SessionLoadStatus.Repaired, result.Status);
    }

    [Fact]
    public void NoRenderSection_LeavesTheRenderSizeAlone()
    {
        var result = LoadRaw("""{ "tabs": [] }""");
        Assert.Null(result.Session!.Render);
        Assert.Equal(SessionLoadStatus.Loaded, result.Status);
    }

    [Fact]
    public void ANewerSchema_StillLoads_WithANote()
    {
        var result = LoadRaw("""{ "schemaVersion": 99, "tabs": [ { "name": "A" } ], "somethingNew": true }""");
        Assert.Equal(SessionLoadStatus.Repaired, result.Status);
        Assert.Single(result.Session!.Tabs);
        Assert.Contains(result.Notes, n => n.Contains("newer"));
    }
}

public sealed class SessionPlanTests
{
    private sealed class FakeFiles(params string[] existing) : ISessionFiles
    {
        private readonly HashSet<string> _files = new(existing, StringComparer.OrdinalIgnoreCase);

        public bool Exists(string path) => _files.Contains(path);

        public string? FindByName(string folder, string fileName)
            => _files.FirstOrDefault(f => f.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    private const string SessionPath = @"C:\Sesi\Misa.stagecast";

    private static SessionFile Session(params SessionTab[] tabs) => new() { Tabs = [.. tabs] };

    private static SessionTab Doc(string name, string kind, string? source, string? relative = null)
        => new() { Name = name, Kind = kind, Url = TabRules.BlankUrl, Source = source, RelativeSource = relative };

    [Fact]
    public void AFileWhereItWasSaved_IsFoundAsOriginal()
    {
        var plan = SessionPlan.Create(Session(Doc("A", "pdf", @"D:\G\a.pdf")), SessionPath, new FakeFiles(@"D:\G\a.pdf"));
        Assert.Equal((SourceMatch.Original, @"D:\G\a.pdf"), (plan.Tabs[0].Match, plan.Tabs[0].FoundPath));
        Assert.Equal(0, plan.MissingCount);
    }

    [Fact]
    public void TheRelativePath_IsTriedWhenTheOriginalIsGone()
    {
        var plan = SessionPlan.Create(Session(Doc("A", "pdf", @"D:\G\a.pdf", @"dokumen\a.pdf")), SessionPath, new FakeFiles(@"C:\Sesi\dokumen\a.pdf"));
        Assert.Equal((SourceMatch.RelativeToSession, @"C:\Sesi\dokumen\a.pdf"), (plan.Tabs[0].Match, plan.Tabs[0].FoundPath));
        Assert.Equal(@"C:\Sesi\dokumen\a.pdf", plan.Tabs[0].Info.Source);
    }

    [Fact]
    public void ARelativePathWithDots_IsResolvedAgainstTheSessionFolder()
    {
        var plan = SessionPlan.Create(Session(Doc("A", "pdf", @"D:\G\a.pdf", @"..\Bersama\a.pdf")), SessionPath, new FakeFiles(@"C:\Bersama\a.pdf"));
        Assert.Equal((SourceMatch.RelativeToSession, @"C:\Bersama\a.pdf"), (plan.Tabs[0].Match, plan.Tabs[0].FoundPath));
    }

    [Fact]
    public void TheNameBesideTheSessionFile_IsTheLastTry()
    {
        var plan = SessionPlan.Create(Session(Doc("A", "pdf", @"D:\G\a.pdf", @"x\a.pdf")), SessionPath, new FakeFiles(@"C:\Sesi\a.pdf"));
        Assert.Equal((SourceMatch.NextToSession, @"C:\Sesi\a.pdf"), (plan.Tabs[0].Match, plan.Tabs[0].FoundPath));
    }

    [Fact]
    public void TheOriginalBeatsTheOthers()
    {
        var plan = SessionPlan.Create(Session(Doc("A", "pdf", @"D:\G\a.pdf", @"a.pdf")), SessionPath, new FakeFiles(@"D:\G\a.pdf", @"C:\Sesi\a.pdf"));
        Assert.Equal(SourceMatch.Original, plan.Tabs[0].Match);
    }

    [Fact]
    public void AMissingFile_KeepsItsOldPath_SoTheTabCanSayFileNotFound()
    {
        var plan = SessionPlan.Create(Session(Doc("A", "powerpoint", @"D:\G\a.pptx")), SessionPath, new FakeFiles());
        var tab = plan.Tabs[0];
        Assert.True(tab.IsMissing);
        Assert.Null(tab.FoundPath);
        Assert.Equal(@"D:\G\a.pptx", tab.Info.Source);
        Assert.Equal(TabKind.PowerPoint, tab.Info.Kind);
        Assert.Equal(1, plan.MissingCount);
    }

    [Fact]
    public void WebTabs_AndFileTabsWithoutAFile_AreNeverMissing()
    {
        var plan = SessionPlan.Create(Session(new SessionTab { Name = "W", Url = "https://x.test/" }, Doc("Kosong", "pdf", null)), SessionPath, new FakeFiles());
        Assert.All(plan.Tabs, t => Assert.Equal(SourceMatch.NotNeeded, t.Match));
        Assert.Equal(0, plan.MissingCount);
        Assert.Equal("https://x.test/", plan.Tabs[0].Info.Url);
        Assert.Null(plan.Tabs[1].Info.Source);
    }

    [Fact]
    public void EveryTabGetsItsOwnId()
    {
        var plan = SessionPlan.Create(Session(new SessionTab { Name = "A" }, new SessionTab { Name = "B" }), SessionPath, new FakeFiles());
        Assert.NotEqual(plan.Tabs[0].Info.Id, plan.Tabs[1].Info.Id);
        Assert.NotEqual(Guid.Empty, plan.Tabs[0].Info.Id);
    }

    [Fact]
    public void AFolder_FillsInTheMissingFilesByName_AndLeavesTheOthers()
    {
        var files = new FakeFiles(@"D:\G\a.pdf", @"E:\Baru\b.pptx", @"E:\Baru\sub\c.pdf");
        var plan = SessionPlan.Create(Session(
            Doc("A", "pdf", @"D:\G\a.pdf"), Doc("B", "powerpoint", @"X:\lama\b.pptx"), Doc("C", "pdf", @"X:\lama\c.pdf"), Doc("D", "pdf", @"X:\lama\d.pdf")), SessionPath, files);
        Assert.Equal(3, plan.MissingCount);

        var next = plan.WithFolder(@"E:\Baru", files);
        Assert.Equal([SourceMatch.Original, SourceMatch.ChosenFolder, SourceMatch.ChosenFolder, SourceMatch.Missing], next.Tabs.Select(t => t.Match));
        Assert.Equal(@"E:\Baru\b.pptx", next.Tabs[1].Info.Source);
        Assert.Equal(@"E:\Baru\sub\c.pdf", next.Tabs[2].Info.Source);
        Assert.Equal(@"X:\lama\d.pdf", next.Tabs[3].Info.Source);
        Assert.Equal(1, next.MissingCount);
        Assert.Equal(3, plan.MissingCount); // the old plan is unchanged
    }

    [Fact]
    public void ChoosingOneFile_AlsoSearchesItsFolderForTheOthers()
    {
        var files = new FakeFiles(@"E:\Baru\b.pptx", @"E:\Baru\c.pdf", @"F:\Lain\d.pdf");
        var plan = SessionPlan.Create(Session(
            Doc("B", "powerpoint", @"X:\lama\b.pptx"), Doc("C", "pdf", @"X:\lama\c.pdf"), Doc("D", "pdf", @"X:\lama\d.pdf")), SessionPath, files);

        var next = plan.WithChosenFile(0, @"E:\Baru\b.pptx", files);
        Assert.Equal([SourceMatch.Chosen, SourceMatch.ChosenFolder, SourceMatch.Missing], next.Tabs.Select(t => t.Match));
        Assert.Equal(1, next.MissingCount);

        var done = next.WithChosenFile(2, @"F:\Lain\d.pdf", files);
        Assert.Equal(0, done.MissingCount);
    }

    [Fact]
    public void ChoosingAFileThatIsNotThere_ChangesNothing()
    {
        var files = new FakeFiles();
        var plan = SessionPlan.Create(Session(Doc("A", "pdf", @"X:\a.pdf")), SessionPath, files);
        Assert.Same(plan, plan.WithChosenFile(0, @"E:\nope.pdf", files));
        Assert.Same(plan, plan.WithChosenFile(5, @"E:\nope.pdf", files));
    }

    [Fact]
    public void AChosenFile_MayHaveAnotherName()
    {
        var files = new FakeFiles(@"E:\Baru\versi-2.pdf");
        var plan = SessionPlan.Create(Session(Doc("A", "pdf", @"X:\a.pdf")), SessionPath, files);
        var next = plan.WithChosenFile(0, @"E:\Baru\versi-2.pdf", files);
        Assert.Equal(@"E:\Baru\versi-2.pdf", next.Tabs[0].Info.Source);
        Assert.False(next.Tabs[0].IsMissing);
    }

    [Fact]
    public void AProfileIsKeptOnlyWhereItsFolderExists()
    {
        string a = new('a', 32), b = new('b', 32);
        var plan = SessionPlan.Create(Session(
            new SessionTab { Name = "A", Url = "https://a.test/", ProfileId = a },
            new SessionTab { Name = "B", Url = "https://b.test/", ProfileId = b },
            new SessionTab { Name = "C", Url = "https://c.test/" }), SessionPath, new FakeFiles());

        var tabs = plan.ToTabInfos(id => id == a);
        Assert.Equal(new string?[] { a, null, null }, tabs.Select(t => t.ProfileId));
        Assert.Equal(b, plan.Tabs[1].Info.ProfileId); // the plan itself is not changed
    }

    [Fact]
    public void ThePlanCarriesTheRenderSizeAndMode()
    {
        var session = new SessionFile { Render = new RenderConfig { Width = 1920, Height = 1080, Fps = 30 }, PreviewMode = true, ActiveTab = 1, Tabs = [new SessionTab { Name = "A" }, new SessionTab { Name = "B" }] };
        var plan = SessionPlan.Create(session, SessionPath, new FakeFiles());
        Assert.Equal(30, plan.Render!.Fps);
        Assert.True(plan.PreviewMode);
        Assert.Equal(1, plan.ActiveTab);
    }
}

public sealed class SessionBuilderTests
{
    private static readonly RenderConfig Render = new() { Width = 1920, Height = 1080, Fps = 30 };

    [Fact]
    public void WebTabs_KeepAddressAndProfile_DocumentsKeepTheirPath()
    {
        string profile = new('c', 32);
        var tabs = new[]
        {
            new TabInfo(Guid.NewGuid(), "Web", "https://x.test/", profile),
            new TabInfo(Guid.NewGuid(), "Deck", TabRules.BlankUrl, null, TabKind.PowerPoint, @"C:\Sesi\dokumen\deck.pptx"),
        };
        var session = SessionBuilder.Create(tabs, 1, previewMode: true, Render, @"C:\Sesi\Misa.stagecast");

        Assert.Equal(("https://x.test/", profile), (session.Tabs[0].Url, session.Tabs[0].ProfileId));
        Assert.Equal(("powerpoint", @"C:\Sesi\dokumen\deck.pptx", @"dokumen\deck.pptx"), (session.Tabs[1].Kind, session.Tabs[1].Source, session.Tabs[1].RelativeSource));
        Assert.True(session.PreviewMode);
        Assert.Equal(1, session.ActiveTab);
        Assert.Equal(1920, session.Render!.Width);
    }

    [Fact]
    public void ADocumentOnAnotherDrive_HasNoRelativePath()
    {
        var tabs = new[] { new TabInfo(Guid.NewGuid(), "Deck", TabRules.BlankUrl, null, TabKind.Pdf, @"D:\Gereja\a.pdf") };
        var session = SessionBuilder.Create(tabs, 0, false, Render, @"C:\Sesi\Misa.stagecast");
        Assert.Null(session.Tabs[0].RelativeSource);
        Assert.Equal(@"D:\Gereja\a.pdf", session.Tabs[0].Source);
    }

    [Fact]
    public void ADocumentAboveTheSessionFolder_GetsADotDotPath()
    {
        var tabs = new[] { new TabInfo(Guid.NewGuid(), "Deck", TabRules.BlankUrl, null, TabKind.Pdf, @"C:\Bersama\a.pdf") };
        var session = SessionBuilder.Create(tabs, 0, false, Render, @"C:\Sesi\Misa.stagecast");
        Assert.Equal(@"..\Bersama\a.pdf", session.Tabs[0].RelativeSource);
    }

    [Fact]
    public void ADocumentTabWithoutAFile_StaysEmpty_AndTheActiveIndexIsClamped()
    {
        var tabs = new[] { new TabInfo(Guid.NewGuid(), "Kosong", TabRules.BlankUrl, null, TabKind.Pdf, null) };
        var session = SessionBuilder.Create(tabs, 7, false, Render, @"C:\Sesi\Misa.stagecast");
        Assert.Null(session.Tabs[0].Source);
        Assert.Null(session.Tabs[0].RelativeSource);
        Assert.Equal(0, session.ActiveTab);
    }

    [Fact]
    public void NoTabs_MakeAValidEmptySession()
    {
        var session = SessionBuilder.Create([], -1, false, Render, @"C:\Sesi\Misa.stagecast");
        Assert.Empty(session.Tabs);
        Assert.Equal(0, session.ActiveTab);
    }

    [Fact]
    public void WhatTheBuilderMakes_SurvivesTheLoaderUnchanged()
    {
        string path = Path.Combine(Path.GetTempPath(), "stagecast-tests-" + Guid.NewGuid().ToString("N"), "s" + SessionFile.Extension);
        try
        {
            string folder = Path.GetDirectoryName(path)!;
            var tabs = new[]
            {
                new TabInfo(Guid.NewGuid(), "Web", "https://x.test/", new string('d', 32)),
                new TabInfo(Guid.NewGuid(), "Deck", TabRules.BlankUrl, null, TabKind.Pdf, Path.Combine(folder, "a.pdf")),
            };
            SessionStore.Save(path, SessionBuilder.Create(tabs, 1, true, Render, path));
            var result = SessionStore.Load(path);

            Assert.Equal(SessionLoadStatus.Loaded, result.Status);
            Assert.Equal(["Web", "Deck"], result.Session!.Tabs.Select(t => t.Name));
            Assert.Equal("a.pdf", result.Session.Tabs[1].RelativeSource);
        }
        finally
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}

public sealed class DiskFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stagecast-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Touch(params string[] parts)
    {
        string path = Path.Combine([_dir, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void TheFolderItselfComesFirst_ThenFoldersBelowItDownToTheLimit()
    {
        string top = Touch("a.pdf"), below = Touch("sub", "b.pdf"), deep = Touch("s1", "s2", "c.pdf"), tooDeep = Touch("s1", "s2", "s3", "d.pdf");
        Touch("sub", "a.pdf");
        var files = new DiskFiles();

        Assert.Equal(top, files.FindByName(_dir, "a.pdf"));
        Assert.Equal(below, files.FindByName(_dir, "b.pdf"));
        Assert.Equal(deep, files.FindByName(_dir, "c.pdf"));
        Assert.Null(files.FindByName(_dir, "d.pdf"));
        Assert.True(File.Exists(tooDeep));
    }

    [Fact]
    public void ANameThatIsNotThere_AndAFolderThatIsNotThere_GiveNull()
    {
        Directory.CreateDirectory(_dir);
        var files = new DiskFiles();
        Assert.Null(files.FindByName(_dir, "nope.pdf"));
        Assert.Null(files.FindByName(Path.Combine(_dir, "nofolder"), "a.pdf"));
        Assert.False(files.Exists(Path.Combine(_dir, "nope.pdf")));
        Assert.False(files.Exists("bad\0path"));
    }
}

public sealed class NetworkGuardTests
{
    private sealed class Probe : ISessionFiles
    {
        public List<string> Asked = [];

        public bool Answer = true;

        public bool Exists(string path)
        {
            Asked.Add(path);
            return Answer;
        }

        public string? FindByName(string folder, string fileName)
        {
            Asked.Add(folder);
            return Path.Combine(folder, fileName);
        }
    }

    [Theory]
    [InlineData(@"\\server\share\a.pdf", true)]
    [InlineData("//server/share/a.pdf", true)]
    [InlineData(@"\\?\UNC\server\share\a.pdf", true)]
    [InlineData(@"\\?\C:\Gereja\a.pdf", false)]
    [InlineData(@"\\.\C:\a.pdf", false)]
    [InlineData(@"C:\Gereja\a.pdf", false)]
    [InlineData(@"relatif\a.pdf", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void NetworkPaths_AreRecognised(string? path, bool network)
        => Assert.Equal(network, NetworkGuard.IsNetworkPath(path));

    [Fact]
    public void WithoutPermission_ANetworkPathIsNeverAskedOfTheDisk()
    {
        var probe = new Probe();
        var guard = new NetworkGuard(probe, allowNetwork: false);

        Assert.False(guard.Exists(@"\\server\share\a.pdf"));
        Assert.Null(guard.FindByName(@"\\server\share", "a.pdf"));
        Assert.Empty(probe.Asked);

        Assert.True(guard.Exists(@"C:\a.pdf"));
        Assert.Equal(@"C:\Dok\a.pdf", guard.FindByName(@"C:\Dok", "a.pdf"));
        Assert.Equal(2, probe.Asked.Count);
    }

    [Fact]
    public void WithPermission_EverythingGoesThrough()
    {
        var probe = new Probe();
        var guard = new NetworkGuard(probe, allowNetwork: true);
        Assert.True(guard.Exists(@"\\server\share\a.pdf"));
        Assert.Single(probe.Asked);
    }

    [Fact]
    public void ANetworkDocumentInAPlan_StaysMissing_WithoutTheDiskBeingAsked()
    {
        var probe = new Probe { Answer = false };
        var session = new SessionFile { Tabs = [new SessionTab { Name = "A", Kind = "pdf", Url = TabRules.BlankUrl, Source = @"\\evil\share\a.pdf" }] };
        var plan = SessionPlan.Create(session, @"C:\Sesi\Misa.stagecast", new NetworkGuard(probe, false));
        Assert.True(plan.Tabs[0].IsMissing);
        Assert.Empty(probe.Asked.Where(p => p.StartsWith(@"\\evil")));
        Assert.Equal([@"\\evil\share\a.pdf"], SessionPlan.NetworkSources(session));
    }

    [Fact]
    public void NetworkSources_ListsEachOnceAndSkipsLocalAndWebTabs()
    {
        var session = new SessionFile
        {
            Tabs =
            [
                new SessionTab { Name = "A", Kind = "pdf", Source = @"\\s\a.pdf" },
                new SessionTab { Name = "B", Kind = "pdf", Source = @"\\S\A.pdf" },
                new SessionTab { Name = "C", Kind = "pdf", Source = @"C:\a.pdf" },
                new SessionTab { Name = "D" },
            ],
        };
        Assert.Single(SessionPlan.NetworkSources(session));
    }
}

public sealed class RecentSessionsConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stagecast-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void TheRecentList_RoundTrips_AndIsRepairedOnLoad()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "config.json");
        var many = Enumerable.Range(1, 12).Select(i => $@"C:\Sesi\s{i}.stagecast").ToList();
        var store = new ConfigStore(path, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        store.Save(new AppConfig { RecentSessions = [@"C:\Sesi\a.stagecast", "", @"C:\SESI\A.stagecast", "bad\0path", .. many] });

        var loaded = store.Load().Config.RecentSessions;
        Assert.Equal(AppConfig.MaxRecentSessions, loaded.Count);
        Assert.Equal(@"C:\Sesi\a.stagecast", loaded[0]);
        Assert.Equal(loaded.Count, loaded.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(loaded, p => p.Length == 0 || p.Contains('\0'));
    }

    [Fact]
    public void AFileWithoutTheList_LoadsWithAnEmptyOne()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, """{ "schemaVersion": 1 }""");
        var result = new ConfigStore(path, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).Load();
        Assert.Equal(ConfigLoadStatus.Loaded, result.Status);
        Assert.Empty(result.Config.RecentSessions);
    }
}
