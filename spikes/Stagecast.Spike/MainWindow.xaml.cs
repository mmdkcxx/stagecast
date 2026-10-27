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
        if (_mode is "s2" or "s3") await StartCompositionAsync();
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
            await _composition.InitializeAsync(hwnd, dataDir, _url, new Stagecast.Core.Rendering.Resolution(2520, 1080));
            if (_mode == "s2")
            {
                await _composition.LogViewportAsync();
                Status.Text = "S2 ok: page loaded in a composition visual (nothing is shown on screen by design)";
                SpikeLog.Write("S2 ok");
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
