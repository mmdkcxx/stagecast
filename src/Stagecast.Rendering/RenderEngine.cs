using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Stagecast.Core.Display;
using Stagecast.Core.Frames;
using Stagecast.Core.Rendering;
using Stagecast.Core.Tabs;
using Stagecast.Rendering.Capture;
using Stagecast.Rendering.Interop;
using Windows.UI.Composition;

namespace Stagecast.Rendering;

/// <param name="WindowHandle">Any top-level window of the application; WebView2 needs a parent window even when it draws into a visual.</param>
/// <param name="ProfilesDirectory">Where the browser profiles (cookies, logins) live: <c>default</c> for the shared one, <c>tab-&lt;id&gt;</c> for each separate one.</param>
/// <param name="BrowserArguments">Extra Chromium switches. The default lets pages play sound without a click.</param>
public sealed record RenderEngineOptions(
    IntPtr WindowHandle, string ProfilesDirectory, Resolution Resolution, int Fps,
    string BrowserArguments = "--autoplay-policy=no-user-gesture-required");

public sealed record RenderEngineStats(FrameHubStats Hub, CaptureStats Capture);

/// <summary>
/// Renders web pages at a chosen resolution, independent of any window, and publishes them as frames.
/// Each page (<see cref="PageTab"/>) is a WebView2 that draws into its own composition visual which is not shown anywhere. A
/// constant "stage" visual holds the active tab's visual; Windows Graphics Capture reads the stage, so switching tabs never
/// restarts the capture. The <see cref="FrameCapturer"/> picks frames and the <see cref="FrameHub"/> repeats and fans them out at
/// the chosen rate. Resolution, rate and tabs can be changed while running. Create and use it on the UI thread.
/// </summary>
public sealed partial class RenderEngine : IDisposable
{
    private const string SharedProfileKey = "default";

    /// <summary>How long a tab that is about to be shown gets to draw itself before it replaces the one on the stage.</summary>
    private const int WarmUpMilliseconds = 150;

    /// <summary>How long the new visual is on underneath the old one before it takes over.</summary>
    private const int OverlapMilliseconds = 80;

    private readonly ILogger<RenderEngine> _logger;
    private readonly IntPtr _dispatcherQueueController;
    private readonly Compositor _compositor;
    private readonly ContainerVisual _stage;
    private readonly Dictionary<string, CoreWebView2Environment> _environments = [];
    private readonly RenderEngineOptions _options;
    private readonly IntPtr _windowHandle;
    private readonly FrameCapturer _capturer;
    private readonly FramePump _pump;
    private readonly List<StageTab> _tabs = [];
    private DocumentSurfaces? _documentSurfaces;
    private readonly SemaphoreSlim _switching = new(1, 1);
    private StageTab? _active;  // the tab the operator works on: the program in Direct mode, the preview in Preview + Take
    private StageTab? _program; // the tab on air while in Preview + Take; null in Direct mode

    private RenderEngine(RenderEngineOptions options, IntPtr dispatcherQueueController, Compositor compositor, ContainerVisual stage,
        CoreWebView2Environment sharedEnvironment, FrameHub hub, FramePool pool, FrameCapturer capturer, FramePump pump, ILogger<RenderEngine> logger)
    {
        _dispatcherQueueController = dispatcherQueueController;
        _compositor = compositor;
        _stage = stage;
        _options = options;
        _environments[SharedProfileKey] = sharedEnvironment;
        _windowHandle = options.WindowHandle;
        Hub = hub;
        Pool = pool;
        _capturer = capturer;
        _pump = pump;
        _logger = logger;
        Resolution = options.Resolution;
        Fps = options.Fps;
        Hub.SetPatternFormat(Resolution.Width, Resolution.Height, Fps);
    }

    public FrameHub Hub { get; }

    public FramePool Pool { get; }

    public Resolution Resolution { get; private set; }

    public int Fps { get; private set; }

    /// <summary>Every tab, web pages and files, in the operator's order.</summary>
    public IReadOnlyList<StageTab> Tabs => _tabs;

    /// <summary>The tab on the stage. Throws before the first tab was added.</summary>
    public StageTab? Active => _active;

    /// <summary>The tab on the stage as a web page. Throws when there is no tab or the tab is a file.</summary>
    public PageTab ActivePage => _active as PageTab ?? throw new InvalidOperationException("The active tab is not a web page.");

    public CoreWebView2 Browser => ActivePage.Browser;

    /// <summary>A copy of the page for the operator, at the output's frame rate (one per frame sent), raised on a capture thread.</summary>
    public event Action<PreviewImage>? PreviewReady
    {
        add => _capturer.PreviewReady += value;
        remove => _capturer.PreviewReady -= value;
    }

    public static async Task<RenderEngine> CreateAsync(RenderEngineOptions options, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger<RenderEngine>();
        var queue = DispatcherQueueHelper.Create();
        var compositor = new Compositor();
        var stage = compositor.CreateContainerVisual();
        stage.Size = new Vector2(options.Resolution.Width, options.Resolution.Height);

        string sharedFolder = ProfileFolder(options.ProfilesDirectory, null);
        var env = await CoreWebView2Environment.CreateAsync(
            userDataFolder: sharedFolder,
            options: new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = options.BrowserArguments });
        logger.LogInformation("WebView2 runtime {Version}, profile {Profile}", env.BrowserVersionString, sharedFolder);

        var hub = new FrameHub();
        var pool = new FramePool();
        var capturer = new FrameCapturer(stage, hub, pool, options.Fps, TimeProvider.System, loggerFactory.CreateLogger<FrameCapturer>());
        hub.FramesWanted += capturer.RequestFrame;
        capturer.Start();
        var pump = new FramePump(hub, options.Fps, loggerFactory.CreateLogger<FramePump>());
        return new RenderEngine(options, queue, compositor, stage, env, hub, pool, capturer, pump, logger);
    }

    // ---- Profiles (logins) ----

    private static string ProfileFolder(string profilesDirectory, string? profileId)
        => Path.Combine(profilesDirectory, profileId is null ? SharedProfileKey : "tab-" + profileId);

    private async Task<CoreWebView2Environment> EnvironmentForAsync(string? profileId)
    {
        string key = profileId ?? SharedProfileKey;
        if (_environments.TryGetValue(key, out var existing)) return existing;
        var env = await CoreWebView2Environment.CreateAsync(
            userDataFolder: ProfileFolder(_options.ProfilesDirectory, profileId),
            options: new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = _options.BrowserArguments });
        _environments[key] = env;
        _logger.LogInformation("Separate profile {Profile} created", key);
        return env;
    }

    /// <summary>
    /// Gives an existing tab another profile (or the shared one with <c>null</c>): a new browser is created, the page is loaded
    /// again there, and it takes the old tab's place. The old browser, and the old separate profile's data, are discarded.
    /// </summary>
    public async Task<PageTab> ReplaceProfileAsync(PageTab old, string? profileId)
    {
        int position = _tabs.IndexOf(old);
        if (position < 0) throw new ArgumentException("The tab is not part of this engine.", nameof(old));
        bool wasActive = ReferenceEquals(old, _active);
        var fresh = await AddTabAsync(Guid.NewGuid(), old.FailedUrl ?? old.LastUrl, profileId);
        _tabs.Remove(fresh);
        _tabs.Insert(position, fresh);
        if (wasActive) await SelectAsync(fresh);
        Guid id = old.Id;
        CloseTab(old);
        fresh.Id = id;
        return fresh;
    }

    /// <summary>Signs out of every site in every profile (the shared one and all separate ones), then reloads the pages. Returns how many profiles were cleared.</summary>
    public async Task<int> ClearLoginDataAsync()
    {
        var profiles = new Dictionary<string, CoreWebView2Profile>(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in _tabs.OfType<PageTab>()) profiles[tab.Browser.Profile.ProfilePath] = tab.Browser.Profile;

        // The shared profile may have no tab at the moment (all tabs on separate profiles): borrow a browser just to reach it.
        string sharedPath = Path.GetFullPath(ProfileFolder(_options.ProfilesDirectory, null));
        if (!profiles.Keys.Any(k => string.Equals(Path.GetFullPath(k), sharedPath, StringComparison.OrdinalIgnoreCase)))
        {
            var borrowed = await _environments[SharedProfileKey].CreateCoreWebView2CompositionControllerAsync(_windowHandle);
            try
            {
                await borrowed.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllSite);
            }
            finally
            {
                borrowed.Close();
            }
        }
        foreach (var profile in profiles.Values) await profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllSite);
        foreach (var tab in _tabs.OfType<PageTab>()) tab.Browser.Reload();
        _logger.LogInformation("Login data cleared in {Count} profile(s)", profiles.Count);
        return profiles.Count;
    }

    /// <summary>How many HttpOnly cookies (the kind used for sessions) the tab's site has. Cookie values are never read into the log.</summary>
    public async Task<int> SessionCookieCountAsync(PageTab tab)
    {
        if (!Uri.TryCreate(tab.Browser.Source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return 0;
        var cookies = await tab.Browser.CookieManager.GetCookiesAsync(uri.GetLeftPart(UriPartial.Authority));
        return cookies.Count(c => c.IsHttpOnly);
    }

    /// <summary>Adds a cookie to the tab's profile. Used by tests to stand in for a login.</summary>
    public void AddCookie(PageTab tab, string name, string value, string domain, DateTime? expires)
    {
        var cookie = tab.Browser.CookieManager.CreateCookie(name, value, domain, "/");
        cookie.IsHttpOnly = true;
        if (expires is { } e) cookie.Expires = e;
        tab.Browser.CookieManager.AddOrUpdateCookie(cookie);
    }

    public Task<IReadOnlyList<CoreWebView2Cookie>> CookiesAsync(PageTab tab, string uri) => GetCookiesAsync(tab, uri);

    private static async Task<IReadOnlyList<CoreWebView2Cookie>> GetCookiesAsync(PageTab tab, string uri)
        => (await tab.Browser.CookieManager.GetCookiesAsync(uri)).ToList();

    /// <summary>Deletes the folders of separate profiles that no tab refers to any more (left by a crash or an earlier session).</summary>
    public static void RemoveUnusedProfiles(string profilesDirectory, IEnumerable<string?> inUse)
    {
        if (!Directory.Exists(profilesDirectory)) return;
        var keep = inUse.OfType<string>().Select(id => "tab-" + id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in Directory.GetDirectories(profilesDirectory, "tab-*"))
        {
            if (!keep.Contains(Path.GetFileName(folder))) _ = DeleteFolderAsync(folder);
        }
    }

    // The browser process can hold files for a moment after its tab closed, so try again for a while.
    private static async Task DeleteFolderAsync(string folder)
    {
        for (int attempt = 0; attempt < 20 && Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(500);
            }
        }
    }

    // ---- Tabs ----

    /// <summary>Raised when the active tab changed.</summary>
    public event Action? ActiveTabChanged;

    /// <summary>Raised for any tab (not only the active one) after its address or title changed.</summary>
    public event Action<PageTab>? TabNavigated;

    /// <summary>
    /// Creates a tab and starts loading <paramref name="url"/>. The first tab goes on the stage at once; others stay hidden and
    /// muted until <see cref="SelectAsync"/>. Returns without waiting for the page.
    /// </summary>
    public async Task<PageTab> AddTabAsync(Guid id, string url, string? profileId = null)
    {
        var environment = await EnvironmentForAsync(profileId);
        var visual = _compositor.CreateContainerVisual();
        visual.Size = new Vector2(Resolution.Width, Resolution.Height);
        var controller = await environment.CreateCoreWebView2CompositionControllerAsync(_windowHandle);
        controller.RootVisualTarget = visual;
        // The display scale must never decide the render size: we choose it, always 1.0 (see S3).
        controller.ShouldDetectMonitorScaleChanges = false;
        controller.RasterizationScale = 1.0;
        controller.Bounds = new System.Drawing.Rectangle(0, 0, Resolution.Width, Resolution.Height);

        var tab = new PageTab(id, visual, controller, profileId) { Environment = environment, LastUrl = url };
        Hook(tab);
        Register(tab);
        tab.IsLoading = true;
        tab.Browser.Navigate(url);
        _logger.LogInformation("Tab {Id} added ({Count} tabs)", id, _tabs.Count);
        return tab;
    }

    /// <summary>
    /// Creates a tab for a file instead of a web page. It is shown, selected, taken and closed like any other tab. Until the
    /// sources that play files exist, its visual is a single colour (<paramref name="color"/>, a dark slate by default).
    /// </summary>
    public DocumentTab AddDocumentTab(Guid id, TabKind kind, string? source, string title, Windows.UI.Color? color = null)
    {
        if (!TabKinds.IsDocument(kind)) throw new ArgumentException("A web page is added with AddTabAsync.", nameof(kind));
        var visual = _compositor.CreateContainerVisual();
        visual.Size = new Vector2(Resolution.Width, Resolution.Height);
        var content = _compositor.CreateSpriteVisual();
        content.Size = visual.Size;
        content.Brush = _compositor.CreateColorBrush(color ?? Windows.UI.Color.FromArgb(255, 28, 34, 46));
        visual.Children.InsertAtTop(content);

        var tab = new DocumentTab(id, visual, content, kind, source, title);
        Register(tab);
        _logger.LogInformation("{Kind} tab {Id} added ({Count} tabs)", kind, id, _tabs.Count);
        return tab;
    }

    /// <summary>
    /// Gives a PDF tab the component that draws its pages. Open a file with <see cref="PdfSource.OpenAsync"/>; the tab's visual
    /// shows the page scaled to fit, with black bars.
    /// </summary>
    public PdfSource AttachPdf(DocumentTab tab)
    {
        if (tab.Kind != TabKind.Pdf) throw new ArgumentException("Only a PDF tab shows pages.", nameof(tab));
        if (tab.Pdf is not null) return tab.Pdf;
        PdfSource.CleanOldTempFiles();
        _documentSurfaces ??= new DocumentSurfaces(_compositor, _logger);
        tab.Content.Brush = BarBrush();
        tab.Pdf = new PdfSource(_compositor, _documentSurfaces, tab.Visual, Resolution.Width, Resolution.Height, _logger);
        // A still page makes no new frames: a capture restarted for a new size can keep the picture from just before the page was
        // drawn again, so once it is drawn the capture starts over and its first frame has the finished page.
        tab.Pdf.Redrawn += () =>
        {
            if (tab == _program || tab == _active) _capturer.Restart();
        };
        return tab.Pdf;
    }

    /// <summary>
    /// Gives a PowerPoint tab the component that plays its slides with the installed PowerPoint. Open a file with
    /// <see cref="PowerPointSource.OpenAsync"/>; the tab's visual shows the slide scaled to fit, with black bars.
    /// </summary>
    public PowerPointSource AttachPowerPoint(DocumentTab tab)
    {
        if (tab.Kind != TabKind.PowerPoint) throw new ArgumentException("Only a PowerPoint tab shows slides.", nameof(tab));
        if (tab.PowerPoint is not null) return tab.PowerPoint;
        PowerPointSource.CleanOrphanedPowerPoint(_logger);
        PowerPointSource.CleanOldTempFiles();
        _documentSurfaces ??= new DocumentSurfaces(_compositor, _logger);
        tab.Content.Brush = BarBrush();
        tab.PowerPoint = new PowerPointSource(_compositor, _documentSurfaces, tab.Visual, Resolution.Width, Resolution.Height, _logger);
        // Same as a PDF page: a still slide makes no new frames, so a capture started for a new size is started again once the slide is placed.
        tab.PowerPoint.Redrawn += () =>
        {
            if (tab == _program || tab == _active) _capturer.Restart();
        };
        return tab.PowerPoint;
    }

    private void Register(StageTab tab)
    {
        _tabs.Add(tab);
        // Every tab's visual stays on the stage for good and only one is switched on: adding or removing visuals while capturing
        // gave empty frames, switching a visual's own visibility does not.
        _stage.Children.InsertAtTop(tab.Visual);
        if (_active is null)
        {
            _active = tab;
            tab.HasBeenShown = true;
            ActiveTabChanged?.Invoke();
        }
        else
        {
            // Not on the output yet: its visual is off and the tab is muted. A web browser itself keeps running and drawing,
            // because a page that was hidden does not repaint when shown again if it did not change (see docs/PLAN_PHASES.md A3).
            tab.Visual.IsVisible = false;
            SetMuted(tab, true);
        }
    }

    /// <summary>
    /// Picks the tab the operator works on. In Direct mode that is also the tab on air; in Preview + Take it is the preview and
    /// the tab on air stays as it is. A tab that was never shown first gets time to load and draw. Then the new visual is switched
    /// on underneath the old one and only raised once the compositor has drawn it, so nothing ever shows an empty picture.
    /// </summary>
    public async Task SelectAsync(StageTab tab)
    {
        await _switching.WaitAsync();
        try
        {
            if (ReferenceEquals(tab, _active) || !_tabs.Contains(tab) || ReferenceEquals(tab, _program)) return;
            var previous = _active;
            var position = PreviewMode ? new Vector3(PreviewOriginX, 0, 0) : Vector3.Zero;

            if (!tab.HasBeenShown)
            {
                await WaitForFirstLoadAsync(tab);
                await Task.Delay(WarmUpMilliseconds);
            }

            if (previous is not null)
            {
                SetMuted(previous, true);
                previous.HasKeyboardFocus = false;
            }
            // Only the tab on air is heard: in Direct mode that is this one, in Preview + Take it is another.
            SetMuted(tab, PreviewMode);
            tab.Visual.Offset = position;
            // Overlap: switch the new visual on underneath the old one, give the compositor a few frames to draw it, and only then
            // raise it and drop the old one. The output never sees a visual that has not been composed yet.
            _stage.Children.Remove(tab.Visual);
            _stage.Children.InsertAtBottom(tab.Visual);
            tab.Visual.IsVisible = true;
            await Task.Delay(OverlapMilliseconds);
            _stage.Children.Remove(tab.Visual);
            _stage.Children.InsertAtTop(tab.Visual);
            if (previous is not null) previous.Visual.IsVisible = false;
            tab.HasBeenShown = true;
            _active = tab;
            SetPageFocus(false);
            _logger.LogInformation("Tab {Id} is now {Role}", tab.Id, PreviewMode ? "the preview" : "on the stage");
        }
        finally
        {
            _switching.Release();
        }
        ActiveTabChanged?.Invoke();
        NavigationStateChanged?.Invoke();
        CursorChanged?.Invoke();
    }

    // ---- Preview + Take ----

    /// <summary>True while the tab on air (<see cref="Program"/>) and the operator's tab (<see cref="Active"/>) are different tabs.</summary>
    /// <summary>The WebView2 browser processes in use (one per profile); the audio capture follows these. UI thread only.</summary>
    public IReadOnlyList<int> BrowserProcessIds => _tabs.OfType<PageTab>().Select(t => (int)t.Browser.BrowserProcessId).Distinct().ToList();

    /// <summary>
    /// Every WebView2 process of the pages in use: the browser, renderers, GPU and utility processes. It asks each distinct environment
    /// (one per profile), so WebView2 of other applications is not included. UI thread only.
    /// </summary>
    public IReadOnlyList<int> WebViewProcessIds()
    {
        var ids = new HashSet<int>(BrowserProcessIds);
        foreach (var environment in _tabs.OfType<PageTab>().Select(t => t.Environment).OfType<CoreWebView2Environment>().Distinct())
        {
            try
            {
                foreach (var info in environment.GetProcessInfos()) ids.Add(info.ProcessId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // The browser is restarting: the browser process ids above are all there is for now.
            }
        }
        return ids.ToList();
    }

    /// <summary>The processes whose sound is captured: the browsers, and the PowerPoint that plays a deck. UI thread only.</summary>
    public IReadOnlyList<int> AudioProcessIds => BrowserProcessIds
        .Concat(_tabs.OfType<DocumentTab>().Select(t => t.PowerPoint?.ProcessId ?? 0).Where(id => id > 0)).Distinct().ToList();

    /// <summary>
    /// The PowerPoint processes that must not be heard right now: a web tab is muted at its browser, but a deck's sound comes from
    /// the PowerPoint process, so it is let through only while a deck is the tab on air. UI thread only.
    /// </summary>
    public IReadOnlySet<int> SilentAudioProcessIds
    {
        get
        {
            var silent = _tabs.OfType<DocumentTab>().Select(t => t.PowerPoint?.ProcessId ?? 0).Where(id => id > 0).ToHashSet();
            if (Program is DocumentTab { PowerPoint.ProcessId: > 0 } onAir) silent.Remove(onAir.PowerPoint.ProcessId);
            return silent;
        }
    }

    private bool _transparentBars;

    /// <summary>
    /// When true the bars around a document that does not fill the frame are see-through instead of black, so an output that sends
    /// transparency (NDI or OMT with "Kirim alpha") shows what is behind them. Outputs that do not send it show them black: the
    /// stage picture is premultiplied, so dropping the alpha is the same as laying it on black. UI thread only.
    /// </summary>
    public bool TransparentBars
    {
        get => _transparentBars;
        set
        {
            if (_transparentBars == value) return;
            _transparentBars = value;
            foreach (var tab in _tabs.OfType<DocumentTab>().Where(t => t.Pdf is not null || t.PowerPoint is not null)) tab.Content.Brush = BarBrush();
            _logger.LogInformation("Bars around documents are {State}", value ? "transparent" : "black");
        }
    }

    private CompositionBrush? BarBrush() => _transparentBars ? null : _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));

    public bool PreviewMode { get; private set; }

    /// <summary>The tab on air. Same as <see cref="Active"/> in Direct mode.</summary>
    public StageTab? Program => _program ?? _active;

    /// <summary>The program, small, a few times a second, raised on a capture thread. Only in Preview + Take.</summary>
    public event Action<PreviewImage>? ProgramThumbReady
    {
        add => _capturer.ProgramThumbReady += value;
        remove => _capturer.ProgramThumbReady -= value;
    }

    // The preview sits to the right of the program on the stage, at an x that is a multiple of 16 so shrunk previews stay aligned.
    private int PreviewOriginX => (Resolution.Width + 15) / 16 * 16;

    /// <summary>
    /// Starts Preview + Take: the tab on air stays on air at the left of a wider stage, and <paramref name="preview"/> appears
    /// at the right for the operator. The output keeps its size; only the capture is restarted once.
    /// </summary>
    public async Task EnterPreviewModeAsync(StageTab preview)
    {
        await _switching.WaitAsync();
        try
        {
            if (PreviewMode || ReferenceEquals(preview, _active) || !_tabs.Contains(preview)) return;
            var program = _active!;
            if (!preview.HasBeenShown)
            {
                await WaitForFirstLoadAsync(preview);
                await Task.Delay(WarmUpMilliseconds);
            }
            _program = program;
            PreviewMode = true;
            SetMuted(preview, true);
            preview.Visual.IsVisible = true;
            _active = preview;
            preview.HasBeenShown = true;
            ApplyStageGeometry(restartCapture: true);
            SetPageFocus(false);
            _logger.LogInformation("Preview + Take on: program {Program}, preview {Preview}", program.Id, preview.Id);
        }
        finally
        {
            _switching.Release();
        }
        ActiveTabChanged?.Invoke();
        NavigationStateChanged?.Invoke();
        CursorChanged?.Invoke();
    }

    /// <summary>Back to Direct mode: the tab on air stays on air and becomes the operator's tab.</summary>
    public async Task ExitPreviewModeAsync()
    {
        await _switching.WaitAsync();
        try
        {
            if (!PreviewMode) return;
            var preview = _active!;
            var program = _program!;
            preview.Visual.IsVisible = false;
            SetMuted(preview, true);
            preview.HasKeyboardFocus = false;
            PreviewMode = false;
            _program = null;
            _active = program;
            ApplyStageGeometry(restartCapture: true);
            SetPageFocus(false);
            _logger.LogInformation("Preview + Take off: {Program} stays on air", program.Id);
        }
        finally
        {
            _switching.Release();
        }
        ActiveTabChanged?.Invoke();
        NavigationStateChanged?.Invoke();
        CursorChanged?.Invoke();
        RecoverDeferredTabs();
    }

    /// <summary>
    /// Puts the preview on air and the tab that was on air becomes the preview. Both are already drawn, so this only swaps their
    /// places on the stage (in one compositor change) and their sound; no capture is restarted and nothing is empty for a moment.
    /// </summary>
    public async Task TakeAsync()
    {
        await _switching.WaitAsync();
        try
        {
            if (!PreviewMode || ReferenceEquals(_active, _program)) return;
            var onAir = _program!;
            var next = _active!;
            next.Visual.Offset = Vector3.Zero;
            onAir.Visual.Offset = new Vector3(PreviewOriginX, 0, 0);
            SetMuted(onAir, true);
            SetMuted(next, false);
            onAir.HasKeyboardFocus = false;
            _program = next;
            _active = onAir;
            SetPageFocus(false);
            _logger.LogInformation("Take: {Next} is on air, {Old} is the preview", next.Id, onAir.Id);
        }
        finally
        {
            _switching.Release();
        }
        ActiveTabChanged?.Invoke();
        NavigationStateChanged?.Invoke();
        CursorChanged?.Invoke();
    }

    /// <summary>Sizes the stage and places the program (and the preview) for the current resolution and mode.</summary>
    private void ApplyStageGeometry(bool restartCapture)
    {
        int width = Resolution.Width, height = Resolution.Height, originX = PreviewOriginX;
        _stage.Size = new Vector2(PreviewMode ? originX + width : width, height);
        foreach (var tab in _tabs) tab.Resize(width, height);
        if (PreviewMode)
        {
            _program!.Visual.Offset = Vector3.Zero;
            _active!.Visual.Offset = new Vector3(originX, 0, 0);
            _capturer.Layout = new StageLayout(0, 0, width, height, originX, 0);
        }
        else
        {
            if (_active is not null) _active.Visual.Offset = Vector3.Zero;
            _capturer.Layout = null;
        }
        if (restartCapture) _capturer.Restart();
    }

    // A hidden tab keeps loading in the background; wait (bounded) for it so the stage does not get an empty picture.
    private static async Task WaitForFirstLoadAsync(StageTab tab)
    {
        var limit = DateTime.UtcNow.AddSeconds(5);
        while (tab.IsLoading && DateTime.UtcNow < limit) await Task.Delay(20);
    }

    /// <summary>
    /// Takes the tab on air off the stage when it is the only one (or the operator wants none): afterwards no tab is active and the
    /// stage is empty, so the outputs show the test pattern. The tab itself is not closed; call <see cref="CloseTab"/> next.
    /// </summary>
    public async Task ReleaseActiveAsync()
    {
        await _switching.WaitAsync();
        try
        {
            if (_active is not { } tab || PreviewMode) return;
            tab.Visual.IsVisible = false;
            SetMuted(tab, true);
            tab.HasKeyboardFocus = false;
            _active = null;
            SetPageFocus(false);
            _logger.LogInformation("Tab {Id} left the stage: nothing is on air", tab.Id);
        }
        finally
        {
            _switching.Release();
        }
        ActiveTabChanged?.Invoke();
        NavigationStateChanged?.Invoke();
        CursorChanged?.Invoke();
    }

    /// <summary>
    /// Ends Preview + Take by dropping the tab on air (it is about to be closed): the preview tab stays selected, as the only tab
    /// in Direct mode, and nothing is on air until the operator chooses a tab.
    /// </summary>
    public async Task DropProgramAsync()
    {
        await _switching.WaitAsync();
        try
        {
            if (!PreviewMode || _program is not { } program || _active is not { } preview) return;
            program.Visual.IsVisible = false;
            SetMuted(program, true);
            program.HasKeyboardFocus = false;
            PreviewMode = false;
            _program = null;
            SetMuted(preview, false);
            ApplyStageGeometry(restartCapture: true);
            SetPageFocus(false);
            _logger.LogInformation("Preview + Take off: the tab on air {Program} was dropped", program.Id);
        }
        finally
        {
            _switching.Release();
        }
        ActiveTabChanged?.Invoke();
        NavigationStateChanged?.Invoke();
        CursorChanged?.Invoke();
    }

    /// <summary>
    /// Closes a tab that is not on the stage. Select another tab first when closing the active one. The folder of a separate profile
    /// (a login) goes with its last tab unless <paramref name="keepProfile"/> is set, as when a session is closed to open another.
    /// </summary>
    public void CloseTab(StageTab tab, bool keepProfile = false)
    {
        if (ReferenceEquals(tab, _active) || ReferenceEquals(tab, _program)) throw new InvalidOperationException("Select another tab or release this one before closing it (and drop it first if it is on air).");
        if (!_tabs.Remove(tab)) return;
        _stage.Children.Remove(tab.Visual);
        try
        {
            tab.Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // The browser behind the tab was already gone.
        }
        tab.Visual.Dispose();
        _logger.LogInformation("Tab {Id} closed ({Count} tabs)", tab.Id, _tabs.Count);
        if (tab.ProfileId is { } profile && !_tabs.Any(t => t.ProfileId == profile))
        {
            _environments.Remove(profile);
            if (!keepProfile) _ = DeleteFolderAsync(ProfileFolder(_options.ProfilesDirectory, profile));
        }
    }

    public StageTab? TabById(Guid id) => _tabs.FirstOrDefault(t => t.Id == id);

    /// <summary>The web page with this id; null when there is none or the tab is a file.</summary>
    public PageTab? PageTabById(Guid id) => TabById(id) as PageTab;

    /// <summary>Keeps the engine's tab order equal to the operator's list; only affects <see cref="Tabs"/>.</summary>
    public void SortTabs(IReadOnlyList<Guid> order)
    {
        var sorted = order.Select(TabById).OfType<StageTab>().ToList();
        if (sorted.Count != _tabs.Count) return;
        _tabs.Clear();
        _tabs.AddRange(sorted);
    }

    private void Hook(PageTab tab)
    {
        var controller = tab.Controller;
        controller.GotFocus += (_, _) => { tab.HasKeyboardFocus = true; if (ReferenceEquals(tab, _active)) SetPageFocus(true); };
        controller.LostFocus += (_, _) => { tab.HasKeyboardFocus = false; if (ReferenceEquals(tab, _active)) SetPageFocus(false); };
        controller.CursorChanged += (_, _) => { if (ReferenceEquals(tab, _active)) CursorChanged?.Invoke(); };
        controller.AcceleratorKeyPressed += (_, e) =>
        {
            if (!ReferenceEquals(tab, _active)) return;
            if (e.VirtualKey == 0x1B && e.KeyEventKind is CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown && EscapePressed is { } handler)
            {
                e.Handled = true;
                handler();
            }
        };
        void Navigated()
        {
            TabNavigated?.Invoke(tab);
            if (ReferenceEquals(tab, _active)) NavigationStateChanged?.Invoke();
        }
        tab.Browser.NavigationStarting += (_, _) => tab.IsLoading = true;
        tab.Browser.NavigationCompleted += (_, _) => tab.IsLoading = false;
        tab.Browser.SourceChanged += (_, _) => { tab.LastUrl = tab.Browser.Source; Navigated(); };
        HookRecovery(tab);
        tab.Browser.HistoryChanged += (_, _) => Navigated();
        tab.Browser.DocumentTitleChanged += (_, _) => Navigated();
        // A page that asks for a new window (a login popup, a link with target=_blank) is shown in the same tab instead of
        // opening a window the operator cannot see (S6).
        tab.Browser.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            tab.Browser.Navigate(e.Uri);
        };
    }

    // ---- Operating the active page from the preview ----

    /// <summary>What the next previews should contain (see <see cref="PreviewView.Region"/>).</summary>
    public PreviewRegion? PreviewTarget
    {
        get => _capturer.PreviewTarget;
        set => _capturer.PreviewTarget = value;
    }

    /// <summary>Forwards a mouse event to the page. The point is in page pixels (CSS pixels at page zoom 100%).</summary>
    public void SendMouse(CoreWebView2MouseEventKind kind, CoreWebView2MouseEventVirtualKeys keys, uint data, System.Drawing.Point pagePoint)
    {
        // WebView2 rejects mouse input with E_INVALIDARG in some transient states (page navigating, tab switching, window
        // deactivating). A lost mouse event is harmless; an exception on the UI thread is not.
        if (_active is not PageTab page) return; // a file has no page to click
        try { page.Controller.SendMouseInput(kind, keys, data, pagePoint); }
        catch (Exception ex) when (ex is ArgumentException or COMException or InvalidOperationException or ObjectDisposedException)
        {
            if (!_mouseErrorLogged)
            {
                _mouseErrorLogged = true;
                _logger.LogWarning(ex, "Mouse input ({Kind}) was rejected by WebView2; further occurrences are not logged", kind);
            }
        }
    }

    private bool _mouseErrorLogged;

    /// <summary>Gives keyboard focus to the page. The application window must be the active window for keys to arrive.</summary>
    public void FocusPage() => (_active as PageTab)?.Controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);

    /// <summary>True while keystrokes go to the page.</summary>
    public bool PageHasFocus { get; private set; }

    public event Action<bool>? PageFocusChanged;

    /// <summary>Raised on the UI thread when Esc is pressed while the page has keyboard focus.</summary>
    public event Action? EscapePressed;

    /// <summary>The shape the mouse pointer should have over the page (a Windows IDC_ cursor id).</summary>
    public uint CursorId => (_active as PageTab)?.Controller.SystemCursorId ?? ArrowCursorId;

    private const uint ArrowCursorId = 32512; // IDC_ARROW

    public event Action? CursorChanged;

    /// <summary>Page zoom (the Ctrl+scroll kind) of the active tab. It changes the viewport the page sees, so it affects the output too.</summary>
    public double PageZoom
    {
        get => (_active as PageTab)?.Controller.ZoomFactor ?? 1.0;
        set { if (_active is PageTab page) page.Controller.ZoomFactor = Math.Clamp(value, 0.25, 5.0); }
    }

    public bool CanGoBack => _active is PageTab page && page.Browser.CanGoBack;

    public bool CanGoForward => _active is PageTab page && page.Browser.CanGoForward;

    public void GoBack() => (_active as PageTab)?.Browser.GoBack();

    public void GoForward() => (_active as PageTab)?.Browser.GoForward();

    public void Reload() => (_active as PageTab)?.Browser.Reload();

    /// <summary>Raised after the address or the back/forward history of the active tab changed, or another tab became active.</summary>
    public event Action? NavigationStateChanged;

    public string CurrentUrl => (_active as PageTab)?.Browser.Source ?? string.Empty;

    public Task<string> EvaluateAsync(string script) => _active is PageTab page ? page.Browser.ExecuteScriptAsync(script) : Task.FromResult("null");

    /// <summary>Loads a page in the active tab and waits until it has finished loading (or <paramref name="timeout"/> passes).</summary>
    public Task<bool> NavigateAsync(string url, TimeSpan timeout) => NavigateAsync(ActivePage, url, timeout);

    public async Task<bool> NavigateAsync(PageTab tab, string url, TimeSpan timeout)
    {
        var done = new TaskCompletionSource<bool>();
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult(e.IsSuccess);
        tab.Browser.NavigationCompleted += Handler;
        try
        {
            tab.Browser.Navigate(url);
            var finished = await Task.WhenAny(done.Task, Task.Delay(timeout));
            bool ok = finished == done.Task && done.Task.Result;
            _logger.LogInformation("Navigation to {Url} {Result}", url, ok ? "completed" : "failed or timed out");
            return ok;
        }
        finally
        {
            tab.Browser.NavigationCompleted -= Handler;
        }
    }

    /// <summary>Changes the viewport of every page while it runs. Frames of the old size keep flowing until the first new one is captured.</summary>
    public void SetResolution(Resolution resolution)
    {
        if (resolution == Resolution) return;
        Resolution = resolution;
        Hub.SetPatternFormat(resolution.Width, resolution.Height, Fps);
        if (_active is null)
        {
            _stage.Size = new Vector2(resolution.Width, resolution.Height);
            _capturer.Restart();
        }
        else
        {
            ApplyStageGeometry(restartCapture: true);
        }
        _logger.LogInformation("Resolution set to {Resolution}", resolution);
    }

    private void SetPageFocus(bool focused)
    {
        if (PageHasFocus == focused) return;
        PageHasFocus = focused;
        PageFocusChanged?.Invoke(focused);
    }

    public void SetFps(int fps)
    {
        if (fps == Fps) return;
        Fps = fps;
        Hub.SetPatternFormat(Resolution.Width, Resolution.Height, fps);
        _capturer.TargetFps = fps;
        _pump.Fps = fps;
        Hub.ResetStats();
        _capturer.ResetStats();
        _logger.LogInformation("Frame rate set to {Fps}", fps);
    }

    public RenderEngineStats Stats() => new(Hub.Snapshot(), _capturer.Stats());

    public void ResetStats()
    {
        Hub.ResetStats();
        _capturer.ResetStats();
    }

    public void Dispose()
    {
        DisposeRecovery();
        _pump.Dispose();
        _capturer.Dispose();
        Hub.Clear();
        foreach (var tab in _tabs)
        {
            tab.Close();
            tab.Visual.Dispose();
        }
        _tabs.Clear();
        _stage.Dispose();
        _documentSurfaces?.Dispose();
        _compositor.Dispose();
        if (_dispatcherQueueController != IntPtr.Zero) Marshal.Release(_dispatcherQueueController);
    }
}
