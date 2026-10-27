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
/// <param name="UserDataFolder">Where the browser profile (cookies, logins) lives.</param>
/// <param name="BrowserArguments">Extra Chromium switches. The default lets pages play sound without a click.</param>
public sealed record RenderEngineOptions(
    IntPtr WindowHandle, string UserDataFolder, Resolution Resolution, int Fps,
    string BrowserArguments = "--autoplay-policy=no-user-gesture-required");

public sealed record RenderEngineStats(FrameHubStats Hub, CaptureStats Capture);

/// <summary>
/// Renders a web page at a chosen resolution, independent of any window, and publishes it as frames.
/// The page lives in a WebView2 that draws into a composition visual which is not shown anywhere; Windows Graphics Capture reads
/// that visual, the <see cref="FrameCapturer"/> picks frames, and the <see cref="FrameHub"/> repeats and fans them out at the
/// chosen rate. Resolution and rate can be changed while running. Create and use it on the UI thread.
/// </summary>
public sealed class RenderEngine : IDisposable
{
    private readonly ILogger<RenderEngine> _logger;
    private readonly IntPtr _dispatcherQueueController;
    private readonly Compositor _compositor;
    private readonly ContainerVisual _root;
    private readonly CoreWebView2CompositionController _controller;
    private readonly FrameCapturer _capturer;
    private readonly FramePump _pump;

    private RenderEngine(RenderEngineOptions options, IntPtr dispatcherQueueController, Compositor compositor, ContainerVisual root,
        CoreWebView2CompositionController controller, FrameHub hub, FramePool pool, FrameCapturer capturer, FramePump pump, ILogger<RenderEngine> logger)
    {
        _dispatcherQueueController = dispatcherQueueController;
        _compositor = compositor;
        _root = root;
        _controller = controller;
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

    public CoreWebView2 Browser => _controller.CoreWebView2;

    /// <summary>A small copy of the page, about 15 times a second, raised on a capture thread.</summary>
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
        var root = compositor.CreateContainerVisual();
        root.Size = new Vector2(options.Resolution.Width, options.Resolution.Height);

        var env = await CoreWebView2Environment.CreateAsync(
            userDataFolder: options.UserDataFolder,
            options: new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = options.BrowserArguments });
        logger.LogInformation("WebView2 runtime {Version}, profile {Profile}", env.BrowserVersionString, options.UserDataFolder);

        var controller = await env.CreateCoreWebView2CompositionControllerAsync(options.WindowHandle);
        controller.RootVisualTarget = root;
        // The display scale must never decide the render size: we choose it, always 1.0 (see S3).
        controller.ShouldDetectMonitorScaleChanges = false;
        controller.RasterizationScale = 1.0;
        controller.Bounds = new System.Drawing.Rectangle(0, 0, options.Resolution.Width, options.Resolution.Height);

        var hub = new FrameHub();
        var pool = new FramePool();
        var capturer = new FrameCapturer(root, hub, pool, options.Fps, TimeProvider.System, loggerFactory.CreateLogger<FrameCapturer>());
        capturer.Start();
        var pump = new FramePump(hub, options.Fps, loggerFactory.CreateLogger<FramePump>());
        var engine = new RenderEngine(options, queue, compositor, root, controller, hub, pool, capturer, pump, logger);
        engine.HookEvents();
        return engine;
    }

    /// <summary>What the next previews should contain (see <see cref="PreviewView.Region"/>).</summary>
    public PreviewRegion? PreviewTarget
    {
        get => _capturer.PreviewTarget;
        set => _capturer.PreviewTarget = value;
    }

    // ---- Operating the page from the preview ----

    /// <summary>Forwards a mouse event to the page. The point is in page pixels (CSS pixels at page zoom 100%).</summary>
    public void SendMouse(CoreWebView2MouseEventKind kind, CoreWebView2MouseEventVirtualKeys keys, uint data, System.Drawing.Point pagePoint)
        => _controller.SendMouseInput(kind, keys, data, pagePoint);

    /// <summary>Gives keyboard focus to the page. The application window must be the active window for keys to arrive.</summary>
    public void FocusPage() => _controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);

    /// <summary>True while keystrokes go to the page.</summary>
    public bool PageHasFocus { get; private set; }

    public event Action<bool>? PageFocusChanged;

    /// <summary>Raised on the UI thread when Esc is pressed while the page has keyboard focus. Set <c>handled</c> to keep it from the page.</summary>
    public event Action? EscapePressed;

    /// <summary>The shape the mouse pointer should have over the page (a Windows IDC_ cursor id).</summary>
    public uint CursorId => _controller.SystemCursorId;

    public event Action? CursorChanged;

    /// <summary>Page zoom (the Ctrl+scroll kind). It changes the viewport the page sees, so it affects the output too.</summary>
    public double PageZoom
    {
        get => _controller.ZoomFactor;
        set => _controller.ZoomFactor = Math.Clamp(value, 0.25, 5.0);
    }

    public bool CanGoBack => Browser.CanGoBack;

    public bool CanGoForward => Browser.CanGoForward;

    public void GoBack() => Browser.GoBack();

    public void GoForward() => Browser.GoForward();

    public void Reload() => Browser.Reload();

    /// <summary>Raised after the address or the back/forward history changed.</summary>
    public event Action? NavigationStateChanged;

    public string CurrentUrl => Browser.Source;

    public Task<string> EvaluateAsync(string script) => Browser.ExecuteScriptAsync(script);

    /// <summary>Loads a page and waits until it has finished loading (or <paramref name="timeout"/> passes).</summary>
    public async Task<bool> NavigateAsync(string url, TimeSpan timeout)
    {
        var done = new TaskCompletionSource<bool>();
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult(e.IsSuccess);
        Browser.NavigationCompleted += Handler;
        try
        {
            Browser.Navigate(url);
            var finished = await Task.WhenAny(done.Task, Task.Delay(timeout));
            bool ok = finished == done.Task && done.Task.Result;
            _logger.LogInformation("Navigation to {Url} {Result}", url, ok ? "completed" : "failed or timed out");
            return ok;
        }
        finally
        {
            Browser.NavigationCompleted -= Handler;
        }
    }

    /// <summary>Changes the page's viewport while it runs. Frames of the old size keep flowing until the first new one is captured.</summary>
    public void SetResolution(Resolution resolution)
    {
        if (resolution == Resolution) return;
        _root.Size = new Vector2(resolution.Width, resolution.Height);
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, resolution.Width, resolution.Height);
        Resolution = resolution;
        _capturer.Restart();
        _logger.LogInformation("Resolution set to {Resolution}", resolution);
    }

    private void HookEvents()
    {
        _controller.GotFocus += (_, _) => SetPageFocus(true);
        _controller.LostFocus += (_, _) => SetPageFocus(false);
        _controller.CursorChanged += (_, _) => CursorChanged?.Invoke();
        _controller.AcceleratorKeyPressed += (_, e) =>
        {
            if (e.VirtualKey == 0x1B && e.KeyEventKind is CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown && EscapePressed is { } handler)
            {
                e.Handled = true;
                handler();
            }
        };
        Browser.SourceChanged += (_, _) => NavigationStateChanged?.Invoke();
        Browser.HistoryChanged += (_, _) => NavigationStateChanged?.Invoke();
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
        _controller.Close();
        _root.Dispose();
        _compositor.Dispose();
        if (_dispatcherQueueController != IntPtr.Zero) Marshal.Release(_dispatcherQueueController);
    }
}
