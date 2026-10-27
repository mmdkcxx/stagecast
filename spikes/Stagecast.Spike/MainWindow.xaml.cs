using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Stagecast.Spike;

/// <summary>S1: one ordinary WebView2 loading a URL. Proves runtime, network and the Windows copy/build loop.</summary>
public partial class MainWindow : Window
{
    private const string DefaultUrl = "https://example.com/";

    private readonly string _url = ArgValue("--url") ?? DefaultUrl;
    private readonly string _mode = ArgValue("--mode") ?? "s3";
    private CompositionSpike? _composition;
    private readonly bool _autoClose = Environment.GetCommandLineArgs().Contains("--autoclose");

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await StartAsync();
    }

    private async Task StartAsync()
    {
        if (_mode == "s9") await StartTabsAsync();
        else if (_mode is "s2" or "s3" or "s4" or "s5" or "s6" or "s6i" or "s8" or "s10") await StartCompositionAsync();
        else await StartPlainAsync();
    }

    private TabsSpike? _tabs;

    private async Task StartTabsAsync()
    {
        try
        {
            SpikeLog.Write("S9 start");
            Web.Visibility = Visibility.Collapsed;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stagecast", "Spike");
            bool hide = Environment.GetCommandLineArgs().Contains("--hide");
            _tabs = new TabsSpike();
            Status.Text = $"S9: two tabs, switching (inactive tab hidden: {hide})";
            bool ok = await _tabs.RunAsync(hwnd, dataRoot, hide, Path.Combine(AppContext.BaseDirectory, "assets", "tab.html"));
            SpikeLog.Write(ok ? $"S9 ok hide={hide}" : $"S9 FAILED hide={hide}");
        }
        catch (Exception ex)
        {
            SpikeLog.Write($"S9 FAILED {ex.GetType().Name}: {ex.Message}");
        }
        if (_autoClose)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            Close();
        }
    }

    private async Task StartCompositionAsync()
    {
        try
        {
            SpikeLog.Write($"{_mode.ToUpperInvariant()} start");
            Title = $"Stagecast spike {_mode.ToUpperInvariant()}";
            Web.Visibility = Visibility.Collapsed; // no WebView2 control: the page lives only in a composition visual
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Stagecast", "Spike", _mode == "s6i" ? $"WebView2-s6-{ArgValue("--profile") ?? "main"}" : "WebView2-composition");
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            _composition = new CompositionSpike();
            Status.Text = $"{_mode.ToUpperInvariant()}: page is loading inside an off-screen composition visual...";
            string url = _url;
            if (_mode == "s5" && ArgValue("--url") is null)
                url = new Uri(Path.Combine(AppContext.BaseDirectory, "assets", "animation.html")).AbsoluteUri;
            if (_mode == "s10" && ArgValue("--url") is null)
                url = new Uri(Path.Combine(AppContext.BaseDirectory, "assets", "control.html")).AbsoluteUri;
            if (_mode == "s8" && ArgValue("--url") is null)
                url = new Uri(Path.Combine(AppContext.BaseDirectory, "assets", "tone.html")).AbsoluteUri;
            if (_mode == "s6" && ArgValue("--url") is null)
            {
                string media = new Uri(ArgValue("--media") ?? @"C:\dev\media\bbb.mp4").AbsoluteUri;
                url = new Uri(Path.Combine(AppContext.BaseDirectory, "assets", "video.html")).AbsoluteUri + "?src=" + Uri.EscapeDataString(media);
            }
            string? browserArgs = ArgValue("--autoplay") == "open" || _mode == "s8" ? "--autoplay-policy=no-user-gesture-required" : null;
            await _composition.InitializeAsync(hwnd, dataDir, url, new Stagecast.Core.Rendering.Resolution(2520, 1080), browserArgs);
            if (_mode == "s2")
            {
                await _composition.LogViewportAsync();
                Status.Text = "S2 ok: page loaded in a composition visual (nothing is shown on screen by design)";
                SpikeLog.Write("S2 ok");
            }
            else if (_mode == "s10")
            {
                await Task.Delay(1000);
                bool ok = await new ControlSpike(_composition).RunAsync(this);
                Status.Text = ok ? "S10 ok" : "S10 FAILED (see log)";
                SpikeLog.Write(ok ? "S10 ok" : "S10 FAILED");
            }
            else if (_mode == "s8")
            {
                await Task.Delay(2000); // let the page start
                SpikeLog.Write($"tone page says {await _composition.EvalAsync("document.getElementById('s').textContent")}");
                string wav = Path.Combine(AppContext.BaseDirectory, "logs", "s8-loopback.wav");
                // Target the WebView2 browser process: capturing from our own (host) process tree stays silent, see PLAN_PHASES S8.
                int target = ArgValue("--target") == "root" ? Environment.ProcessId : _composition.BrowserProcessId;
                SpikeLog.Write($"loopback target pid {target} ({(ArgValue("--target") == "root" ? "host process" : "WebView2 browser process")})");
                var phases = await AudioSpike.RunAsync(target, m => { _composition.SetMuted(m); return Task.CompletedTask; }, wav);
                foreach (var p in phases) SpikeLog.Write($"S8 phase {p.Name}: {p.Seconds:F1}s peak={p.PeakDb:F1} dBFS rms={p.RmsDb:F1} dBFS");
                bool audible = phases.Where(p => p.Name != "muted").All(p => p.PeakDb > -60);
                bool muted = phases.First(p => p.Name == "muted").PeakDb < -80;
                Status.Text = $"S8: audible={audible} mute-silent={muted}";
                SpikeLog.Write(audible && muted ? "S8 ok" : $"S8 FAILED audible={audible} muteSilent={muted}");
            }
            else if (_mode == "s6i")
            {
                await StartInteractiveAsync();
                return; // stays open until the operator closes the window
            }
            else if (_mode == "s6")
            {
                string png = Path.Combine(AppContext.BaseDirectory, "logs", "s6-video-frame.png");
                string report = await _composition.RunVideoCheckAsync(10, png);
                bool ok = report.Contains("\"readyState\":4") && report.Contains("\"videoWidth\":1280") && !report.Contains("\"paused\":true");
                Status.Text = $"S6: {report}";
                SpikeLog.Write(ok ? "S6 ok video plays" : "S6 FAILED video did not play");
            }
            else if (_mode == "s5")
            {
                int seconds = int.TryParse(ArgValue("--seconds"), out var sec) ? sec : 20;
                var (first, second) = await _composition.RunContinuousAsync(seconds, ArgValue("--switch") != "no", Environment.GetCommandLineArgs().Contains("--nopreview") ? null : ShowPreview);
                LogSummary("60 fps", first);
                if (second is not null) LogSummary("30 fps", second);
                // Pass on the average rate. Stutters (gaps over 1.5 intervals) are logged for judgement, not a gate.
                bool ok60 = first.DeliveredFps >= 55;
                bool ok30 = second is null || (second.DeliveredFps is >= 28 and <= 32);
                Status.Text = $"S5: 60 fps run {first.DeliveredFps:F1} fps" + (second is null ? "" : $", 30 fps run {second.DeliveredFps:F1} fps");
                SpikeLog.Write(ok60 && ok30 ? "S5 ok" : "S5 FAILED fps below target");
            }
            else if (_mode == "s4")
            {
                string png = Path.Combine(AppContext.BaseDirectory, "logs", "s4-frame.png");
                var (w, h) = await _composition.CaptureFrameAsync(png);
                bool ok = w == 2520 && h == 1080;
                Status.Text = $"S4: captured {w}x{h}";
                SpikeLog.Write(ok ? $"S4 ok {w}x{h}" : $"S4 FAILED size {w}x{h}, expected 2520x1080");
            }
            else
            {
                var (pass, fail) = await _composition.RunMatrixAsync();
                Status.Text = $"S3: {pass} passed, {fail} failed (see logs/spike.log)";
                SpikeLog.Write(fail == 0 ? "S3 ok" : "S3 FAILED some cases did not match");
            }
        }
        catch (Exception ex)
        {
            SpikeLog.Write($"{_mode.ToUpperInvariant()} FAILED {ex.GetType().Name}: {ex.Message}");
            Status.Text = $"{_mode.ToUpperInvariant()} failed: {ex.Message}";
        }
        if (_autoClose)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            SpikeLog.Write($"{_mode.ToUpperInvariant()} autoclose");
            Close();
        }
    }

    // S6 interactive: the page is only in the composition visual, so the preview image is the operator's view and
    // mouse/keyboard are forwarded into the browser by hand. This is exactly what the real app's preview tab will need.
    private ContinuousCapture? _preview;

    private async Task StartInteractiveAsync()
    {
        Bar.Visibility = Visibility.Visible;
        UrlBox.Text = _composition!.CurrentUrl;
        GoButton.Click += (_, _) => _composition.Navigate(NormalizeUrl(UrlBox.Text));
        UrlBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) _composition.Navigate(NormalizeUrl(UrlBox.Text)); };
        Preview.MouseMove += (_, e) => Forward(Microsoft.Web.WebView2.Core.CoreWebView2MouseEventKind.Move, e, 0);
        Preview.MouseLeftButtonDown += (_, e) => { Preview.Focus(); _composition.FocusBrowser(); Forward(Microsoft.Web.WebView2.Core.CoreWebView2MouseEventKind.LeftButtonDown, e, 0); };
        Preview.MouseLeftButtonUp += (_, e) => Forward(Microsoft.Web.WebView2.Core.CoreWebView2MouseEventKind.LeftButtonUp, e, 0);
        Preview.MouseRightButtonDown += (_, e) => Forward(Microsoft.Web.WebView2.Core.CoreWebView2MouseEventKind.RightButtonDown, e, 0);
        Preview.MouseRightButtonUp += (_, e) => Forward(Microsoft.Web.WebView2.Core.CoreWebView2MouseEventKind.RightButtonUp, e, 0);
        Preview.MouseWheel += (_, e) => Forward(Microsoft.Web.WebView2.Core.CoreWebView2MouseEventKind.Wheel, e, (uint)e.Delta);
        Preview.Focusable = true;
        _preview = _composition.StartPreview(ShowPreview, 50);
        foreach (var site in new[] { "https://www.canva.com", "https://kahoot.it", "https://slido.com" })
            await _composition.LogCookiesAsync(site);
        Status.Text = "S6 interactive: click the preview to use the page. Close the window when done (log: logs/spike.log)";
        SpikeLog.Write("S6 interactive ready");
    }

    private static string NormalizeUrl(string text) => text.Contains("://") ? text : "https://" + text;

    private void Forward(Microsoft.Web.WebView2.Core.CoreWebView2MouseEventKind kind, System.Windows.Input.MouseEventArgs e, uint data)
    {
        // Preview is stretched Uniform: map the pointer from the displayed image back to page pixels.
        var p = e.GetPosition(Preview);
        double scale = Math.Min(Preview.ActualWidth / 2520.0, Preview.ActualHeight / 1080.0);
        double offX = (Preview.ActualWidth - 2520 * scale) / 2, offY = (Preview.ActualHeight - 1080 * scale) / 2;
        var point = new System.Drawing.Point((int)((p.X - offX) / scale), (int)((p.Y - offY) / scale));
        _composition!.SendMouse(kind, Microsoft.Web.WebView2.Core.CoreWebView2MouseEventVirtualKeys.None, data, point);
    }

    private static void LogSummary(string label, CaptureSummary s) =>
        SpikeLog.Write($"S5 summary {label}: {s.Seconds:F1}s delivered={s.Delivered} ({s.DeliveredFps:F1}/s) arrived={s.Arrived} ({s.ArrivedFps:F1}/s) "
            + $"p99={s.P99IntervalMs:F1}ms max={s.MaxIntervalMs:F1}ms stutters={s.Stutters} dropped={s.Dropped} cpu={s.CpuPercent:F1}%");

    // Called from the capture thread a few times per second; the pixels are copied into a frozen bitmap for the preview image.
    private void ShowPreview(byte[] pixels, int width, int height, int stride)
    {
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        Dispatcher.BeginInvoke(() => Preview.Source = bitmap);
    }

    protected override void OnClosed(EventArgs e)
    {
        _preview?.Dispose();
        _tabs?.Dispose();
        _composition?.Dispose();
        base.OnClosed(e);
    }

    private async Task StartPlainAsync()
    {
        try
        {
            SpikeLog.Write("S1 start");
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Stagecast", "Spike", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
            await Web.EnsureCoreWebView2Async(env);
            SpikeLog.Write($"webview2 runtime {env.BrowserVersionString}");
            SpikeLog.Write($"user data {dataDir}");

            var core = Web.CoreWebView2;
            core.NavigationStarting += (_, e) => SpikeLog.Write($"navigating {e.Uri}");
            core.DocumentTitleChanged += (_, _) => Title = $"Stagecast spike S1 - {core.DocumentTitle}";
            core.NavigationCompleted += OnNavigationCompleted;

            Status.Text = $"loading {_url}";
            core.Navigate(_url);
        }
        catch (Exception ex)
        {
            SpikeLog.Write($"S1 FAILED {ex.GetType().Name}: {ex.Message}");
            Status.Text = $"failed: {ex.Message}";
            if (_autoClose) Close();
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var core = Web.CoreWebView2;
        SpikeLog.Write($"navigation completed success={e.IsSuccess} status={e.WebErrorStatus} title='{core.DocumentTitle}' source={core.Source}");
        Status.Text = e.IsSuccess
            ? $"ok  {core.Source}  '{core.DocumentTitle}'"
            : $"navigation failed: {e.WebErrorStatus}";

        if (!_autoClose) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        timer.Tick += (_, _) => { timer.Stop(); SpikeLog.Write("S1 autoclose"); Close(); };
        timer.Start();
    }

    private static string? ArgValue(string name)
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
