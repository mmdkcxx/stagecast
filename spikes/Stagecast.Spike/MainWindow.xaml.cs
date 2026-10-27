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
    private readonly bool _autoClose = Environment.GetCommandLineArgs().Contains("--autoclose");

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await StartAsync();
    }

    private async Task StartAsync()
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
