using Microsoft.Web.WebView2.Core;
using Stagecast.Core.Tabs;
using Windows.UI.Composition;

namespace Stagecast.Rendering;

/// <summary>
/// One page: a WebView2 drawing into its own composition visual. Only the active tab's visual is placed on the engine's stage,
/// so only the active tab is captured. Tabs are created and switched through <see cref="RenderEngine"/>.
/// </summary>
public sealed class PageTab : StageTab
{
    internal PageTab(Guid id, ContainerVisual visual, CoreWebView2CompositionController controller, string? profileId)
        : base(id, visual)
    {
        ProfileId = profileId;
        Controller = controller;
    }

    public override TabKind Kind => TabKind.Web;

    /// <summary>Null on the shared profile; otherwise the id of this tab's own profile (its own login).</summary>
    public override string? ProfileId { get; }

    internal CoreWebView2CompositionController Controller { get; }

    public CoreWebView2 Browser => Controller.CoreWebView2;

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

    public override string Title => Browser.DocumentTitle;

    internal override void Resize(int width, int height)
    {
        Visual.Size = new System.Numerics.Vector2(width, height);
        Controller.Bounds = new System.Drawing.Rectangle(0, 0, width, height);
    }

    internal override void Close() => Controller.Close();
}
