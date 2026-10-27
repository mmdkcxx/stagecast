using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Stagecast.Core.Configuration;
using Stagecast.Core.Tabs;

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
        var items = _config.Tabs.Select(t => new TabInfo(Guid.NewGuid(), t.Name, t.Url)).ToList();
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
        await engine.AddTabAsync(_tabs.Active.Id, _tabs.Active.Url);
        foreach (var info in _tabs.Items.Where(t => t.Id != _tabs.Active.Id)) await engine.AddTabAsync(info.Id, info.Url);
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
        await _engine.AddTabAsync(info.Id, url); // the list keeps only an address worth restoring; the page gets the real one
        await SelectTabAsync(info.Id);
        return true;
    }

    internal async Task SelectTabAsync(Guid id)
    {
        if (_engine is null || _engine.TabById(id) is not { } tab || !_tabs.Select(id)) return;
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
        bool wasActive = _tabs.Active.Id == id;
        if (_tabs.Close(id) is not { } newActive) return false;
        if (wasActive && _engine.TabById(newActive.Id) is { } next) await _engine.SelectAsync(next);
        _engine.CloseTab(tab);
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

    private void RebuildTabList()
    {
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
            if (active)
                buttons.Children.Add(new TextBlock
                {
                    Text = "TAYANG", Foreground = (Brush)FindResource("Warn"), FontFamily = new FontFamily("Segoe UI"), FontSize = 10, FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0),
                });
            int index = i;
            buttons.Children.Add(SmallButton("✎", "Ganti nama (F2)", () => StartRename(tab.Id)));
            buttons.Children.Add(SmallButton("▲", "Naikkan", () => MoveTab(tab.Id, -1), enabled: index > 0));
            buttons.Children.Add(SmallButton("▼", "Turunkan", () => MoveTab(tab.Id, +1), enabled: index < _tabs.Items.Count - 1));
            buttons.Children.Add(SmallButton("✕", "Tutup tab (Ctrl+W)", () => _ = CloseTabAsync(tab.Id), enabled: _tabs.CanClose));
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);

            var row = new Border
            {
                Child = grid, Padding = new Thickness(8, 4, 4, 4), Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand,
                Background = (Brush)FindResource("Raised"), BorderBrush = (Brush)FindResource(active ? "Warn" : "LineStrong"), BorderThickness = new Thickness(1),
            };
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
        if (e.Key == Key.F2 && _renamingId is null) StartRename(_tabs.Active.Id);
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
