using Stagecast.Core.Tabs;
using Windows.UI.Composition;

namespace Stagecast.Rendering;

/// <summary>
/// What every tab has in common: a composition visual of the render size that the engine puts on the stage, and the state the
/// engine needs to switch tabs. A web page (<see cref="PageTab"/>) and a file played by Stagecast itself
/// (<see cref="DocumentTab"/>) are both tabs, so selecting, Preview + Take, closing and capturing work the same for them.
/// </summary>
public abstract class StageTab
{
    internal StageTab(Guid id, ContainerVisual visual)
    {
        Id = id;
        Visual = visual;
    }

    public Guid Id { get; internal set; }

    public abstract TabKind Kind { get; }

    /// <summary>Null on the shared profile and for tabs that are not web pages; otherwise the id of this tab's own profile (its own login).</summary>
    public virtual string? ProfileId => null;

    internal ContainerVisual Visual { get; }

    /// <summary>True once the tab has been on the stage, so its visual holds a picture.</summary>
    public bool HasBeenShown { get; internal set; }

    /// <summary>True while the tab is still loading what it shows (a web page); the engine waits for it a moment before showing the tab.</summary>
    public bool IsLoading { get; internal set; }

    /// <summary>True while the page would receive keystrokes. Always false for tabs that are not web pages.</summary>
    public bool HasKeyboardFocus { get; internal set; }

    /// <summary>What is wrong with this tab right now, in words for the operator; null when it is fine.</summary>
    public string? Problem { get; internal set; }

    public abstract string Title { get; }

    /// <summary>Gives the tab the new render size.</summary>
    internal abstract void Resize(int width, int height);

    /// <summary>Releases what the tab holds other than <see cref="Visual"/>, which the engine disposes.</summary>
    internal abstract void Close();
}
