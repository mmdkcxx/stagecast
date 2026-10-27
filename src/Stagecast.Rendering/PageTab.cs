using Microsoft.Web.WebView2.Core;
using Windows.UI.Composition;

namespace Stagecast.Rendering;

/// <summary>
/// One page: a WebView2 drawing into its own composition visual. Only the active tab's visual is placed on the engine's stage,
/// so only the active tab is captured. Tabs are created and switched through <see cref="RenderEngine"/>.
/// </summary>
public sealed class PageTab
{
    internal PageTab(Guid id, ContainerVisual visual, CoreWebView2CompositionController controller)
    {
        Id = id;
        Visual = visual;
        Controller = controller;
    }

    public Guid Id { get; }

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

    public string Title => Browser.DocumentTitle;
}
