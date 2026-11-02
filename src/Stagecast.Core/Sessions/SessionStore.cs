using System.Text.Json;
using Stagecast.Core.Configuration;
using Stagecast.Core.Rendering;
using Stagecast.Core.Tabs;

namespace Stagecast.Core.Sessions;

public enum SessionLoadStatus
{
    /// <summary>The file was read as it was.</summary>
    Loaded,

    /// <summary>The file was readable but some values were unusable; they were repaired or dropped (see the notes).</summary>
    Repaired,

    /// <summary>There is no such file.</summary>
    NotFound,

    /// <summary>The file could not be read or is not a session. It is left untouched.</summary>
    Unreadable,
}

public sealed record SessionLoadResult(SessionFile? Session, SessionLoadStatus Status, IReadOnlyList<string> Notes)
{
    public bool Ok => Session is not null;
}

/// <summary>
/// Reads and writes session files. Unlike the settings file, a session file belongs to the operator (and may come from someone else),
/// so a bad one is reported and never moved, rewritten or deleted.
/// </summary>
public static class SessionStore
{
    /// <summary>A session file is a short list; anything larger is not one.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Writes through a temporary file so a crash while saving cannot leave a half-written session.</summary>
    public static void Save(string path, SessionFile session)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp = full + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(session, Json));
        if (File.Exists(full)) File.Replace(temp, full, null);
        else File.Move(temp, full);
    }

    public static SessionLoadResult Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new SessionLoadResult(null, SessionLoadStatus.NotFound, []);
            if (info.Length > MaxFileBytes) return Unreadable("the file is too large to be a session");
            var parsed = JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(path), Json);
            if (parsed is null) return Unreadable("the file is empty or 'null'");
            return Normalize(parsed);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return Unreadable($"could not read the file ({ex.Message})");
        }
    }

    private static SessionLoadResult Unreadable(string note) => new(null, SessionLoadStatus.Unreadable, [note]);

    internal static SessionLoadResult Normalize(SessionFile input)
    {
        var notes = new List<string>();
        if (input.SchemaVersion > SessionFile.CurrentSchemaVersion)
            notes.Add($"the file is from a newer version (schema {input.SchemaVersion}); unknown settings are ignored");

        RenderConfig? render = null;
        if (input.Render is { } r)
        {
            if (Resolution.TryCreate(r.Width, r.Height, out _) && r.Fps is 30 or 60) render = new RenderConfig { Width = r.Width, Height = r.Height, Fps = r.Fps };
            else notes.Add("the resolution or rate in the file is not usable; the current ones stay");
        }

        var tabs = new List<SessionTab>();
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<TabInfo>();
        // Positions in the file that were dropped, so the active tab still points at the tab it meant.
        var kept = new List<int>();
        int position = -1;
        foreach (var entry in input.Tabs ?? [])
        {
            position++;
            if (entry is null) continue;
            if (tabs.Count >= TabRules.MaxTabs)
            {
                notes.Add($"more than {TabRules.MaxTabs} tabs; the rest were dropped");
                break;
            }

            string name = (entry.Name ?? string.Empty).Trim();
            if (TabRules.ValidateName(name) == TabNameError.Empty) name = TabRules.NextDefaultName(names);
            else if (name.Length > TabRules.MaxNameLength) name = name[..TabRules.MaxNameLength].TrimEnd();

            if (!TabKinds.TryParse(entry.Kind, out var kind))
            {
                notes.Add($"tab '{name}' has an unknown kind '{entry.Kind}' and was dropped");
                continue;
            }

            SessionTab tab;
            if (TabKinds.IsDocument(kind))
            {
                string? source = SafePath(TabRules.StorableSource(entry.Source));
                string? relative = SafeRelative(entry.RelativeSource);
                if (entry.Source is not null && source is null) notes.Add($"tab '{name}' had an unusable file path; the file has to be chosen again");
                tab = new SessionTab { Name = name, Kind = TabKinds.ConfigName(kind), Url = TabRules.BlankUrl, Source = source, RelativeSource = relative };
            }
            else
            {
                string url = SafeUrl(entry.Url, out bool changed);
                if (changed) notes.Add($"tab '{name}' had an address that is not http or https; it opens blank");
                string? profileId = TabRules.IsValidProfileId(entry.ProfileId) && profiles.Add(entry.ProfileId!) ? entry.ProfileId : null;
                if (entry.ProfileId is not null && profileId is null) notes.Add($"tab '{name}' had an unusable profile id; it uses the shared profile");
                tab = new SessionTab { Name = name, Kind = "web", Url = url, ProfileId = profileId };
            }
            tabs.Add(tab);
            names.Add(new TabInfo(Guid.Empty, name, TabRules.BlankUrl));
            kept.Add(position);
        }

        // Dropped tabs before the active one shift it; a dropped active tab falls back to the first.
        int active = kept.IndexOf(input.ActiveTab);
        if (active < 0) active = tabs.Count == 0 ? 0 : Math.Clamp(kept.FindLastIndex(p => p < input.ActiveTab), 0, tabs.Count - 1);

        var session = new SessionFile { Render = render, PreviewMode = input.PreviewMode, ActiveTab = active, Tabs = tabs };
        return new SessionLoadResult(session, notes.Count == 0 ? SessionLoadStatus.Loaded : SessionLoadStatus.Repaired, notes);
    }

    /// <summary>Only web addresses (and the blank page) are kept: a session file must not be able to open a local file, run a script or reach an odd scheme.</summary>
    private static string SafeUrl(string? url, out bool changed)
    {
        changed = false;
        string trimmed = (url ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Equals(TabRules.BlankUrl, StringComparison.OrdinalIgnoreCase)) return TabRules.BlankUrl;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return trimmed;
        changed = true;
        return TabRules.BlankUrl;
    }

    /// <summary>A relative path is joined to the folder of the session file, so a rooted one is not relative and is dropped.</summary>
    private static string? SafeRelative(string? relative)
    {
        string? stored = SafePath(TabRules.StorableSource(relative));
        return stored is null || Path.IsPathRooted(stored) ? null : stored;
    }

    // A null character ends a path in Windows calls, so a path holding one is not a path to a file.
    private static string? SafePath(string? path) => path is not null && path.Contains('\0') ? null : path;
}
