using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Stagecast.Core.Configuration;
using Stagecast.Core.Sessions;
using Stagecast.Core.Tabs;
using Stagecast.Rendering;

namespace Stagecast.App;

// Tabs: the operator's list (names, order, which is active) and the matching pages in the render engine.
public partial class MainWindow
{
    private TabList _tabs;
    private Guid? _renamingId;
    private DispatcherTimer? _persistTimer;

    internal TabList Tabs => _tabs;

    private TabList BuildTabList()
    {
        var items = _config.Tabs.Select(t =>
        {
            TabKinds.TryParse(t.Kind, out var kind); // the settings loader already replaced unknown names with web
            return new TabInfo(Guid.NewGuid(), t.Name, t.Url, t.ProfileId, kind, t.Source);
        }).ToList();
        var list = new TabList(items, _config.ActiveTab);
        // --url opens that address in the tab that was active.
        if (_context.StartUrl is { } url) list.SetUrl(list.Active.Id, url);
        return list;
    }

    private async Task StartTabsAsync()
    {
        var engine = _engine!;
        engine.TabNavigated += OnTabNavigated;
        engine.ActiveTabChanged += () => ShowZoomLabel();

        // The tab that was active goes first, so it is on the stage from the start; the others load behind it.
        RenderEngine.RemoveUnusedProfiles(_context.Paths.ProfilesDirectory, _tabs.Items.Select(t => t.ProfileId));
        await AddEngineTabAsync(engine, _tabs.Active);
        // A snapshot: pages start loading while the next tabs are created, and their address changes update the list.
        foreach (var info in _tabs.Items.Where(t => t.Id != _tabs.Active.Id).ToList()) await AddEngineTabAsync(engine, info);
        engine.SortTabs(_tabs.Items.Select(t => t.Id).ToList());
        RebuildTabList();
        await OpenStoredPdfsAsync();
    }

    // A file tab has no page to load; until the sources that play files exist it only holds its place on the stage.
    private static async Task AddEngineTabAsync(RenderEngine engine, TabInfo info)
    {
        if (TabKinds.IsDocument(info.Kind)) engine.AddDocumentTab(info.Id, info.Kind, info.Source, info.Name);
        else await engine.AddTabAsync(info.Id, info.Url, info.ProfileId);
    }

    private void OnTabNavigated(Rendering.PageTab tab)
    {
        if (_tabs.IndexOf(tab.Id) < 0) return;
        _tabs.SetUrl(tab.Id, tab.Url);
        if (tab.Url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) && tab.Id != _tabs.Active.Id) return;
        SchedulePersist();
    }

    // Addresses change often while pages load; write the file once things settle.
    private void SchedulePersist()
    {
        _persistTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _persistTimer.Tick -= OnPersistTimer;
        _persistTimer.Tick += OnPersistTimer;
        _persistTimer.Stop();
        _persistTimer.Start();
    }

    private void OnPersistTimer(object? sender, EventArgs e)
    {
        _persistTimer!.Stop();
        Persist();
    }

    // ---- logins ----

    private readonly Dictionary<Guid, SessionState> _sessions = [];

    internal IReadOnlyDictionary<Guid, SessionState> Sessions => _sessions;

    /// <summary>Marks every tab with a guess: session active, needs login, or nothing to log in to. Does not change what is on the stage.</summary>
    internal async Task CheckSessionsAsync()
    {
        if (_engine is null) return;
        _sessions.Clear();
        foreach (var info in _tabs.Items)
        {
            if (_engine.PageTabById(info.Id) is not { } tab) continue; // file tabs have no login
            _sessions[info.Id] = SessionHeuristics.Classify(tab.Url, await _engine.SessionCookieCountAsync(tab));
        }
        int needLogin = _sessions.Values.Count(s => s == SessionState.NeedsLogin);
        SessionNote.Text = needLogin == 0
            ? "Tidak ada tab yang tampak perlu login. Ini perkiraan; tetap lihat tiap tab di preview."
            : $"{needLogin} tab tampak perlu login. Ini perkiraan; periksa di preview.";
        RebuildTabList();
    }

    /// <summary>Signs out of every site in every profile, after asking. Returns false when the operator declined.</summary>
    internal async Task<bool> ClearLoginDataAsync()
    {
        if (_engine is null) return false;
        if (!Confirm("Hapus semua data login? Semua tab akan keluar dari semua situs, termasuk tab dengan profil sendiri. Ini tidak bisa dibatalkan.")) return false;
        await _engine.ClearLoginDataAsync();
        _sessions.Clear();
        SessionNote.Text = "Data login dihapus. Login lagi di tiap tab yang membutuhkannya.";
        RebuildTabList();
        return true;
    }

    /// <summary>Gives a tab its own login (a separate browser profile) or puts it back on the shared one. The page is loaded again.</summary>
    internal async Task<bool> SetSeparateProfileAsync(Guid id, bool separate)
    {
        if (_engine is null || _engine.PageTabById(id) is not { } tab) return false;
        if ((tab.ProfileId is not null) == separate) return true;
        string what = separate
            ? "Tab ini akan memakai login sendiri. Halamannya dimuat ulang dan belum login di profil baru."
            : "Tab ini kembali memakai login bersama. Halamannya dimuat ulang dan login sendirinya dihapus.";
        if (!Confirm(what + " Lanjutkan?")) return false;
        _tabs.SetSeparateProfile(id, separate);
        await _engine.ReplaceProfileAsync(tab, _tabs.Items[_tabs.IndexOf(id)].ProfileId);
        _sessions.Remove(id);
        RebuildTabList();
        UpdateNavigationState();
        ShowZoomLabel();
        SchedulePersist();
        return true;
    }

    private void OnCheckSessions(object sender, RoutedEventArgs e) => _ = CheckSessionsAsync();

    private void OnClearLogins(object sender, RoutedEventArgs e) => _ = ClearLoginDataAsync();

    // ---- problems and recovery (A8) ----

    private string NameOf(Stagecast.Rendering.PageTab tab)
        => _tabs.Items.FirstOrDefault(t => t.Id == tab.Id)?.Name ?? "Tab";

    private void ShowTabProblem(Stagecast.Rendering.PageTab tab, string message)
    {
        StatusText.Foreground = (Brush)FindResource("Warn");
        StatusText.Text = $"{NameOf(tab)}: {message}";
        RebuildTabList();
    }

    private void ShowTabRecovered(Stagecast.Rendering.PageTab tab)
    {
        if (_engine!.Tabs.All(t => t.Problem is null))
        {
            StatusText.Foreground = (Brush)FindResource("Muted");
            StatusText.Text = $"{NameOf(tab)}: pulih.";
        }
        RebuildTabList();
    }

    // ---- Langsung / Preview + Take ----

    private void UpdateModeButtons()
    {
        ModeDirectButton.IsChecked = !_tabs.PreviewMode;
        ModePreviewButton.IsChecked = _tabs.PreviewMode;
        ModePreviewButton.IsEnabled = _tabs.PreviewMode || _tabs.CanEnterPreviewMode;
        TakeButton.Visibility = _tabs.PreviewMode ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Switches between Langsung and Preview + Take. The tab on air keeps playing; only the operator's view changes.</summary>
    internal async Task<bool> SetPreviewModeAsync(bool on)
    {
        if (_engine is null || _tabs.PreviewMode == on) { UpdateModeButtons(); return _engine is not null; }
        if (on)
        {
            if (!_tabs.EnterPreviewMode())
            {
                TabError.Text = "Preview + Take butuh minimal dua tab.";
                UpdateModeButtons();
                return false;
            }
            await _engine.EnterPreviewModeAsync(_engine.TabById(_tabs.Active.Id)!);
        }
        else
        {
            _tabs.ExitPreviewMode();
            await _engine.ExitPreviewModeAsync();
        }
        TabError.Text = string.Empty;
        PagePreviewView.PreviewMode = on;
        UpdateModeButtons();
        RebuildTabList();
        UpdateNavigationState();
        ShowZoomLabel();
        SchedulePersist();
        return true;
    }

    /// <summary>TAKE: the preview goes on air and the tab that was on air becomes the preview.</summary>
    internal async Task<bool> TakeAsync()
    {
        if (_engine is null || !_tabs.Take()) return false;
        await _engine.TakeAsync();
        RebuildTabList();
        UpdateNavigationState();
        ShowZoomLabel();
        SchedulePersist();
        return true;
    }

    private void OnModeDirect(object sender, RoutedEventArgs e) => _ = SetPreviewModeAsync(false);

    private void OnModePreview(object sender, RoutedEventArgs e) => _ = SetPreviewModeAsync(true);

    private void OnTake(object sender, RoutedEventArgs e) => _ = TakeAsync();

    // ---- operations (used by the buttons, the keys and the self test) ----

    internal async Task<bool> AddTabAsync(string url = TabRules.BlankUrl)
    {
        if (_engine is null) return false;
        var info = _tabs.Add(url);
        if (info is null)
        {
            TabError.Text = $"Paling banyak {TabRules.MaxTabs} tab.";
            return false;
        }
        TabError.Text = string.Empty;
        await _engine.AddTabAsync(info.Id, url, info.ProfileId); // the list keeps only an address worth restoring; the page gets the real one
        await SelectTabAsync(info.Id);
        return true;
    }

    /// <summary>Adds a tab for a file and selects it. <paramref name="placeholder"/> is the colour its visual shows until the source that plays the file exists (used by tests).</summary>
    internal async Task<bool> AddDocumentTabAsync(TabKind kind, string? source, Windows.UI.Color? placeholder = null)
    {
        if (_engine is null) return false;
        var info = _tabs.AddDocument(kind, source);
        if (info is null)
        {
            TabError.Text = $"Paling banyak {TabRules.MaxTabs} tab.";
            return false;
        }
        TabError.Text = string.Empty;
        _engine.AddDocumentTab(info.Id, kind, info.Source, info.Name, placeholder);
        await SelectTabAsync(info.Id);
        return true;
    }

    internal async Task SelectTabAsync(Guid id)
    {
        if (_engine is null || _engine.TabById(id) is not { } tab) return;
        if (!_tabs.Select(id))
        {
            if (_tabs.PreviewMode && id == _tabs.ProgramId) TabError.Text = "Tab ini sedang tayang. Pilih tab lain untuk preview, lalu TAKE.";
            return;
        }
        TabError.Text = string.Empty;
        RebuildTabList();
        await _engine.SelectAsync(tab);
        // The operator may have clicked another tab meanwhile; the list already shows the newest choice.
        UpdateNavigationState();
        ShowZoomLabel();
        SchedulePersist();
    }

    internal async Task<bool> CloseTabAsync(Guid id)
    {
        if (_engine is null || _engine.TabById(id) is not { } tab) return false;
        if (_tabs.PreviewMode && id == _tabs.ProgramId)
        {
            TabError.Text = "Tab yang sedang tayang tidak bisa ditutup. Tekan TAKE dulu untuk menukarnya.";
            return false;
        }
        bool wasActive = _tabs.Active.Id == id;
        if (_tabs.Close(id) is not { } newActive) return false;
        bool leftPreviewMode = _engine.PreviewMode && !_tabs.PreviewMode; // only the tab on air is left
        if (leftPreviewMode) await _engine.ExitPreviewModeAsync();
        else if (wasActive && _engine.TabById(newActive.Id) is { } next) await _engine.SelectAsync(next);
        _engine.CloseTab(tab);
        ForgetDocument(id);
        if (leftPreviewMode) PagePreviewView.PreviewMode = false;
        UpdateModeButtons();
        TabError.Text = string.Empty;
        RebuildTabList();
        UpdateNavigationState();
        ShowZoomLabel();
        SchedulePersist();
        return true;
    }

    internal TabNameError RenameTab(Guid id, string? name)
    {
        var error = _tabs.Rename(id, name);
        if (error == TabNameError.None) SchedulePersist();
        return error;
    }

    internal bool MoveTab(Guid id, int delta)
    {
        if (!_tabs.Move(id, delta)) return false;
        _engine?.SortTabs(_tabs.Items.Select(t => t.Id).ToList());
        RebuildTabList();
        SchedulePersist();
        return true;
    }

    private static string RenameErrorText(TabNameError error) => error switch
    {
        TabNameError.Empty => "Nama tab tidak boleh kosong.",
        TabNameError.TooLong => $"Nama tab paling panjang {TabRules.MaxNameLength} karakter.",
        _ => "",
    };

    // ---- the list ----

    private void OnAddTab(object sender, RoutedEventArgs e) => _ = AddTabAsync();

    private void StartRename(Guid id)
    {
        _renamingId = id;
        TabError.Text = string.Empty;
        RebuildTabList();
    }

    private readonly TabThumbnails _thumbs = new();
    private readonly Dictionary<Guid, (Image Picture, UIElement Placeholder)> _thumbViews = [];

    /// <summary>Capture thread: offers the picture of a tab; it is shrunk here and handed to the UI thread when one is due.</summary>
    private void OfferThumbnail(Guid id, Stagecast.Rendering.Capture.PreviewImage image)
    {
        if (!_thumbs.Due(id, Environment.TickCount64)) return;
        if (TabThumbnails.Downscale(image) is not { } bitmap) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            _thumbs.Set(id, bitmap);
            if (_thumbViews.TryGetValue(id, out var view))
            {
                view.Picture.Source = bitmap;
                view.Picture.Visibility = Visibility.Visible;
                view.Placeholder.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void OnThumbSourcePreview(Stagecast.Rendering.Capture.PreviewImage image)
    {
        if (_engine is not { } engine) return;
        // A zoomed (100%) view shows only part of the page; only a picture of the whole page makes a thumbnail.
        int mip = 1 << image.Mip;
        bool wholePage = image.SourceX == 0 && image.SourceY == 0
            && image.Width * mip >= engine.Resolution.Width - mip && image.Height * mip >= engine.Resolution.Height - mip;
        if (wholePage) OfferThumbnail(engine.Active.Id, image);
    }

    private void OnThumbSourceProgram(Stagecast.Rendering.Capture.PreviewImage image)
    {
        if (_engine is { } engine) OfferThumbnail(engine.Program.Id, image);
    }

    private static string ThumbnailLabel(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile) return "berkas lokal";
            if (!string.IsNullOrEmpty(uri.Host)) return uri.Host.StartsWith("www.") ? uri.Host[4..] : uri.Host;
        }
        return "kosong";
    }

    private FrameworkElement ThumbnailFor(Guid id, string label)
    {
        var picture = new Image { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        var placeholder = new TextBlock
        {
            Text = label, Foreground = (Brush)FindResource("Muted"), FontFamily = (FontFamily)FindResource("FontMono"), FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0),
        };
        if (_thumbs.Get(id) is { } known)
        {
            picture.Source = known;
            picture.Visibility = Visibility.Visible;
            placeholder.Visibility = Visibility.Collapsed;
        }
        _thumbViews[id] = (picture, placeholder);
        var grid = new Grid();
        grid.Children.Add(placeholder);
        grid.Children.Add(picture);
        return new Border
        {
            Width = 96, Height = 54, Background = (Brush)FindResource("Bg"), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Top, Child = grid,
        };
    }

    private Button SmallButton(string glyph, string tip, Action click, bool enabled = true)
    {
        var button = new Button
        {
            Content = glyph, ToolTip = tip, Style = (Style)FindResource("IconButton"), FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 14,
            IsEnabled = enabled, Focusable = false,
        };
        button.Click += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return button;
    }

    /// <summary>Small status chip on a tab row. <paramref name="fill"/> null = outline only.</summary>
    private Border Chip(string text, string tip, string foreground, string? fill = null) => new()
    {
        ToolTip = tip, CornerRadius = new CornerRadius(2), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 6, 0),
        Background = fill is null ? Brushes.Transparent : (Brush)FindResource(fill),
        BorderBrush = (Brush)FindResource(fill ?? foreground), BorderThickness = new Thickness(1),
        Child = new TextBlock
        {
            Text = text, FontFamily = (FontFamily)FindResource("FontBody"), FontSize = 11, FontWeight = FontWeights.Bold,
            Foreground = fill is null ? (Brush)FindResource(foreground) : fill == "Ok" ? (Brush)FindResource("Bg") : Brushes.White,
        },
    };

    private void RebuildTabList()
    {
        SyncAudioSources();
        UpdateModeButtons(); // the number of tabs decides whether Preview + Take is available
        TabListPanel.Children.Clear();
        _thumbViews.Clear();
        _thumbs.Keep(_tabs.Items.Select(t => t.Id));
        for (int i = 0; i < _tabs.Items.Count; i++)
        {
            var tab = _tabs.Items[i];
            bool active = i == _tabs.ActiveIndex;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var thumbnail = ThumbnailFor(tab.Id, TabKinds.IsDocument(tab.Kind) ? FileLabel(tab.Source) : ThumbnailLabel(tab.Url));
            grid.Children.Add(thumbnail);

            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 0, 4, 0) };
            if (_renamingId == tab.Id)
            {
                var box = new TextBox { Text = tab.Name, Style = (Style)FindResource("Field"), FontFamily = (FontFamily)FindResource("FontBody"), FontSize = 14, MinHeight = 30, Padding = new Thickness(6, 0, 6, 0), MaxLength = TabRules.MaxNameLength + 20 };
                box.Loaded += (_, _) =>
                {
                    box.Focus();
                    box.SelectAll();
                };
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        var error = RenameTab(tab.Id, box.Text);
                        if (error != TabNameError.None) { TabError.Text = RenameErrorText(error); return; }
                        _renamingId = null;
                        TabError.Text = string.Empty;
                        RebuildTabList();
                    }
                    else if (e.Key == Key.Escape)
                    {
                        _renamingId = null;
                        TabError.Text = string.Empty;
                        RebuildTabList();
                    }
                    else
                    {
                        return;
                    }
                    e.Handled = true;
                };
                box.LostKeyboardFocus += (_, _) =>
                {
                    if (_renamingId != tab.Id) return;
                    if (RenameTab(tab.Id, box.Text) == TabNameError.None) TabError.Text = string.Empty;
                    _renamingId = null;
                    Dispatcher.BeginInvoke(RebuildTabList);
                };
                content.Children.Add(box);
            }
            else
            {
                var title = new TextBlock
                {
                    Foreground = (Brush)FindResource("Text"), FontFamily = (FontFamily)FindResource("FontSemi"), FontSize = 15,
                    TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = $"{tab.Name}\n{(TabKinds.IsDocument(tab.Kind) ? tab.Source : tab.Url)}",
                };
                if (i < 9)
                    title.Inlines.Add(new Run((i + 1) + "  ") { Foreground = (Brush)FindResource("Muted"), FontFamily = (FontFamily)FindResource("FontMono"), FontSize = 12 });
                title.Inlines.Add(new Run(tab.Name));
                content.Children.Add(title);
            }

            var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            bool onAir = _tabs.PreviewMode ? tab.Id == _tabs.ProgramId : active;
            bool previewing = _tabs.PreviewMode && active;
            if (previewing) chips.Children.Add(Chip("PREVIEW", "Tab yang sedang disiapkan di preview", "Ok", "Ok"));
            else if (onAir) chips.Children.Add(Chip(_tabs.PreviewMode ? "PROGRAM" : "LIVE", "Tab ini yang sedang tayang", "Live", "LiveBg"));
            if (tab.ProfileId is not null)
                chips.Children.Add(Chip("PROFIL", "Memakai login sendiri (profil terpisah)", "Muted"));
            if (_sessions.TryGetValue(tab.Id, out var session))
            {
                var (text, brush, tip) = session switch
                {
                    SessionState.LoggedIn => ("SESI AKTIF", "Ok", "Ada cookie sesi untuk situs ini (perkiraan)"),
                    SessionState.NeedsLogin => ("PERLU LOGIN", "Warn", "Halaman login atau belum ada cookie sesi (perkiraan)"),
                    _ => ("TANPA LOGIN", "Muted", "Bukan situs web, tidak ada yang perlu login"),
                };
                chips.Children.Add(Chip(text, tip, brush));
            }
            if (_engine?.TabById(tab.Id)?.Problem is { } problem)
                chips.Children.Add(Chip("MASALAH", problem, "Live"));
            if (chips.Children.Count > 0) content.Children.Add(chips);
            Grid.SetColumn(content, 1);
            grid.Children.Add(content);

            int index = i;
            var buttons = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Top };
            buttons.Children.Add(SmallButton("✎", "Ganti nama (F2)", () => StartRename(tab.Id)));
            buttons.Children.Add(SmallButton("✕", "Tutup tab (Ctrl+W)", () => _ = CloseTabAsync(tab.Id), enabled: _tabs.CanCloseTab(tab.Id)));
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);

            var row = new Border
            {
                Child = grid, Padding = new Thickness(10, 8, 6, 8), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand, CornerRadius = new CornerRadius(3),
                Background = (Brush)FindResource("Raised"), BorderBrush = (Brush)FindResource(previewing ? "Ok" : onAir ? (_tabs.PreviewMode ? "Live" : "Warn") : "Line"),
                BorderThickness = new Thickness(previewing || onAir ? 2 : 1),
            };
            var menu = new ContextMenu();
            var rename = new MenuItem { Header = "Ganti nama (F2)" };
            rename.Click += (_, _) => StartRename(tab.Id);
            var separate = new MenuItem { Header = "Profil terpisah (login sendiri)", IsCheckable = true, IsChecked = tab.ProfileId is not null };
            separate.Click += (_, _) => _ = SetSeparateProfileAsync(tab.Id, separate.IsChecked);
            var up = new MenuItem { Header = "Naikkan", IsEnabled = index > 0 };
            up.Click += (_, _) => MoveTab(tab.Id, -1);
            var down = new MenuItem { Header = "Turunkan", IsEnabled = index < _tabs.Items.Count - 1 };
            down.Click += (_, _) => MoveTab(tab.Id, +1);
            menu.Items.Add(rename);
            menu.Items.Add(separate);
            menu.Items.Add(new Separator());
            menu.Items.Add(up);
            menu.Items.Add(down);
            row.ContextMenu = menu;
            row.MouseLeftButtonUp += (_, e) =>
            {
                if (_renamingId is null) _ = SelectTabAsync(tab.Id);
                e.Handled = true;
            };
            TabListPanel.Children.Add(row);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || Keyboard.FocusedElement is TextBox) return;
        if (_engine is not null && TryClickerKey(e)) { e.Handled = true; return; }
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (e.Key == Key.F2 && _renamingId is null) StartRename(_tabs.Active.Id);
        else if (e.Key == Key.Space && _tabs.PreviewMode && !PagePreviewView.CanPanNow) _ = TakeAsync();
        else if (ctrl && shift && e.Key == Key.F) { OnFreeze(this, e); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.B) { OnBlackout(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.T) _ = AddTabAsync();
        else if (ctrl && e.Key == Key.W) _ = CloseTabAsync(_tabs.Active.Id);
        else if (ctrl && e.Key is >= Key.D1 and <= Key.D9)
        {
            int index = e.Key - Key.D1;
            if (index < _tabs.Items.Count) _ = SelectTabAsync(_tabs.Items[index].Id);
        }
        else return;
        e.Handled = true;
    }
}
