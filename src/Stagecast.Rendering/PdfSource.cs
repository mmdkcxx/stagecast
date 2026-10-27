using System.IO;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Frames;
using Stagecast.Rendering.Capture;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Composition;

namespace Stagecast.Rendering;

/// <summary>A small picture of a page: premultiplied BGRA, <c>Width * 4</c> bytes per row.</summary>
public sealed record PdfThumbnail(byte[] Pixels, int Width, int Height);

public enum PdfOpenResult
{
    Opened,

    /// <summary>The file is protected and no password (or an empty one) was given.</summary>
    PasswordRequired,

    WrongPassword,

    /// <summary>Missing, damaged or not a PDF; <see cref="PdfSource.Problem"/> says what happened.</summary>
    Failed,
}

/// <summary>
/// Shows the pages of a PDF on a <see cref="DocumentTab"/>: the built-in Windows PDF component renders a page at the size it fills
/// on the canvas (scale to fit, see <see cref="FitComposer"/>), the pixels go into a composition surface, and the stage capture
/// carries them to every output like a web page. Changing page is a hard cut. The file is opened from a temporary copy, and the
/// password is used once to open it and never kept.
/// </summary>
public sealed class PdfSource : IDisposable
{
    // The page on screen plus one before and two after: stepping forward is the common case.
    private const int PagesBefore = 1;
    private const int PagesAfter = 2;

    private readonly Compositor _compositor;
    private readonly DocumentSurfaces _surfaces;
    private readonly SpriteVisual _page;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _renderLock = new(1, 1);
    private readonly Dictionary<int, Rendered> _cache = new();
    private PdfDocument? _document;
    private string? _tempPath;
    private CompositionDrawingSurface? _surface;
    private int _surfaceWidth;
    private int _surfaceHeight;
    private int _canvasWidth;
    private int _canvasHeight;
    private int _version;
    private bool _disposed;

    internal PdfSource(Compositor compositor, DocumentSurfaces surfaces, ContainerVisual host, int canvasWidth, int canvasHeight, ILogger logger)
    {
        _compositor = compositor;
        _surfaces = surfaces;
        _logger = logger;
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
        _page = compositor.CreateSpriteVisual();
        _page.IsVisible = false;
        host.Children.InsertAtTop(_page);
    }

    /// <summary>0 until a file is open.</summary>
    public int PageCount { get; private set; }

    /// <summary>Zero-based index of the page on screen; -1 before the first page is shown.</summary>
    public int PageIndex { get; private set; } = -1;

    /// <summary>The page on screen now (one-based, 0 before the first page), for "Halaman 3 / 40".</summary>
    public int PageNumber => PageIndex + 1;

    public string? Problem { get; private set; }

    public event Action? PageChanged;

    /// <summary>Opens <paramref name="path"/> and shows page <paramref name="startPage"/> (zero-based, the first by default). The password is only used for this call.</summary>
    public async Task<PdfOpenResult> OpenAsync(string path, string? password = null, int startPage = 0)
    {
        ClearDocument();
        string temp = Path.Combine(Path.GetTempPath(), "stagecast-pdf-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            File.Copy(path, temp);
            StorageFile file = await StorageFile.GetFileFromPathAsync(temp);
            PdfDocument document = await PdfDocument.LoadFromFileAsync(file, password ?? "");
            if (document.PageCount == 0)
            {
                Delete(temp);
                return Fail("The PDF has no pages.");
            }

            _tempPath = temp;
            _document = document;
            PageCount = (int)document.PageCount;
            Problem = null;
        }
        catch (Exception ex)
        {
            Delete(temp);
            // The component reports a locked file with one error for "no password" and "wrong password": tell them apart by what was given.
            if (ex.HResult == unchecked((int)0x8007052B) || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                Problem = string.IsNullOrEmpty(password) ? "The PDF needs a password." : "The password is wrong.";
                return string.IsNullOrEmpty(password) ? PdfOpenResult.PasswordRequired : PdfOpenResult.WrongPassword;
            }

            _logger.LogWarning(ex, "Could not open PDF {Path}", path);
            return Fail(ex is FileNotFoundException or DirectoryNotFoundException ? "The file was not found." : "The file could not be opened as a PDF.");
        }

        _logger.LogInformation("PDF opened: {Pages} pages", PageCount);
        await GoToAsync(startPage);
        return PdfOpenResult.Opened;
    }

    /// <summary>Shows page <paramref name="index"/> (zero-based, clamped to the document). Returns false when no document is open.</summary>
    public async Task<bool> GoToAsync(int index)
    {
        if (_document is null || _disposed) return false;
        index = Math.Clamp(index, 0, PageCount - 1);
        int version = ++_version;
        Rendered? rendered = await GetAsync(index);
        // A newer request (or a resize) arrived while this page was rendering; that one decides what is on screen.
        if (rendered is null || version != _version || _disposed) return rendered is not null;
        Show(rendered);
        PageIndex = index;
        PageChanged?.Invoke();
        _ = PrepareNeighboursAsync(index, version);
        return true;
    }

    public Task<bool> NextAsync() => PageIndex + 1 < PageCount ? GoToAsync(PageIndex + 1) : Task.FromResult(false);

    public Task<bool> PreviousAsync() => PageIndex > 0 ? GoToAsync(PageIndex - 1) : Task.FromResult(false);

    /// <summary>The render size changed: pages are drawn again at the new size, the page on screen first.</summary>
    internal void Resize(int canvasWidth, int canvasHeight)
    {
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
        _cache.Clear();
        if (_document is null || PageIndex < 0) return;
        _ = GoToAsync(PageIndex);
    }

    private async Task<Rendered?> GetAsync(int index)
    {
        if (_cache.TryGetValue(index, out Rendered? cached) && cached.CanvasWidth == _canvasWidth && cached.CanvasHeight == _canvasHeight) return cached;
        await _renderLock.WaitAsync();
        try
        {
            if (_document is null || _disposed) return null;
            if (_cache.TryGetValue(index, out cached) && cached.CanvasWidth == _canvasWidth && cached.CanvasHeight == _canvasHeight) return cached;
            int cw = _canvasWidth, ch = _canvasHeight;
            using PdfPage page = _document.GetPage((uint)index);
            FitLayout layout = FitComposer.Fit(page.Size.Width, page.Size.Height, cw, ch);
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)layout.Width,
                DestinationHeight = (uint)layout.Height,
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
                // The default PNG output costs about three times as much time as raw pixels (docs/SPIKE_REPORT.md).
                BitmapEncoderId = BitmapEncoder.BmpEncoderId,
            };
            (byte[] pixels, int pixelWidth, int pixelHeight) = await DrawAsync(page, options);
            var rendered = new Rendered(index, pixels, pixelWidth, pixelHeight, layout, cw, ch);
            _cache[index] = rendered;
            return rendered;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not render PDF page {Page}", index + 1);
            Problem = $"Page {index + 1} could not be drawn.";
            return null;
        }
        finally
        {
            _renderLock.Release();
        }
    }

    private static async Task<(byte[] Pixels, int Width, int Height)> DrawAsync(PdfPage page, PdfPageRenderOptions options)
    {
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        PixelDataProvider data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return (data.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }

    /// <summary>
    /// A small picture of page <paramref name="index"/> (zero-based) at most <paramref name="maxWidth"/> x <paramref name="maxHeight"/>,
    /// premultiplied BGRA, for the page strip. Null when no document is open or the page cannot be drawn.
    /// </summary>
    public async Task<PdfThumbnail?> RenderThumbnailAsync(int index, int maxWidth, int maxHeight)
    {
        await _renderLock.WaitAsync();
        try
        {
            if (_document is null || _disposed || index < 0 || index >= PageCount) return null;
            using PdfPage page = _document.GetPage((uint)index);
            FitLayout layout = FitComposer.Fit(page.Size.Width, page.Size.Height, maxWidth, maxHeight);
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)layout.Width,
                DestinationHeight = (uint)layout.Height,
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
                BitmapEncoderId = BitmapEncoder.BmpEncoderId,
            };
            (byte[] pixels, int width, int height) = await DrawAsync(page, options);
            return new PdfThumbnail(pixels, width, height);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not draw a thumbnail of PDF page {Page}", index + 1);
            return null;
        }
        finally
        {
            _renderLock.Release();
        }
    }

    private async Task PrepareNeighboursAsync(int index, int version)
    {
        // Keep only the pages around the one on screen so a long document does not fill the memory.
        foreach (int key in _cache.Keys.Where(k => k < index - PagesBefore || k > index + PagesAfter).ToList()) _cache.Remove(key);
        for (int next = index + 1; next <= index + PagesAfter && next < PageCount; next++)
        {
            if (version != _version || _disposed) return;
            await GetAsync(next);
        }

        if (index - PagesBefore >= 0 && version == _version && !_disposed) await GetAsync(index - PagesBefore);
    }

    private void Show(Rendered rendered)
    {
        if (_surface is null || _surfaceWidth != rendered.Width || _surfaceHeight != rendered.Height)
        {
            _surface?.Dispose();
            _surface = _surfaces.CreateSurface(rendered.Width, rendered.Height);
            _surfaceWidth = rendered.Width;
            _surfaceHeight = rendered.Height;
            CompositionSurfaceBrush brush = _compositor.CreateSurfaceBrush(_surface);
            brush.Stretch = CompositionStretch.None;
            _page.Brush = brush;
        }

        _surfaces.Draw(_surface, rendered.Pixels, rendered.Width, rendered.Height, rendered.Width * 4);
        _page.Size = new Vector2(rendered.Width, rendered.Height);
        _page.Offset = new Vector3(rendered.Layout.X, rendered.Layout.Y, 0);
        _page.IsVisible = true;
    }

    private PdfOpenResult Fail(string problem)
    {
        Problem = problem;
        return PdfOpenResult.Failed;
    }

    private void ClearDocument()
    {
        _version++;
        _document = null;
        _cache.Clear();
        _page.IsVisible = false;
        PageCount = 0;
        PageIndex = -1;
        Problem = null;
        if (_tempPath is not null) Delete(_tempPath);
        _tempPath = null;
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearDocument();
        _surface?.Dispose();
        _surface = null;
        _page.Dispose();
    }

    private sealed record Rendered(int Index, byte[] Pixels, int Width, int Height, FitLayout Layout, int CanvasWidth, int CanvasHeight);
}
