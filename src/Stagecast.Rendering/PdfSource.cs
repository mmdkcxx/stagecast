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
public sealed class PdfSource : IDocumentSource, IDisposable
{
    // The page on screen plus one before and two after: stepping forward is the common case.
    private const int PagesBefore = 1;
    private const int PagesAfter = 2;

    // Pages are drawn at the size they fill on the canvas, so a big canvas means big pages (8K is 132 MB each). Neighbours are only
    // kept ahead of time while the cache stays under this; the page on screen is always kept.
    private const long CacheBudgetBytes = 256L * 1024 * 1024;

    // Files left behind by a crash are removed when the first PDF tab of a run is created.
    private const string TempPrefix = "stagecast-pdf-";
    private static bool _tempCleaned;

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
    private int _target = -1;
    private bool _redrawPending;
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

    /// <summary>A page could not be drawn (damaged page, out of memory); the picture on screen stays as it was. The argument is the one-based page number.</summary>
    public event Action<int>? PageFailed;

    /// <summary>The page was drawn again after a render size change. A capture started meanwhile may hold the old picture of a still page, so the engine restarts it.</summary>
    internal event Action? Redrawn;

    internal static void CleanOldTempFiles()
    {
        if (_tempCleaned) return;
        _tempCleaned = true;
        try
        {
            foreach (string file in Directory.EnumerateFiles(Path.GetTempPath(), TempPrefix + "*.pdf"))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-12)) Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Opens <paramref name="path"/> and shows page <paramref name="startPage"/> (zero-based, the first by default). The password is
    /// only used for this call. A file that cannot be opened changes nothing: a document that is already showing keeps showing, so a
    /// failed reload does not blank a tab that is on air.
    /// </summary>
    public async Task<PdfOpenResult> OpenAsync(string path, string? password = null, int startPage = 0)
    {
        if (_disposed) return PdfOpenResult.Failed;
        string temp = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N") + ".pdf");
        PdfDocument document;
        try
        {
            File.Copy(path, temp);
            StorageFile file = await StorageFile.GetFileFromPathAsync(temp);
            document = await PdfDocument.LoadFromFileAsync(file, password ?? "");
            if (document.PageCount == 0)
            {
                Delete(temp);
                return Fail("The PDF has no pages.");
            }
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

        if (_disposed)
        {
            Delete(temp);
            return PdfOpenResult.Failed;
        }

        // The new file is good: it replaces the old one, whose picture stays on screen until the new first page is ready.
        string? oldTemp = _tempPath;
        _version++;
        _cache.Clear();
        _tempPath = temp;
        _document = document;
        PageCount = (int)document.PageCount;
        Problem = null;
        if (oldTemp is not null) Delete(oldTemp);
        _logger.LogInformation("PDF opened: {Pages} pages", PageCount);
        await GoToAsync(startPage);
        return PdfOpenResult.Opened;
    }

    /// <summary>Shows page <paramref name="index"/> (zero-based, clamped to the document). Returns false when no document is open or the page could not be drawn.</summary>
    public async Task<bool> GoToAsync(int index)
    {
        if (_document is null || _disposed) return false;
        index = Math.Clamp(index, 0, PageCount - 1);
        int version = ++_version;
        _target = index;
        Rendered? rendered = await GetAsync(index, version);
        // A newer request (or a resize) arrived while this page waited or rendered; that one decides what is on screen.
        if (version != _version || _disposed) return true;
        if (rendered is null)
        {
            _target = PageIndex;
            PageFailed?.Invoke(index + 1);
            return false;
        }

        Show(rendered);
        PageIndex = index;
        if (_redrawPending)
        {
            _redrawPending = false;
            Redrawn?.Invoke();
        }
        PageChanged?.Invoke();
        _ = PrepareNeighboursAsync(index, version);
        return true;
    }

    // Next and Previous count from the page last asked for, so two quick clicks while a page is still drawing move two pages.
    private int Current => _target >= 0 ? _target : PageIndex;

    public Task<bool> NextAsync() => Current + 1 < PageCount ? GoToAsync(Current + 1) : Task.FromResult(false);

    public Task<bool> PreviousAsync() => Current > 0 ? GoToAsync(Current - 1) : Task.FromResult(false);

    /// <summary>The render size changed: pages are drawn again at the new size, the page on screen first.</summary>
    internal void Resize(int canvasWidth, int canvasHeight)
    {
        if (_disposed) return;
        _logger.LogDebug("pdf: resize to {W}x{H} (page index {Index})", canvasWidth, canvasHeight, PageIndex);
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
        _cache.Clear();
        if (_document is null || PageIndex < 0) return;
        _redrawPending = true;
        _ = GoToAsync(PageIndex);
    }

    private async Task<Rendered?> GetAsync(int index, int version)
    {
        if (_cache.TryGetValue(index, out Rendered? cached) && cached.CanvasWidth == _canvasWidth && cached.CanvasHeight == _canvasHeight) return cached;
        await _renderLock.WaitAsync();
        try
        {
            // Waited for the lock behind other pages: if the request is already out of date, do not spend time on it.
            if (_document is null || _disposed || version != _version) return null;
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
    public async Task<PageThumbnail?> RenderThumbnailAsync(int index, int maxWidth, int maxHeight)
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
            return new PageThumbnail(pixels, width, height);
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

    private long CacheBytes() => _cache.Values.Sum(r => (long)r.Pixels.Length);

    private async Task PrepareNeighboursAsync(int index, int version)
    {
        // Keep only the pages around the one on screen so a long document does not fill the memory.
        foreach (int key in _cache.Keys.Where(k => k < index - PagesBefore || k > index + PagesAfter).ToList()) _cache.Remove(key);
        for (int next = index + 1; next <= index + PagesAfter && next < PageCount; next++)
        {
            if (version != _version || _disposed || CacheBytes() > CacheBudgetBytes) return;
            await GetAsync(next, version);
        }

        if (index - PagesBefore >= 0 && version == _version && !_disposed && CacheBytes() <= CacheBudgetBytes) await GetAsync(index - PagesBefore, version);
    }

    private void Show(Rendered rendered)
    {
        _logger.LogDebug("pdf: show page {Page} {W}x{H} at x={X} for canvas {CW}x{CH}", rendered.Index + 1, rendered.Width, rendered.Height, rendered.Layout.X, rendered.CanvasWidth, rendered.CanvasHeight);
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
