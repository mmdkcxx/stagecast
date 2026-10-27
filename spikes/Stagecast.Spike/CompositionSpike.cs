using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Windows.UI.Composition;

namespace Stagecast.Spike;

/// <summary>
/// S2: WebView2 in visual-hosting (composition) mode. The page is rendered into a Windows.UI.Composition
/// ContainerVisual that is never attached to a window, so nothing about the page is visible on screen.
/// </summary>
internal sealed class CompositionSpike : IDisposable
{
    private IntPtr _dispatcherQueueController;
    private Compositor? _compositor;
    private ContainerVisual? _root;
    private CoreWebView2CompositionController? _controller;

    public async Task RunAsync(IntPtr parentHwnd, string userDataFolder, string url, int width, int height)
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
        _root.Size = new Vector2(width, height);
        SpikeLog.Write($"compositor ok, root visual {width}x{height}");

        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        SpikeLog.Write($"webview2 runtime {env.BrowserVersionString}");

        _controller = await env.CreateCoreWebView2CompositionControllerAsync(parentHwnd);
        SpikeLog.Write("composition controller created");

        _controller.RootVisualTarget = _root;
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, width, height);
        SpikeLog.Write($"root visual target set, bounds {width}x{height}");

        var done = new TaskCompletionSource<bool>();
        var core = _controller.CoreWebView2;
        core.NavigationStarting += (_, e) => SpikeLog.Write($"navigating {e.Uri}");
        core.NavigationCompleted += (_, e) =>
        {
            SpikeLog.Write($"navigation completed success={e.IsSuccess} status={e.WebErrorStatus} title='{core.DocumentTitle}' source={core.Source}");
            done.TrySetResult(e.IsSuccess);
        };
        core.Navigate(url);

        var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        if (finished != done.Task) throw new TimeoutException("no NavigationCompleted within 20 s");
        if (!done.Task.Result) throw new InvalidOperationException("navigation failed");

        // The page runs inside the hosted visual: ask it for its own idea of the viewport.
        string size = await core.ExecuteScriptAsync("window.innerWidth + 'x' + window.innerHeight");
        SpikeLog.Write($"page viewport {size.Trim('"')}");
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
