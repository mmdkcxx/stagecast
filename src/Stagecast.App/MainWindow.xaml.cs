using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Configuration;
using Stagecast.Core.Rendering;
using Stagecast.Rendering;
using Stagecast.Rendering.Capture;

namespace Stagecast.App;

public partial class MainWindow : Window
{
    private readonly StartupContext _context;
    private readonly ILogger<MainWindow> _logger;
    private AppConfig _config;
    private RenderEngine? _engine;
    private bool _suppressFpsEvent;

    public MainWindow(StartupContext context)
    {
        InitializeComponent();
        _context = context;
        _config = context.Config.Config;
        _logger = context.LoggerFactory.CreateLogger<MainWindow>();
        VersionText.Text = $"v{context.Version}";
        WidthBox.Text = _config.Render.Width.ToString();
        HeightBox.Text = _config.Render.Height.ToString();
        _suppressFpsEvent = true;
        (_config.Render.Fps == 30 ? Fps30 : Fps60).IsChecked = true;
        _suppressFpsEvent = false;
        StatusText.Text = context.Config.Status switch
        {
            ConfigLoadStatus.Repaired => $"Sebagian pengaturan tidak valid dan diganti bawaan: {string.Join("; ", context.Config.Notes)}",
            ConfigLoadStatus.RecoveredFromCorrupt => $"Berkas pengaturan rusak, disimpan sebagai salinan: {string.Join("; ", context.Config.Notes)}",
            _ => context.Paths.ConfigFile,
        };
        Loaded += async (_, _) => await StartEngineAsync();
        Closed += (_, _) => _engine?.Dispose();
    }

    private async Task StartEngineAsync()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stagecast", "Profiles", "default");
            var render = _config.Render;
            _engine = await RenderEngine.CreateAsync(
                new RenderEngineOptions(hwnd, profile, new Resolution(render.Width, render.Height), render.Fps), _context.LoggerFactory);
            _engine.PreviewReady += ShowPreview;

            var statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            statsTimer.Tick += (_, _) => UpdateStats();
            statsTimer.Start();

            string url = _context.StartUrl ?? "about:blank";
            UrlBox.Text = url;
            await _engine.NavigateAsync(url, TimeSpan.FromSeconds(20));

            if (_context.SelfTest is not null) {
                await new SelfTest(_engine, _context.LoggerFactory.CreateLogger<SelfTest>()).RunAsync(_context.SelfTest, this);
                Close();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Starting the render engine failed");
            StatusText.Text = $"Gagal memulai mesin render: {ex.Message}";
        }
    }

    // Raised on a capture thread: build a frozen bitmap there and hand it to the UI thread.
    private void ShowPreview(PreviewImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        Dispatcher.BeginInvoke(() => Preview.Source = bitmap);
    }

    private void UpdateStats()
    {
        if (_engine is null) return;
        var s = _engine.Stats();
        StatsText.Text = string.Join('\n',
            $"Render   {_engine.Resolution.Width} × {_engine.Resolution.Height} @ {_engine.Fps}",
            $"Frame    {s.Capture.Width} × {s.Capture.Height}",
            $"Masuk    {s.Capture.ArrivedPerSecond,6:F1} /dtk (compositor)",
            $"Keluar   {s.Hub.DeliveredPerSecond,6:F1} /dtk",
            $"  baru   {s.Hub.Delivered,6}  ulang {s.Hub.Repeated}",
            $"Dibuang  {s.Hub.Dropped,6}",
            $"Umur     {s.Hub.AverageAgeMs,6:F1} ms (maks {s.Hub.MaxAgeMs:F0})",
            $"Detak    p99 {s.Hub.TickP99Ms:F1} ms (maks {s.Hub.TickMaxMs:F0})",
            $"Salin    {s.Capture.ReadbackAverageMs,6:F1} ms (maks {s.Capture.ReadbackMaxMs:F0})");
    }

    private void OnUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _engine is null) return;
        string text = UrlBox.Text.Trim();
        if (text.Length == 0) return;
        string url = text.Contains("://") || text.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ? text : "https://" + text;
        UrlBox.Text = url;
        _ = _engine.NavigateAsync(url, TimeSpan.FromSeconds(20));
    }

    private void OnResolutionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyResolution();
    }

    private void OnApplyResolution(object sender, RoutedEventArgs e) => ApplyResolution();

    private void ApplyResolution()
    {
        if (_engine is null) return;
        if (!int.TryParse(WidthBox.Text.Trim(), out int width) || !int.TryParse(HeightBox.Text.Trim(), out int height) || !Resolution.TryCreate(width, height, out var resolution))
        {
            ResolutionError.Text = $"Lebar dan tinggi harus angka {Resolution.MinSide}-{Resolution.MaxSide}.";
            return;
        }
        ResolutionError.Text = string.Empty;
        _engine.SetResolution(resolution);
        SaveConfig(c => new AppConfig { Render = new RenderConfig { Width = resolution.Width, Height = resolution.Height, Fps = c.Render.Fps }, Presets = c.Presets });
    }

    private void OnFpsChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressFpsEvent || _engine is null) return;
        int fps = ReferenceEquals(sender, Fps30) ? 30 : 60;
        _engine.SetFps(fps);
        SaveConfig(c => new AppConfig { Render = new RenderConfig { Width = c.Render.Width, Height = c.Render.Height, Fps = fps }, Presets = c.Presets });
    }

    /// <summary>Used by the self test, which drives the same code paths as the controls.</summary>
    internal void ApplyFromCode(Resolution resolution, int fps)
    {
        WidthBox.Text = resolution.Width.ToString();
        HeightBox.Text = resolution.Height.ToString();
        ApplyResolution();
        _suppressFpsEvent = true;
        (fps == 30 ? Fps30 : Fps60).IsChecked = true;
        _suppressFpsEvent = false;
        _engine!.SetFps(fps);
        SaveConfig(c => new AppConfig { Render = new RenderConfig { Width = c.Render.Width, Height = c.Render.Height, Fps = fps }, Presets = c.Presets });
    }

    private void SaveConfig(Func<AppConfig, AppConfig> change)
    {
        _config = change(_config);
        try
        {
            _context.ConfigStore.Save(_config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Saving the config failed");
            StatusText.Text = $"Pengaturan tidak bisa disimpan: {ex.Message}";
        }
    }
}
