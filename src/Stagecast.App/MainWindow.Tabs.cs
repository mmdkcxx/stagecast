using System.Windows;
using System.Windows.Controls;
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
        var items = _config.Tabs.Select(t => new TabInfo(Guid.NewGuid(), t.Name, t.Url, t.ProfileId)).ToList();
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
        await engine.AddTabAsync(_tabs.Active.Id, _tabs.Active.Url, _tabs.Active.ProfileId);
        // A snapshot: pages start loading while the next tabs are created, and their address changes update the list.
        foreach (var info in _tabs.Items.Where(t => t.Id != _tabs.Active.Id).ToList()) await engine.AddTabAsync(info.Id, info.Url, info.ProfileId);
        engine.SortTabs(_tabs.Items.Select(t => t.Id).ToList());
        RebuildTabList();
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
            if (_engine.TabById(info.Id) is not { } tab) continue;
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
        if (_engine is null || _engine.TabById(id) is not { } tab) return false;
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

    private Button SmallButton(string glyph, string tip, Action click, bool enabled = true)
    {
        var button = new Button
        {
            Content = glyph, ToolTip = tip, Style = (Style)FindResource("ToolButton"), Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(3, 0, 0, 0), FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 12, IsEnabled = enabled, Focusable = false,
        };
        button.Click += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return button;
    }

    private TextBlock Badge(string text, string tip, string brush) => new()
    {
        Text = text, ToolTip = tip, Foreground = (Brush)FindResource(brush), FontFamily = new FontFamily("Segoe UI"), FontSize = 10, FontWeight = FontWeights.Bold,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
    };

    private void RebuildTabList()
    {
        SyncAudioSources();
        TabListPanel.Children.Clear();
        for (int i = 0; i < _tabs.Items.Count; i++)
        {
            var tab = _tabs.Items[i];
            bool active = i == _tabs.ActiveIndex;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var number = new TextBlock
            {
                Text = i < 9 ? (i + 1).ToString() : "", Width = 16, Foreground = (Brush)FindResource("Muted"), FontFamily = new FontFamily("Consolas"),
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            };
            grid.Children.Add(number);

            FrameworkElement nameElement;
            if (_renamingId == tab.Id)
            {
                var box = new TextBox { Text = tab.Name, Style = (Style)FindResource("Field"), FontFamily = new FontFamily("Segoe UI"), FontSize = 13, Padding = new Thickness(4, 2, 4, 2), MaxLength = TabRules.MaxNameLength + 20 };
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
                nameElement = box;
            }
            else
            {
                nameElement = new TextBlock
                {
                    Text = tab.Name, Foreground = (Brush)FindResource("Text"), FontFamily = new FontFamily("Segoe UI"), FontSize = 13,
                    FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = $"{tab.Name}\n{tab.Url}",
                };
            }
            Grid.SetColumn(nameElement, 1);
            grid.Children.Add(nameElement);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            if (tab.ProfileId is not null)
                buttons.Children.Add(Badge("PROFIL", "Memakai login sendiri (profil terpisah)", "Muted"));
            if (_sessions.TryGetValue(tab.Id, out var session))
            {
                var (text, brush, tip) = session switch
                {
                    SessionState.LoggedIn => ("SESI AKTIF", "Ok", "Ada cookie sesi untuk situs ini (perkiraan)"),
                    SessionState.NeedsLogin => ("PERLU LOGIN", "Warn", "Halaman login atau belum ada cookie sesi (perkiraan)"),
                    _ => ("TANPA LOGIN", "Muted", "Bukan situs web, tidak ada yang perlu login"),
                };
                buttons.Children.Add(Badge(text, tip, brush));
            }
            if (_engine?.TabById(tab.Id)?.Problem is { } problem)
                buttons.Children.Add(Badge("MASALAH", problem, "Live"));
            bool onAir = _tabs.PreviewMode ? tab.Id == _tabs.ProgramId : active;
            bool previewing = _tabs.PreviewMode && active;
            if (onAir || previewing)
                buttons.Children.Add(new TextBlock
                {
                    Text = previewing ? "PREVIEW" : _tabs.PreviewMode ? "PROGRAM" : "TAYANG", Foreground = (Brush)FindResource(previewing ? "Ok" : _tabs.PreviewMode ? "Live" : "Warn"), FontFamily = new FontFamily("Segoe UI"), FontSize = 10, FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0),
                });
            int index = i;
            buttons.Children.Add(SmallButton("✎", "Ganti nama (F2)", () => StartRename(tab.Id)));
            buttons.Children.Add(SmallButton("▲", "Naikkan", () => MoveTab(tab.Id, -1), enabled: index > 0));
            buttons.Children.Add(SmallButton("▼", "Turunkan", () => MoveTab(tab.Id, +1), enabled: index < _tabs.Items.Count - 1));
            buttons.Children.Add(SmallButton("✕", "Tutup tab (Ctrl+W)", () => _ = CloseTabAsync(tab.Id), enabled: _tabs.CanCloseTab(tab.Id)));
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);

            var row = new Border
            {
                Child = grid, Padding = new Thickness(8, 4, 4, 4), Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand,
                Background = (Brush)FindResource("Raised"), BorderBrush = (Brush)FindResource(previewing ? "Ok" : onAir ? (_tabs.PreviewMode ? "Live" : "Warn") : "LineStrong"), BorderThickness = new Thickness(1),
            };
            var menu = new ContextMenu();
            var rename = new MenuItem { Header = "Ganti nama (F2)" };
            rename.Click += (_, _) => StartRename(tab.Id);
            var separate = new MenuItem { Header = "Profil terpisah (login sendiri)", IsCheckable = true, IsChecked = tab.ProfileId is not null };
            separate.Click += (_, _) => _ = SetSeparateProfileAsync(tab.Id, separate.IsChecked);
            menu.Items.Add(rename);
            menu.Items.Add(separate);
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
