using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Stagecast.Core.Display;
using Stagecast.Core.Frames;
using Stagecast.Core.Rendering;
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
public sealed class RenderEngine : IDisposable
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
    private readonly List<PageTab> _tabs = [];
    private readonly SemaphoreSlim _switching = new(1, 1);
    private PageTab? _active;

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
    }

    public FrameHub Hub { get; }

    public FramePool Pool { get; }

    public Resolution Resolution { get; private set; }

    public int Fps { get; private set; }

    public IReadOnlyList<PageTab> Tabs => _tabs;

    /// <summary>The tab on the stage. Throws before the first tab was added.</summary>
    public PageTab Active => _active ?? throw new InvalidOperationException("The engine has no tab yet.");

    public CoreWebView2 Browser => Active.Browser;

    /// <summary>A copy of the page for the operator, about 15 times a second, raised on a capture thread.</summary>
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
        var fresh = await AddTabAsync(Guid.NewGuid(), old.Browser.Source, profileId);
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
        foreach (var tab in _tabs) profiles[tab.Browser.Profile.ProfilePath] = tab.Browser.Profile;

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
        foreach (var tab in _tabs) tab.Browser.Reload();
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

        var tab = new PageTab(id, visual, controller, profileId);
        Hook(tab);
        _tabs.Add(tab);
        // Every tab's visual stays on the stage for good and only one is switched on: adding or removing visuals while capturing
        // gave empty frames, switching a visual's own visibility does not.
        _stage.Children.InsertAtTop(visual);
        if (_active is null)
        {
            _active = tab;
            tab.HasBeenShown = true;
            ActiveTabChanged?.Invoke();
        }
        else
        {
            // Not on the output yet: its visual is off and the tab is muted. The browser itself keeps running and drawing,
            // because a page that was hidden does not repaint when shown again if it did not change (see docs/PLAN_PHASES.md A3).
            visual.IsVisible = false;
            tab.Browser.IsMuted = true;
        }
        tab.IsLoading = true;
        tab.Browser.Navigate(url);
        _logger.LogInformation("Tab {Id} added ({Count} tabs)", id, _tabs.Count);
        return tab;
    }

    /// <summary>
    /// Puts a tab on the stage. A tab that was never shown first gets time to load and draw. Then the new visual is switched on
    /// underneath the old one and only raised once the compositor has drawn it, so the output never shows an empty picture.
    /// </summary>
    public async Task SelectAsync(PageTab tab)
    {
        await _switching.WaitAsync();
        try
        {
            if (ReferenceEquals(tab, _active) || !_tabs.Contains(tab)) return;
            var previous = _active;

            if (!tab.HasBeenShown)
            {
                await WaitForFirstLoadAsync(tab);
                await Task.Delay(WarmUpMilliseconds);
            }

            if (previous is not null)
            {
                previous.Browser.IsMuted = true;
                previous.HasKeyboardFocus = false;
            }
            tab.Browser.IsMuted = false;
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
            _logger.LogInformation("Tab {Id} is now on the stage", tab.Id);
        }
        finally
        {
            _switching.Release();
        }
        ActiveTabChanged?.Invoke();
        NavigationStateChanged?.Invoke();
        CursorChanged?.Invoke();
    }

    // A hidden tab keeps loading in the background; wait (bounded) for it so the stage does not get an empty picture.
    private static async Task WaitForFirstLoadAsync(PageTab tab)
    {
        var limit = DateTime.UtcNow.AddSeconds(5);
        while (tab.IsLoading && DateTime.UtcNow < limit) await Task.Delay(20);
    }

    /// <summary>Closes a tab that is not on the stage. Select another tab first when closing the active one.</summary>
    public void CloseTab(PageTab tab)
    {
        if (ReferenceEquals(tab, _active)) throw new InvalidOperationException("Select another tab before closing the active one.");
        if (!_tabs.Remove(tab)) return;
        _stage.Children.Remove(tab.Visual);
        tab.Controller.Close();
        tab.Visual.Dispose();
        _logger.LogInformation("Tab {Id} closed ({Count} tabs)", tab.Id, _tabs.Count);
        if (tab.ProfileId is { } profile && !_tabs.Any(t => t.ProfileId == profile))
        {
            _environments.Remove(profile);
            _ = DeleteFolderAsync(ProfileFolder(_options.ProfilesDirectory, profile));
        }
    }

    public PageTab? TabById(Guid id) => _tabs.FirstOrDefault(t => t.Id == id);

    /// <summary>Keeps the engine's tab order equal to the operator's list; only affects <see cref="Tabs"/>.</summary>
    public void SortTabs(IReadOnlyList<Guid> order)
    {
        var sorted = order.Select(TabById).OfType<PageTab>().ToList();
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
        tab.Browser.SourceChanged += (_, _) => Navigated();
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
        => Active.Controller.SendMouseInput(kind, keys, data, pagePoint);

    /// <summary>Gives keyboard focus to the page. The application window must be the active window for keys to arrive.</summary>
    public void FocusPage() => Active.Controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);

    /// <summary>True while keystrokes go to the page.</summary>
    public bool PageHasFocus { get; private set; }

    public event Action<bool>? PageFocusChanged;

    /// <summary>Raised on the UI thread when Esc is pressed while the page has keyboard focus.</summary>
    public event Action? EscapePressed;

    /// <summary>The shape the mouse pointer should have over the page (a Windows IDC_ cursor id).</summary>
    public uint CursorId => Active.Controller.SystemCursorId;

    public event Action? CursorChanged;

    /// <summary>Page zoom (the Ctrl+scroll kind) of the active tab. It changes the viewport the page sees, so it affects the output too.</summary>
    public double PageZoom
    {
        get => Active.Controller.ZoomFactor;
        set => Active.Controller.ZoomFactor = Math.Clamp(value, 0.25, 5.0);
    }

    public bool CanGoBack => Browser.CanGoBack;

    public bool CanGoForward => Browser.CanGoForward;

    public void GoBack() => Browser.GoBack();

    public void GoForward() => Browser.GoForward();

    public void Reload() => Browser.Reload();

    /// <summary>Raised after the address or the back/forward history of the active tab changed, or another tab became active.</summary>
    public event Action? NavigationStateChanged;

    public string CurrentUrl => Browser.Source;

    public Task<string> EvaluateAsync(string script) => Browser.ExecuteScriptAsync(script);

    /// <summary>Loads a page in the active tab and waits until it has finished loading (or <paramref name="timeout"/> passes).</summary>
    public Task<bool> NavigateAsync(string url, TimeSpan timeout) => NavigateAsync(Active, url, timeout);

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
        _stage.Size = new Vector2(resolution.Width, resolution.Height);
        foreach (var tab in _tabs)
        {
            tab.Visual.Size = new Vector2(resolution.Width, resolution.Height);
            tab.Controller.Bounds = new System.Drawing.Rectangle(0, 0, resolution.Width, resolution.Height);
        }
        Resolution = resolution;
        _capturer.Restart();
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
        _pump.Dispose();
        _capturer.Dispose();
        Hub.Clear();
        foreach (var tab in _tabs)
        {
            tab.Controller.Close();
            tab.Visual.Dispose();
        }
        _tabs.Clear();
        _stage.Dispose();
        _compositor.Dispose();
        if (_dispatcherQueueController != IntPtr.Zero) Marshal.Release(_dispatcherQueueController);
    }
}
