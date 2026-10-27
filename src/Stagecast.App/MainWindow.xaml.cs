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
using Stagecast.Core.Output;
using Stagecast.Core.Presets;
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
        _presets = PresetCatalog.FromConfig(_config);
        Confirm = text => MessageBox.Show(this, text, "Stagecast", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
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
            ShowControls();
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
        RequestChange(new RenderChange(resolution, null));
    }

    private void OnFpsChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressFpsEvent || _engine is null) return;
        RequestChange(new RenderChange(null, ReferenceEquals(sender, Fps30) ? 30 : 60));
    }

    /// <summary>Used by the self test, which drives the same code paths as the controls.</summary>
    internal void ApplyFromCode(Resolution resolution, int fps)
    {
        WidthBox.Text = resolution.Width.ToString();
        HeightBox.Text = resolution.Height.ToString();
        RequestChange(new RenderChange(resolution, fps));
    }

    // ---- changing resolution and rate (A2c) ----

    private readonly RenderChangeGate _gate = new();

    /// <summary>Until the outputs exist (A4) a checkbox stands in for "an output is on".</summary>
    internal bool OutputsLive => LiveSimBox.IsChecked == true;

    internal Func<string, bool> Confirm { get; set; } = _ => false;

    internal bool BannerVisible => Banner.Visibility == Visibility.Visible;

    internal RenderChange? PendingChange => _gate.Pending;

    internal void SetSimulatedLive(bool live) => LiveSimBox.IsChecked = live;

    private void RequestChange(RenderChange change)
    {
        if (_engine is null) return;
        var decision = _gate.Request(change, _engine.Resolution, _engine.Fps, OutputsLive);
        if (decision == ChangeDecision.ApplyNow) Apply(change);
        ShowControls();
    }

    private void Apply(RenderChange change)
    {
        if (change.Resolution is { } resolution)
        {
            _engine!.SetResolution(resolution);
            PagePreviewView.SetResolution(resolution);
        }
        if (change.Fps is { } fps) _engine!.SetFps(fps);
        Persist();
    }

    internal void ConfirmPending()
    {
        if (_gate.Confirm() is { } change) Apply(change);
        ResetBoxes();
        ShowControls();
    }

    internal void CancelPending()
    {
        _gate.Cancel();
        ResetBoxes();
        ShowControls();
    }

    private void OnBannerApply(object sender, RoutedEventArgs e) => ConfirmPending();

    private void OnBannerCancel(object sender, RoutedEventArgs e) => CancelPending();

    private void ResetBoxes()
    {
        if (_engine is null) return;
        WidthBox.Text = _engine.Resolution.Width.ToString();
        HeightBox.Text = _engine.Resolution.Height.ToString();
        ResolutionError.Text = string.Empty;
    }

    /// <summary>Brings the banner, rate choice and preset button in line with what is applied and what waits.</summary>
    private void ShowControls()
    {
        if (_engine is null) return;
        var pending = _gate.Pending;
        var resolution = pending?.Resolution ?? _engine.Resolution;
        int fps = pending?.Fps ?? _engine.Fps;

        _suppressFpsEvent = true;
        (fps == 30 ? Fps30 : Fps60).IsChecked = true;
        _suppressFpsEvent = false;

        PresetButton.Content = "Preset: " + (_presets.Match(resolution)?.Name ?? "Kustom");

        Banner.Visibility = pending is null ? Visibility.Collapsed : Visibility.Visible;
        if (pending is null) return;
        BannerTitle.Text = (pending.Resolution, pending.Fps) switch
        {
            (not null, not null) => "Mengubah resolusi dan FPS saat tayang bisa memutus output beberapa detik",
            (not null, null) => "Mengubah resolusi saat tayang bisa memutus output beberapa detik",
            _ => "Mengubah FPS saat tayang bisa memutus output beberapa detik",
        };
        BannerDetail.Text = $"{resolution.Width} × {resolution.Height} @ {fps}    (sekarang {_engine.Resolution.Width} × {_engine.Resolution.Height} @ {_engine.Fps})";
    }

    // ---- presets (A2d) ----

    private PresetCatalog _presets;

    internal PresetCatalog Presets => _presets;

    private void OnPresetMenu(object sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        var menu = new ContextMenu { PlacementTarget = PresetButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MinWidth = PresetButton.ActualWidth };
        var current = _gate.Pending?.Resolution ?? _engine.Resolution;
        foreach (var preset in _presets.All)
        {
            var item = new MenuItem { Header = $"{preset.Name}    {preset.Resolution.Width} × {preset.Resolution.Height}", IsCheckable = true, IsChecked = preset.Resolution == current };
            item.Click += (_, _) => UsePreset(preset);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    internal void UsePreset(Preset preset)
    {
        WidthBox.Text = preset.Resolution.Width.ToString();
        HeightBox.Text = preset.Resolution.Height.ToString();
        ResolutionError.Text = string.Empty;
        RequestChange(new RenderChange(preset.Resolution, null));
    }

    private void OnSavePreset(object sender, RoutedEventArgs e) => ShowSavePanel();

    private void OnManagePresets(object sender, RoutedEventArgs e) => ShowManagePanel();

    private TextBlock PanelText(string text, bool muted = false, double size = 13) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Segoe UI"), FontSize = size,
        Foreground = (Brush)FindResource(muted ? "Muted" : "Text"),
    };

    private void ShowSavePanel()
    {
        if (_engine is null) return;
        var resolution = _engine.Resolution;
        var name = new TextBox { Style = (Style)FindResource("Field"), MaxLength = PresetRules.MaxNameLength + 8, FontFamily = new FontFamily("Segoe UI"), FontSize = 14, Margin = new Thickness(0, 6, 0, 6) };
        var error = PanelText("");
        error.Foreground = (Brush)FindResource("Warn");
        void Save()
        {
            var result = SavePreset(name.Text);
            if (result == PresetError.None) HidePresetPanel();
            else error.Text = PresetErrorText(result, resolution);
        }
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) Save(); else if (e.Key == Key.Escape) HidePresetPanel(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var save = new Button { Content = "Simpan", Style = (Style)FindResource("ActionButton") };
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "Batal", Style = (Style)FindResource("ToolButton"), Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => HidePresetPanel();
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);

        PresetPanelBody.Children.Clear();
        PresetPanelBody.Children.Add(PanelText($"Simpan {resolution.Width} × {resolution.Height} sebagai preset", size: 13));
        PresetPanelBody.Children.Add(name);
        PresetPanelBody.Children.Add(error);
        PresetPanelBody.Children.Add(buttons);
        PresetPanel.Visibility = Visibility.Visible;
        name.Focus();
    }

    private static string PresetErrorText(PresetError error, Resolution resolution) => error switch
    {
        PresetError.NameEmpty => "Nama tidak boleh kosong.",
        PresetError.NameTooLong => $"Nama paling panjang {PresetRules.MaxNameLength} karakter.",
        PresetError.NameInUse => "Nama itu sudah dipakai preset lain.",
        PresetError.ResolutionInUse => $"Resolusi {resolution.Width} × {resolution.Height} sudah ada di preset lain.",
        _ => "",
    };

    /// <summary>Saves the resolution in use as a preset. Returns what was wrong, or None.</summary>
    internal PresetError SavePreset(string? name)
    {
        if (_engine is null) return PresetError.NameEmpty;
        var error = _presets.TryAdd(name, _engine.Resolution, out var catalog);
        if (error != PresetError.None) return error;
        _presets = catalog;
        Persist();
        ShowControls();
        return PresetError.None;
    }

    private void ShowManagePanel()
    {
        PresetPanelBody.Children.Clear();
        PresetPanelBody.Children.Add(PanelText("Preset bawaan tidak bisa dihapus. Menghapus preset tidak mengubah resolusi yang sedang dipakai.", muted: true, size: 12));
        foreach (var preset in _presets.All)
        {
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
            if (PresetRules.CanDelete(preset))
            {
                var delete = new Button { Content = "Hapus", Style = (Style)FindResource("ToolButton"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3) };
                delete.Click += (_, _) =>
                {
                    if (DeletePreset(preset)) ShowManagePanel();
                };
                DockPanel.SetDock(delete, Dock.Right);
                row.Children.Add(delete);
            }
            else
            {
                var tag = PanelText("bawaan", muted: true, size: 12);
                tag.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(tag, Dock.Right);
                row.Children.Add(tag);
            }
            row.Children.Add(PanelText($"{preset.Name}   {preset.Resolution.Width} × {preset.Resolution.Height}"));
            PresetPanelBody.Children.Add(row);
        }
        var done = new Button { Content = "Selesai", Style = (Style)FindResource("ActionButton"), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        done.Click += (_, _) => HidePresetPanel();
        PresetPanelBody.Children.Add(done);
        PresetPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Asks first, then removes one of the operator's presets. Built-in presets are refused.</summary>
    internal bool DeletePreset(Preset preset)
    {
        if (!PresetRules.CanDelete(preset)) return false;
        if (!Confirm($"Hapus preset \u201C{preset.Name}\u201D? Ini tidak bisa dibatalkan.")) return false;
        if (!_presets.TryRemove(preset, out var catalog)) return false;
        _presets = catalog;
        Persist();
        ShowControls();
        return true;
    }

    private void HidePresetPanel()
    {
        PresetPanel.Visibility = Visibility.Collapsed;
        PresetPanelBody.Children.Clear();
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

    /// <summary>Brings the window to the front even when another program has the focus; real keystrokes only reach the active window.</summary>
    internal async Task<bool> BringToFrontAsync()
    {
        for (int attempt = 0; attempt < 5 && !IsActive; attempt++)
        {
            Topmost = true;
            Activate();
            Topmost = false;
            await Task.Delay(200);
        }
        return IsActive;
    }

    internal AppConfig ReadConfigFromDisk() => _context.ConfigStore.Load().Config;

    private void Persist()
    {
        var render = _engine is null ? _config.Render : new RenderConfig { Width = _engine.Resolution.Width, Height = _engine.Resolution.Height, Fps = _engine.Fps };
        _config = new AppConfig { Render = render, Presets = _presets.ToEntries() };
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
