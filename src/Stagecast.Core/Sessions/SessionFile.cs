using Stagecast.Core.Configuration;

namespace Stagecast.Core.Sessions;

/// <summary>
/// A saved session: the tabs the operator had open, which one was live, the mode and the render size. It is a file the operator
/// can copy to another computer, so it holds no credentials (a login stays in the browser profile on the machine that made it,
/// named here only by its id) and no document contents or passwords; only the path of each document.
/// </summary>
public sealed class SessionFile
{
    public const int CurrentSchemaVersion = 1;

    public const string Extension = ".stagecast";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The resolution and rate to render at. Null leaves the current ones alone (a file without them, or with unusable ones).</summary>
    public RenderConfig? Render { get; init; }

    /// <summary>Preview + Take instead of Direct.</summary>
    public bool PreviewMode { get; init; }

    /// <summary>Position in <see cref="Tabs"/> of the tab that was live (in Direct mode, the selected one).</summary>
    public int ActiveTab { get; init; }

    public List<SessionTab> Tabs { get; init; } = [];
}

public sealed class SessionTab
{
    public string Name { get; init; } = string.Empty;

    /// <summary>"web", "pdf", "powerpoint" or "video"; a string so a name from a newer version does not make the file unreadable.</summary>
    public string Kind { get; init; } = "web";

    /// <summary>Where a web tab was. Only http, https and about:blank are kept: a session file may come from anyone.</summary>
    public string Url { get; init; } = "about:blank";

    /// <summary>The separate profile (login) of a web tab, named by id. Used again only where that profile still exists, so it never carries a login to another computer.</summary>
    public string? ProfileId { get; init; }

    /// <summary>The path of the document on the computer that saved the session. Null for a web tab, or a document tab with no file chosen.</summary>
    public string? Source { get; init; }

    /// <summary>The same document relative to the folder of the session file, when both are on one drive; lets a folder with the session and its documents move as a whole.</summary>
    public string? RelativeSource { get; init; }
}
