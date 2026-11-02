using Stagecast.Core.Configuration;
using Stagecast.Core.Tabs;

namespace Stagecast.Core.Sessions;

public enum SourceMatch
{
    /// <summary>A web tab, or a document tab with no file chosen: there is nothing to find.</summary>
    NotNeeded,

    /// <summary>The file is where the session says it was.</summary>
    Original,

    /// <summary>Found by the path relative to the session file.</summary>
    RelativeToSession,

    /// <summary>Found by its name beside the session file.</summary>
    NextToSession,

    /// <summary>Found by its name in a folder the operator pointed at.</summary>
    ChosenFolder,

    /// <summary>The operator chose this file for the tab.</summary>
    Chosen,

    /// <summary>Not found; the operator is asked.</summary>
    Missing,
}

/// <param name="Info">The tab as it will be created. For a missing file its source is the old path, so the tab says "file not found" and the operator can choose another.</param>
public sealed record PlannedTab(TabInfo Info, SessionTab Saved, string? FoundPath, SourceMatch Match)
{
    public bool IsMissing => Match == SourceMatch.Missing;
}

/// <summary>
/// What opening a session would create, before anything is opened: the tabs with the documents found so far. The operator resolves
/// what is missing through <see cref="WithFolder"/> and <see cref="WithChosenFile"/>; each returns a new plan.
/// </summary>
public sealed class SessionPlan
{
    private SessionPlan(SessionFile session, string sessionFolder, IReadOnlyList<PlannedTab> tabs)
    {
        Session = session;
        SessionFolder = sessionFolder;
        Tabs = tabs;
    }

    public SessionFile Session { get; }

    /// <summary>The folder of the session file; documents are looked for beside it.</summary>
    public string SessionFolder { get; }

    public IReadOnlyList<PlannedTab> Tabs { get; }

    public int MissingCount => Tabs.Count(t => t.IsMissing);

    public IReadOnlyList<PlannedTab> Missing => [.. Tabs.Where(t => t.IsMissing)];

    /// <summary>The render size to apply, or null to keep the current one.</summary>
    public RenderConfig? Render => Session.Render;

    public bool PreviewMode => Session.PreviewMode;

    public int ActiveTab => Session.ActiveTab;

    /// <summary>The tabs to create. Web tabs keep their separate profile only when <paramref name="profileExists"/> says its folder is on this computer.</summary>
    public IReadOnlyList<TabInfo> ToTabInfos(Func<string, bool> profileExists)
        => [.. Tabs.Select(t => t.Info.ProfileId is { } id && !profileExists(id) ? t.Info with { ProfileId = null } : t.Info)];

    /// <summary>The document paths of <paramref name="session"/> that lie on the network, which are not probed without the operator's say-so (see <see cref="NetworkGuard"/>).</summary>
    public static IReadOnlyList<string> NetworkSources(SessionFile session)
        => [.. session.Tabs.Select(t => t.Source).Where(NetworkGuard.IsNetworkPath).Select(s => s!).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The tabs of <paramref name="session"/> (as <see cref="SessionStore.Load"/> returns it) with each document looked for on the disk.</summary>
    public static SessionPlan Create(SessionFile session, string sessionPath, ISessionFiles files)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(sessionPath))!;
        var tabs = new List<PlannedTab>();
        foreach (var saved in session.Tabs)
        {
            TabKinds.TryParse(saved.Kind, out var kind);
            if (!TabKinds.IsDocument(kind))
            {
                tabs.Add(new PlannedTab(new TabInfo(Guid.NewGuid(), saved.Name, saved.Url, saved.ProfileId), saved, null, SourceMatch.NotNeeded));
                continue;
            }
            var (path, match) = Locate(saved, folder, files);
            tabs.Add(new PlannedTab(new TabInfo(Guid.NewGuid(), saved.Name, TabRules.BlankUrl, null, kind, path ?? saved.Source), saved, path, match));
        }
        return new SessionPlan(session, folder, tabs);
    }

    /// <summary>Looks for every missing document by name in <paramref name="folder"/> (and a little below it). The others stay as they are.</summary>
    public SessionPlan WithFolder(string folder, ISessionFiles files)
    {
        var tabs = Tabs.Select(t => t.IsMissing && NameOf(t.Saved) is { } name && files.FindByName(folder, name) is { } found
            ? Found(t, found, SourceMatch.ChosenFolder)
            : t).ToList();
        return new SessionPlan(Session, SessionFolder, tabs);
    }

    /// <summary>The operator chose <paramref name="path"/> for the tab at <paramref name="index"/>; the folder it is in is then searched for the other missing documents.</summary>
    public SessionPlan WithChosenFile(int index, string path, ISessionFiles files)
    {
        if (index < 0 || index >= Tabs.Count || !files.Exists(path)) return this;
        var tabs = Tabs.ToList();
        tabs[index] = Found(tabs[index], path, SourceMatch.Chosen);
        var plan = new SessionPlan(Session, SessionFolder, tabs);
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        return folder is null ? plan : plan.WithFolder(folder, files);
    }

    private static PlannedTab Found(PlannedTab tab, string path, SourceMatch match)
        => tab with { Info = tab.Info with { Source = TabRules.StorableSource(path) }, FoundPath = path, Match = match };

    private static string? NameOf(SessionTab tab)
    {
        if (tab.Source is null) return null;
        string name = Path.GetFileName(tab.Source);
        return name.Length > 0 ? name : null;
    }

    // The path as saved, then the relative path, then the name beside the session file.
    private static (string? Path, SourceMatch Match) Locate(SessionTab saved, string sessionFolder, ISessionFiles files)
    {
        if (saved.Source is null) return (null, SourceMatch.NotNeeded);
        if (files.Exists(saved.Source)) return (saved.Source, SourceMatch.Original);
        if (saved.RelativeSource is { } relative)
        {
            string joined = Path.GetFullPath(Path.Combine(sessionFolder, relative));
            if (files.Exists(joined)) return (joined, SourceMatch.RelativeToSession);
        }
        if (NameOf(saved) is { } name)
        {
            string beside = Path.Combine(sessionFolder, name);
            if (files.Exists(beside)) return (beside, SourceMatch.NextToSession);
        }
        return (null, SourceMatch.Missing);
    }
}

/// <summary>Turns the tabs of a running session into a session file.</summary>
public static class SessionBuilder
{
    /// <summary>
    /// <paramref name="activeIndex"/> is the live tab. Each document also gets its path relative to the session file when both are on one drive.
    /// </summary>
    public static SessionFile Create(IEnumerable<TabInfo> tabs, int activeIndex, bool previewMode, RenderConfig render, string sessionPath)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(sessionPath))!;
        var saved = tabs.Select(t => TabKinds.IsDocument(t.Kind)
            ? new SessionTab { Name = t.Name, Kind = TabKinds.ConfigName(t.Kind), Url = TabRules.BlankUrl, Source = t.Source, RelativeSource = Relative(folder, t.Source) }
            : new SessionTab { Name = t.Name, Kind = "web", Url = t.Url, ProfileId = t.ProfileId }).ToList();
        return new SessionFile
        {
            Render = new RenderConfig { Width = render.Width, Height = render.Height, Fps = render.Fps },
            PreviewMode = previewMode,
            ActiveTab = saved.Count == 0 ? 0 : Math.Clamp(activeIndex, 0, saved.Count - 1),
            Tabs = saved,
        };
    }

    private static string? Relative(string folder, string? source)
    {
        if (source is null || !Path.IsPathRooted(source)) return null;
        try
        {
            string relative = Path.GetRelativePath(folder, source);
            // Another drive has no relative form: GetRelativePath then returns the full path again.
            return Path.IsPathRooted(relative) ? null : TabRules.StorableSource(relative);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
