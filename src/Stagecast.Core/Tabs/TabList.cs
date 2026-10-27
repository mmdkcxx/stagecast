namespace Stagecast.Core.Tabs;

/// <summary>One tab as the operator sees it. The name is only a label; the address is where the tab last was.</summary>
/// <param name="ProfileId">Null when the tab shares the common browser profile; otherwise the id of its own profile (a separate login).</param>
public sealed record TabInfo(Guid Id, string Name, string Url, string? ProfileId = null);

public enum TabNameError
{
    None,
    Empty,
    TooLong,
}

public static class TabRules
{
    public const int MaxNameLength = 40;

    /// <summary>Every tab is a browser with its own page; this keeps memory and CPU in check on a show laptop.</summary>
    public const int MaxTabs = 12;

    public const string BlankUrl = "about:blank";

    /// <summary>A profile id is the name of a folder, so only the exact form this program makes is accepted (32 hex digits).</summary>
    public static bool IsValidProfileId(string? id)
        => id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string NewProfileId() => Guid.NewGuid().ToString("N");

    public static TabNameError ValidateName(string? name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return TabNameError.Empty;
        return trimmed.Length > MaxNameLength ? TabNameError.TooLong : TabNameError.None;
    }

    /// <summary>"Tab 1", "Tab 2", ... the first number not used by an existing default-looking name.</summary>
    public static string NextDefaultName(IEnumerable<TabInfo> existing)
    {
        var used = existing.Select(t => t.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++)
        {
            string name = $"Tab {n}";
            if (!used.Contains(name)) return name;
        }
    }

    /// <summary>Addresses worth restoring at the next start. Inline data pages are not (they can be huge and are only test pages).</summary>
    public static string StorableUrl(string? url)
        => string.IsNullOrWhiteSpace(url) || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? BlankUrl : url;
}

/// <summary>
/// The ordered tabs and which one is active. At least one tab always exists. Closing the active tab activates its neighbour;
/// moving a tab never changes which tab is active.
/// <para>
/// Two ways to work. <b>Direct</b> (the default): the active tab is what the audience sees. <b>Preview + Take</b>: the active tab
/// is only the operator's preview, <see cref="ProgramId"/> names the tab the audience sees, and <see cref="Take"/> swaps them.
/// The program tab cannot be selected as the preview and cannot be closed while it is on air.
/// </para>
/// </summary>
public sealed class TabList
{
    private readonly List<TabInfo> _items;

    public TabList(IEnumerable<TabInfo> items, int activeIndex)
    {
        _items = items.Take(TabRules.MaxTabs).ToList();
        if (_items.Count == 0) _items.Add(new TabInfo(Guid.NewGuid(), "Tab 1", TabRules.BlankUrl));
        ActiveIndex = Math.Clamp(activeIndex, 0, _items.Count - 1);
    }

    public IReadOnlyList<TabInfo> Items => _items;

    public int ActiveIndex { get; private set; }

    public TabInfo Active => _items[ActiveIndex];

    /// <summary>The tab on air while working in Preview + Take; null in Direct mode (where the active tab is on air).</summary>
    public Guid? ProgramId { get; private set; }

    public bool PreviewMode => ProgramId is not null;

    /// <summary>The tab the audience sees.</summary>
    public TabInfo Program => ProgramId is { } id ? _items[IndexOf(id)] : Active;

    public int LiveIndex => ProgramId is { } id ? IndexOf(id) : ActiveIndex;

    public bool CanEnterPreviewMode => !PreviewMode && _items.Count >= 2;

    /// <summary>Starts Preview + Take: the active tab stays on air and the next tab becomes the preview.</summary>
    public bool EnterPreviewMode()
    {
        if (!CanEnterPreviewMode) return false;
        ProgramId = Active.Id;
        ActiveIndex = (ActiveIndex + 1) % _items.Count;
        return true;
    }

    /// <summary>Back to Direct: the tab that was on air stays on air and becomes the active one.</summary>
    public bool ExitPreviewMode()
    {
        if (ProgramId is not { } id) return false;
        ActiveIndex = IndexOf(id);
        ProgramId = null;
        return true;
    }

    /// <summary>Puts the preview tab on air and the tab that was on air becomes the preview.</summary>
    public bool Take()
    {
        if (ProgramId is not { } onAir || Active.Id == onAir) return false;
        var next = Active.Id;
        ActiveIndex = IndexOf(onAir);
        ProgramId = next;
        return true;
    }

    public bool CanAdd => _items.Count < TabRules.MaxTabs;

    public bool CanClose => _items.Count > 1;

    public bool CanCloseTab(Guid id) => _items.Count > 1 && id != ProgramId && IndexOf(id) >= 0;

    public int IndexOf(Guid id) => _items.FindIndex(t => t.Id == id);

    /// <summary>Appends a tab. Returns null at the limit. The new tab is not selected.</summary>
    public TabInfo? Add(string? url = null)
    {
        if (!CanAdd) return null;
        var tab = new TabInfo(Guid.NewGuid(), TabRules.NextDefaultName(_items), TabRules.StorableUrl(url));
        _items.Add(tab);
        return tab;
    }

    public bool Select(Guid id)
    {
        int index = IndexOf(id);
        if (index < 0 || id == ProgramId) return false; // the tab on air cannot be the preview
        ActiveIndex = index;
        return true;
    }

    /// <summary>Removes a tab unless it is the last one. Returns the tab that is active afterwards, or null when nothing was removed.</summary>
    public TabInfo? Close(Guid id)
    {
        int index = IndexOf(id);
        if (index < 0 || !CanCloseTab(id)) return null;
        _items.RemoveAt(index);
        if (index < ActiveIndex) ActiveIndex--;
        else if (index == ActiveIndex) ActiveIndex = Math.Min(index, _items.Count - 1);
        if (ProgramId is { } onAir)
        {
            if (_items.Count < 2) ExitPreviewMode();
            else if (Active.Id == onAir)
            {
                // Closing the preview left the tab on air next to it: the preview moves to the nearest other tab.
                int program = IndexOf(onAir);
                ActiveIndex = program + 1 < _items.Count ? program + 1 : program - 1;
            }
        }
        return Active;
    }

    /// <summary>Gives the tab its own browser profile (a separate login) or puts it back on the shared one. Returns the profile id it had.</summary>
    public string? SetSeparateProfile(Guid id, bool separate)
    {
        int index = IndexOf(id);
        if (index < 0) return null;
        string? old = _items[index].ProfileId;
        string? next = separate ? old ?? TabRules.NewProfileId() : null;
        _items[index] = _items[index] with { ProfileId = next };
        return old;
    }

    public TabNameError Rename(Guid id, string? name)
    {
        var error = TabRules.ValidateName(name);
        int index = IndexOf(id);
        if (error != TabNameError.None || index < 0) return error;
        _items[index] = _items[index] with { Name = name!.Trim() };
        return TabNameError.None;
    }

    public void SetUrl(Guid id, string? url)
    {
        int index = IndexOf(id);
        if (index >= 0) _items[index] = _items[index] with { Url = TabRules.StorableUrl(url) };
    }

    /// <summary>Moves a tab by <paramref name="delta"/> places (negative is towards the start). Returns false at an end.</summary>
    public bool Move(Guid id, int delta)
    {
        int from = IndexOf(id), to = from + delta;
        if (from < 0 || to < 0 || to >= _items.Count || delta == 0) return false;
        var active = Active;
        var tab = _items[from];
        _items.RemoveAt(from);
        _items.Insert(to, tab);
        ActiveIndex = IndexOf(active.Id);
        return true;
    }
}
