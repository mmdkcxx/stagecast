using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Configuration;
using Stagecast.Core.Display;
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
        PagePreviewView.ViewChanged += UpdateViewButtons;
        Closed += (_, _) =>
        {
            _detached?.Close();
            _engine?.Dispose();
        };
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
            PagePreviewView.Attach(_engine);
            _engine.NavigationStateChanged += UpdateNavigationState;
            UpdateNavigationState();
            UpdateViewButtons();

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
        PagePreviewView.SetResolution(resolution);
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

    // ---- page controls ----

    internal PagePreview Preview => PagePreviewView;

    internal RenderEngine? Engine => _engine;

    private void UpdateNavigationState()
    {
        if (_engine is null) return;
        BackButton.IsEnabled = _engine.CanGoBack;
        ForwardButton.IsEnabled = _engine.CanGoForward;
        if (!UrlBox.IsKeyboardFocusWithin && _engine.CurrentUrl is { Length: > 0 } url) UrlBox.Text = url;
    }

    private void UpdateViewButtons()
    {
        FitButton.IsChecked = PagePreviewView.View.Mode == ViewMode.Fit;
        ActualButton.IsChecked = PagePreviewView.View.Mode == ViewMode.Actual;
    }

    private void OnBack(object sender, RoutedEventArgs e) => _engine?.GoBack();

    private void OnForward(object sender, RoutedEventArgs e) => _engine?.GoForward();

    private void OnReload(object sender, RoutedEventArgs e) => _engine?.Reload();

    private void OnFit(object sender, RoutedEventArgs e)
    {
        PagePreviewView.ShowFit();
        UpdateViewButtons();
    }

    private void OnActual(object sender, RoutedEventArgs e)
    {
        PagePreviewView.ShowActual();
        UpdateViewButtons();
    }

    private void OnInteract(object sender, RoutedEventArgs e)
    {
        PagePreviewView.Interactive = InteractButton.IsChecked == true;
        if (!PagePreviewView.Interactive) PagePreviewView.ReleaseKeyboard();
    }

    private void OnEnlarge(object sender, RoutedEventArgs e)
    {
        bool enlarged = EnlargeButton.IsChecked == true;
        SidePanel.Visibility = enlarged ? Visibility.Collapsed : Visibility.Visible;
        SideColumn.Width = enlarged ? new GridLength(0) : new GridLength(300);
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "Zoom halaman (ikut mengubah keluaran)", IsEnabled = false });
        foreach (double zoom in PageZoomLevels)
        {
            var item = new MenuItem { Header = $"{zoom * 100:0}%", IsCheckable = true, IsChecked = Math.Abs(_engine.PageZoom - zoom) < 0.001 };
            item.Click += (_, _) => SetPageZoom(zoom);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private static readonly double[] PageZoomLevels = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0];

    internal void SetPageZoom(double zoom)
    {
        if (_engine is null) return;
        _engine.PageZoom = zoom;
        ZoomLabel.Text = Math.Abs(zoom - 1.0) < 0.001 ? "" : $"zoom halaman {zoom * 100:0}%";
    }

    private PreviewWindow? _detached;

    private void OnDetach(object sender, RoutedEventArgs e)
    {
        if (_detached is not null) { _detached.Activate(); return; }
        PreviewHost.Child = null;
        DetachedNote.Visibility = Visibility.Visible;
        _detached = new PreviewWindow(PagePreviewView);
        _detached.Closed += (_, _) =>
        {
            _detached.Content = null;
            PreviewHost.Child = PagePreviewView;
            DetachedNote.Visibility = Visibility.Collapsed;
            _detached = null;
        };
        _detached.Show();
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
