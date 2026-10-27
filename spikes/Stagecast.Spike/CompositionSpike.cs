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
    public async Task InitializeAsync(IntPtr parentHwnd, string userDataFolder, string url, Resolution css)
    {
        // Composition needs a DispatcherQueue on the thread that creates the Compositor.
        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = 2,     // DQTYPE_THREAD_CURRENT
            apartmentType = 0,  // DQTAT_COM_NONE: WPF already initialised the STA
        };
        int hr = CreateDispatcherQueueController(options, out _dispatcherQueueController);
        SpikeLog.Write($"dispatcher queue hr=0x{hr:X8}");
        Marshal.ThrowExceptionForHR(hr);

        _compositor = new Compositor();
        _root = _compositor.CreateContainerVisual();
        _root.Size = new Vector2(css.Width, css.Height);
        SpikeLog.Write($"compositor ok, root visual {css.Width}x{css.Height}");

        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
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

    public void Dispose()
    {
        _controller?.Close();
        _root?.Dispose();
        _compositor?.Dispose();
        if (_dispatcherQueueController != IntPtr.Zero) Marshal.Release(_dispatcherQueueController);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
}
