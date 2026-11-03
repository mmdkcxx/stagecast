namespace Stagecast.Rendering;

/// <summary>A small picture of a page or slide: premultiplied BGRA, <c>Width * 4</c> bytes per row.</summary>
public sealed record PageThumbnail(byte[] Pixels, int Width, int Height);

/// <summary>
/// What the operator controls on a tab that plays a file made of pages or slides (a PDF, a PowerPoint show): where it is, how to
/// move, and a small picture of each page. The page controls, the strip under the preview and the clicker work on this.
/// </summary>
public interface IDocumentSource
{
    /// <summary>0 until a file is open.</summary>
    int PageCount { get; }

    /// <summary>Zero-based index of the page on screen; -1 before the first page is shown.</summary>
    int PageIndex { get; }

    /// <summary>The page on screen now (one-based, 0 before the first page), for "Halaman 3 / 40".</summary>
    int PageNumber { get; }

    string? Problem { get; }

    event Action? PageChanged;

    /// <summary>A page could not be drawn; the picture on screen stays as it was. The argument is the one-based page number.</summary>
    event Action<int>? PageFailed;

    /// <summary>Shows page <paramref name="index"/> (zero-based, clamped to the document). False when nothing is open or the page could not be shown.</summary>
    Task<bool> GoToAsync(int index);

    Task<bool> NextAsync();

    Task<bool> PreviousAsync();

    /// <summary>A picture of page <paramref name="index"/> at most <paramref name="maxWidth"/> x <paramref name="maxHeight"/>, or null when it cannot be made.</summary>
    Task<PageThumbnail?> RenderThumbnailAsync(int index, int maxWidth, int maxHeight);
}
