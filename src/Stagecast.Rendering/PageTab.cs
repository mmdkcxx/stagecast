using Microsoft.Web.WebView2.Core;
using Windows.UI.Composition;

namespace Stagecast.Rendering;

/// <summary>
/// One page: a WebView2 drawing into its own composition visual. Only the active tab's visual is placed on the engine's stage,
/// so only the active tab is captured. Tabs are created and switched through <see cref="RenderEngine"/>.
/// </summary>
public sealed class PageTab
{
    internal PageTab(Guid id, ContainerVisual visual, CoreWebView2CompositionController controller, string? profileId)
    {
        Id = id;
        ProfileId = profileId;
        Visual = visual;
        Controller = controller;
    }

    public Guid Id { get; internal set; }

    /// <summary>Null on the shared profile; otherwise the id of this tab's own profile (its own login).</summary>
    public string? ProfileId { get; }

    internal ContainerVisual Visual { get; }

    internal CoreWebView2CompositionController Controller { get; }

    public CoreWebView2 Browser => Controller.CoreWebView2;

    /// <summary>True once the tab has been on the stage, so its visual holds a picture.</summary>
    public bool HasBeenShown { get; internal set; }

    /// <summary>True from the start of a navigation until it has completed.</summary>
    public bool IsLoading { get; internal set; }

    /// <summary>True while the page would receive keystrokes.</summary>
    public bool HasKeyboardFocus { get; internal set; }

    public string Url => Browser.Source;

    internal CoreWebView2Environment? Environment { get; set; }

    /// <summary>The last address the tab was on; still known when the browser behind it has died.</summary>
    internal string LastUrl { get; set; } = "about:blank";

    internal string? NavigatingTo { get; set; }

    /// <summary>The address that failed to load because of the network; the engine retries it until it loads.</summary>
    internal string? FailedUrl { get; set; }

    internal int RetryAttempt { get; set; }

    internal bool RetryPending { get; set; }

    internal bool Recovering { get; set; }

    internal List<DateTimeOffset> Crashes { get; } = [];

    /// <summary>What is wrong with this tab right now, in words for the operator; null when it is fine.</summary>
    public string? Problem { get; internal set; }

    public string Title => Browser.DocumentTitle;
}
