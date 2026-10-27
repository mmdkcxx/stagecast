using System.Numerics;
using Stagecast.Core.Tabs;
using Windows.UI.Composition;

namespace Stagecast.Rendering;

/// <summary>
/// A file played by Stagecast itself (a PDF, a PowerPoint show, a video) instead of a web page. It draws into a visual of the render
/// size, like a page does, so the engine treats it as any other tab. For now the visual shows a single colour: the sources that
/// draw the real pictures plug in here later.
/// </summary>
public sealed class DocumentTab : StageTab
{
    internal DocumentTab(Guid id, ContainerVisual visual, SpriteVisual content, TabKind kind, string? source, string title)
        : base(id, visual)
    {
        Content = content;
        Kind = kind;
        Source = source;
        Title = title;
    }

    public override TabKind Kind { get; }

    /// <summary>The path of the file the tab plays; null until one is chosen.</summary>
    public string? Source { get; internal set; }

    public override string Title { get; }

    /// <summary>The pages of a PDF tab, once <see cref="RenderEngine.AttachPdf"/> has been called.</summary>
    public PdfSource? Pdf { get; internal set; }

    internal SpriteVisual Content { get; }

    internal override void Resize(int width, int height)
    {
        Visual.Size = new Vector2(width, height);
        Content.Size = new Vector2(width, height);
        Pdf?.Resize(width, height);
    }

    internal override void Close()
    {
        Pdf?.Dispose();
        Content.Dispose();
    }
}
