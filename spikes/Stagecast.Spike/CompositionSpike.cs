using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Stagecast.Core.Rendering;
using Windows.UI.Composition;

namespace Stagecast.Spike;

/// <summary>
/// WebView2 in visual-hosting (composition) mode. The page is rendered into a Windows.UI.Composition
/// ContainerVisual that is never attached to a window, so nothing about the page is visible on screen.
/// S2 proves it loads; S3 proves the page's viewport follows the size we choose, at several display scales.
/// </summary>
internal sealed class CompositionSpike : IDisposable
{
    private IntPtr _dispatcherQueueController;
    private Compositor? _compositor;
    private ContainerVisual? _root;
    private CoreWebView2CompositionController? _controller;

    private CoreWebView2 Core => _controller!.CoreWebView2;

    /// <summary>Creates the compositor, the hosted browser and loads <paramref name="url"/> at the given CSS size.</summary>
    public async Task InitializeAsync(IntPtr parentHwnd, string userDataFolder, string url, Resolution css, string? browserArguments = null)
    {
        _dispatcherQueueController = DispatcherQueueHelper.Create();

        _compositor = new Compositor();
        _root = _compositor.CreateContainerVisual();
        _root.Size = new Vector2(css.Width, css.Height);
        SpikeLog.Write($"compositor ok, root visual {css.Width}x{css.Height}");

        var envOptions = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = browserArguments };
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder, options: envOptions);
        SpikeLog.Write($"browser arguments: {browserArguments ?? "(none)"}");
        SpikeLog.Write($"webview2 runtime {env.BrowserVersionString}");

        _controller = await env.CreateCoreWebView2CompositionControllerAsync(parentHwnd);
        SpikeLog.Write("composition controller created");

        _controller.RootVisualTarget = _root;
        // The monitor must not decide the scale: the app chooses it (S3 simulates Windows display scaling this way).
        _controller.ShouldDetectMonitorScaleChanges = false;
        _controller.RasterizationScale = 1.0;
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, css.Width, css.Height);
        SpikeLog.Write($"root visual target set, bounds {css.Width}x{css.Height}");

        var done = new TaskCompletionSource<bool>();
        // Policy under test in S6: a popup (e.g. "Sign in with Google") is opened in the same tab instead of a new window.
        Core.NewWindowRequested += (_, e) =>
        {
            SpikeLog.Write($"popup requested uri={e.Uri} isUserInitiated={e.IsUserInitiated} -> same tab");
            e.Handled = true;
            Core.Navigate(e.Uri);
        };
        Core.NavigationStarting += (_, e) => SpikeLog.Write($"navigating {e.Uri}");
        Core.NavigationCompleted += (_, e) =>
        {
            SpikeLog.Write($"navigation completed success={e.IsSuccess} status={e.WebErrorStatus} title='{Core.DocumentTitle}' source={Core.Source}");
            done.TrySetResult(e.IsSuccess);
        };
        Core.Navigate(url);

        var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        if (finished != done.Task) throw new TimeoutException("no NavigationCompleted within 20 s");
        if (!done.Task.Result) throw new InvalidOperationException("navigation failed");
    }

    /// <summary>S2: ask the page for its own idea of the viewport.</summary>
    public async Task LogViewportAsync()
    {
        var (w, h, dpr) = await ReadViewportAsync();
        SpikeLog.Write($"page viewport {w}x{h} dpr={dpr}");
    }

    /// <summary>
    /// S3: for every display scale and every target resolution, resize the live browser (no recreation) and check that
    /// the page reports exactly the target CSS viewport and the chosen devicePixelRatio.
    /// </summary>
    public async Task<(int Pass, int Fail)> RunMatrixAsync()
    {
        double[] scales = [1.0, 1.25, 1.5, 2.0];
        Resolution[] sizes = [new(2520, 1080), new(1920, 1080), new(3840, 1080), new(1080, 1920)];
        int pass = 0, fail = 0;

        foreach (double scale in scales)
        {
            foreach (var css in sizes)
            {
                var (pw, ph) = RenderSurface.PhysicalSize(css, scale);
                _controller!.RasterizationScale = scale;
                _controller.Bounds = new System.Drawing.Rectangle(0, 0, pw, ph);

                var (w, h, dpr) = await WaitForViewportAsync(css.Width, css.Height, scale);
                bool ok = w == css.Width && h == css.Height && Math.Abs(dpr - scale) < 0.001;
                if (ok) pass++; else fail++;
                SpikeLog.Write($"case scale={scale} css={css.Width}x{css.Height} bounds={pw}x{ph} -> page {w}x{h} dpr={dpr} {(ok ? "PASS" : "FAIL")}");
            }
        }

        SpikeLog.Write($"S3 summary pass={pass} fail={fail}");
        return (pass, fail);
    }

    // The resize reaches the renderer asynchronously, so poll until it matches (or give up after 3 s and report what we saw).
    private async Task<(int W, int H, double Dpr)> WaitForViewportAsync(int w, int h, double dpr)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        (int W, int H, double Dpr) last;
        do
        {
            last = await ReadViewportAsync();
            if (last.W == w && last.H == h && Math.Abs(last.Dpr - dpr) < 0.001) break;
            await Task.Delay(50);
        } while (DateTime.UtcNow < deadline);
        return last;
    }

    private async Task<(int W, int H, double Dpr)> ReadViewportAsync()
    {
        string json = await Core.ExecuteScriptAsync("JSON.stringify([window.innerWidth, window.innerHeight, window.devicePixelRatio])");
        var values = JsonSerializer.Deserialize<double[]>(JsonSerializer.Deserialize<string>(json)!)!;
        return ((int)values[0], (int)values[1], values[2]);
    }

    /// <summary>S4: capture one frame of the hosted page and save it as a PNG.</summary>
    public async Task<(int Width, int Height)> CaptureFrameAsync(string pngPath)
    {
        // Give the page a moment to paint before the first capture.
        await Task.Delay(1500);
        return await FrameCapture.CaptureOnceAsync(_root!, pngPath);
    }

    /// <summary>S5: keep capturing for <paramref name="seconds"/>, optionally switching 60 -> 30 fps halfway, and return the statistics.</summary>
    public async Task<(CaptureSummary First, CaptureSummary? Second)> RunContinuousAsync(
        int seconds, bool switchHalfway, Action<byte[], int, int, int>? onPreview)
    {
        await Task.Delay(1500);
        using var capture = new ContinuousCapture(_root!, 60, onPreview);
        capture.Start();
        if (!switchHalfway)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            return (capture.Summarize(), null);
        }
        await Task.Delay(TimeSpan.FromSeconds(seconds / 2.0));
        SpikeLog.Write($"page says {await Core.ExecuteScriptAsync("document.getElementById('info') ? document.getElementById('info').textContent : 'n/a'")}");
        var first = capture.Summarize();
        capture.SetTargetFps(30);
        await Task.Delay(TimeSpan.FromSeconds(seconds / 2.0));
        return (first, capture.Summarize());
    }

    public void SetMuted(bool muted) => Core.IsMuted = muted;

    internal CoreWebView2CompositionController Controller => _controller!;

    public int BrowserProcessId => (int)Core.BrowserProcessId;

    public Task<string> EvalAsync(string script) => Core.ExecuteScriptAsync(script);

    public string CurrentUrl => Core.Source;

    public void Navigate(string url) => Core.Navigate(url);

    /// <summary>Forwards a mouse event from the preview into the hosted page (page coordinates, scale 1.0).</summary>
    public void SendMouse(CoreWebView2MouseEventKind kind, CoreWebView2MouseEventVirtualKeys keys, uint data, System.Drawing.Point point)
        => _controller!.SendMouseInput(kind, keys, data, point);

    public void FocusBrowser() => _controller!.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);

    /// <summary>Logs how many cookies the profile holds for a site (values are never logged), to show a login survived a restart.</summary>
    public async Task LogCookiesAsync(string site)
    {
        var cookies = await Core.CookieManager.GetCookiesAsync(site);
        SpikeLog.Write($"cookies for {site}: {cookies.Count} (session={cookies.Count(c => c.IsSession)}, persistent={cookies.Count(c => !c.IsSession)})");
    }

    public ContinuousCapture StartPreview(Action<byte[], int, int, int> onPreview, int intervalMs)
    {
        var capture = new ContinuousCapture(_root!, 30, onPreview, intervalMs);
        capture.Start();
        return capture;
    }

    /// <summary>S6 media check: let the video page play, log what the page reports, and save a frame to judge colour.</summary>
    public async Task<string> RunVideoCheckAsync(int seconds, string pngPath)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds / 2.0));
        await FrameCapture.CaptureOnceAsync(_root!, pngPath);
        await Task.Delay(TimeSpan.FromSeconds(seconds / 2.0));
        string json = JsonSerializer.Deserialize<string>(await Core.ExecuteScriptAsync("window.report()"))!;
        SpikeLog.Write($"video report {json}");
        return json;
    }

    public void Dispose()
    {
        _controller?.Close();
        _root?.Dispose();
        _compositor?.Dispose();
        if (_dispatcherQueueController != IntPtr.Zero) Marshal.Release(_dispatcherQueueController);
    }
}
