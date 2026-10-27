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
        if (_mode is "s2" or "s3" or "s4" or "s5") await StartCompositionAsync();
        else await StartPlainAsync();
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
                "Stagecast", "Spike", "WebView2-composition");
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            _composition = new CompositionSpike();
            Status.Text = $"{_mode.ToUpperInvariant()}: page is loading inside an off-screen composition visual...";
            string url = _mode == "s5" && ArgValue("--url") is null
                ? new Uri(Path.Combine(AppContext.BaseDirectory, "assets", "animation.html")).AbsoluteUri
                : _url;
            await _composition.InitializeAsync(hwnd, dataDir, url, new Stagecast.Core.Rendering.Resolution(2520, 1080));
            if (_mode == "s2")
            {
                await _composition.LogViewportAsync();
                Status.Text = "S2 ok: page loaded in a composition visual (nothing is shown on screen by design)";
                SpikeLog.Write("S2 ok");
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
