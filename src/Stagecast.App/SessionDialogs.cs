using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Stagecast.Core.Sessions;
using Stagecast.Core.Tabs;

namespace Stagecast.App;

/// <summary>What the operator chose when a session is opened over tabs that already exist.</summary>
/// <param name="Replace">Close the tabs that exist first (otherwise the session's tabs are added after them).</param>
/// <param name="ApplyRender">Use the resolution and rate saved in the session.</param>
/// <param name="SaveFirst">Save the session that is open now before anything changes.</param>
internal sealed record OpenChoice(bool Replace, bool ApplyRender, bool SaveFirst);

/// <summary>The questions of opening a session. Built in code like <see cref="PasswordDialog"/>; the tests replace them.</summary>
internal static class SessionDialogs
{
    private static Brush Res(string key, Brush fallback) => Application.Current.TryFindResource(key) as Brush ?? fallback;

    private static Style? Style(string key) => Application.Current.TryFindResource(key) as Style;

    private static Window Shell(Window owner, string title, UIElement content, double width)
        => new()
        {
            Title = title, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.Height, Width = width,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Content = content, Background = Res("Panel", Brushes.Black),
        };

    private static TextBlock Text(string text, double size, bool muted = false, bool bold = false, Thickness? margin = null)
        => new()
        {
            Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = Res(muted ? "Muted" : "Text", Brushes.White), Margin = margin ?? new Thickness(0),
        };

    private static Button Action(string text, bool primary, RoutedEventHandler click, bool isDefault = false, bool isCancel = false)
    {
        var button = new Button { Content = text, MinHeight = 34, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(8, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel };
        if (Style(primary ? "ActionButton" : "ToolButton") is { } style) button.Style = style;
        button.Click += click;
        return button;
    }

    /// <summary>Replace or add, with the resolution and save-first options that apply. Null when the operator cancelled.</summary>
    public static OpenChoice? AskOpenMode(Window owner, string sessionName, int currentTabs, int sessionTabs, string? renderOffer, bool onAir, bool canSaveFirst, bool saveFirstDefault)
    {
        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 20) };
        panel.Children.Add(Text($"Buka {sessionName}?", 20, bold: true));
        panel.Children.Add(Text($"Sudah ada {currentTabs} tab di sesi sekarang. Sesi ini berisi {sessionTabs} tab.", 14, muted: true, margin: new Thickness(0, 8, 0, 10)));

        var replace = new RadioButton { IsChecked = true, GroupName = "mode", Margin = new Thickness(0, 4, 0, 0), Foreground = Res("Text", Brushes.White) };
        replace.Content = new StackPanel { Children = { Text("Ganti semua tab", 15, bold: true), Text("Tab yang ada ditutup, lalu isi sesi dibuka.", 13, muted: true) } };
        var add = new RadioButton { GroupName = "mode", Margin = new Thickness(0, 10, 0, 0), Foreground = Res("Text", Brushes.White) };
        add.Content = new StackPanel { Children = { Text("Tambahkan ke tab yang ada", 15, bold: true), Text($"Tab sesi ditaruh setelah tab sekarang. Paling banyak {TabRules.MaxTabs} tab.", 13, muted: true) } };
        panel.Children.Add(replace);
        panel.Children.Add(add);

        var warn = new Border
        {
            Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(12, 9, 12, 9), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
            Background = Res("WarnBg", Brushes.Black), BorderBrush = Res("Warn", Brushes.Yellow),
            Child = Text("Tab yang sedang tayang akan ditutup. Output langsung menampilkan test pattern sampai Anda menayangkan tab lain.", 13),
        };
        ((TextBlock)warn.Child).Foreground = Res("Warn", Brushes.Yellow);
        panel.Children.Add(warn);

        CheckBox? render = null;
        if (renderOffer is not null)
        {
            render = new CheckBox { IsChecked = true, Margin = new Thickness(0, 14, 0, 0), Foreground = Res("Text", Brushes.White), Content = Text(renderOffer, 14) };
            panel.Children.Add(render);
        }
        CheckBox? save = null;
        if (canSaveFirst)
        {
            save = new CheckBox { IsChecked = saveFirstDefault, Margin = new Thickness(0, 12, 0, 0), Foreground = Res("Text", Brushes.White), Content = Text("Simpan sesi sekarang dulu", 14) };
            panel.Children.Add(save);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var window = Shell(owner, "Stagecast", panel, 560);
        var ok = Action("Ganti dan buka", true, (_, _) => window.DialogResult = true, isDefault: true);
        buttons.Children.Add(Action("Batal", false, (_, _) => window.DialogResult = false, isCancel: true));
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        void Refresh()
        {
            bool replacing = replace.IsChecked == true;
            ok.Content = replacing ? "Ganti dan buka" : "Tambahkan";
            warn.Visibility = replacing && onAir ? Visibility.Visible : Visibility.Collapsed;
            if (render is not null) render.Visibility = replacing ? Visibility.Visible : Visibility.Collapsed;
        }
        replace.Checked += (_, _) => Refresh();
        add.Checked += (_, _) => Refresh();
        Refresh();

        if (window.ShowDialog() != true) return null;
        bool replacingNow = replace.IsChecked == true;
        return new OpenChoice(replacingNow, replacingNow && render?.IsChecked == true, save?.IsChecked == true);
    }

    /// <summary>
    /// Lists the documents of a session and where each was found. The operator points at files or a folder until none is missing or
    /// decides to go on without them. Returns the plan to open, or null when cancelled.
    /// </summary>
    public static SessionPlan? Relink(Window owner, string sessionName, SessionPlan start, ISessionFiles files, Func<PlannedTab, string?> pickFile, Func<string?> pickFolder)
    {
        var plan = start;
        var rows = new StackPanel();
        var header = Text("", 14, muted: true, margin: new Thickness(0, 8, 0, 14));
        var hint = Text("Pilih satu berkas, lalu Stagecast mencari berkas lain dengan nama yang sama di folder itu. Anda tidak perlu menjawab satu per satu.", 13, muted: true, margin: new Thickness(0, 14, 0, 0));
        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 20) };
        panel.Children.Add(Text("Berkas tidak ditemukan", 20, bold: true));
        panel.Children.Add(header);
        panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(hint);

        var window = Shell(owner, "Stagecast", panel, 760);
        Button primary = null!;
        Button without = null!;

        // A path the operator picked in a dialog is theirs to look at, also on the network; the guard is for what a file says by itself.
        ISessionFiles For(string picked) => NetworkGuard.IsNetworkPath(picked) ? new DiskFiles() : files;

        void Render()
        {
            rows.Children.Clear();
            int total = plan.Tabs.Count(t => t.Match != SourceMatch.NotNeeded), missing = plan.MissingCount;
            header.Text = missing == 0
                ? $"Semua {total} berkas sudah ketemu."
                : $"Sesi {sessionName} dibuat di komputer lain. {missing} dari {total} berkas belum ada di komputer ini.";
            for (int i = 0; i < plan.Tabs.Count; i++)
            {
                var tab = plan.Tabs[i];
                if (tab.Match == SourceMatch.NotNeeded) continue;
                int index = i;
                var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var badge = Text(tab.Info.Kind == TabKind.PowerPoint ? "PPT" : "PDF", 11, muted: true, bold: true);
                badge.Margin = new Thickness(0, 2, 12, 0);
                var details = new StackPanel();
                details.Children.Add(Text(Path.GetFileName(tab.Info.Source ?? tab.Saved.Source ?? tab.Info.Name) is { Length: > 0 } n ? n : tab.Info.Name, 15, bold: true));
                details.Children.Add(Text(Detail(tab), 12, muted: true));
                var status = Text(tab.IsMissing ? "Hilang" : "Ditemukan", 13, bold: true, margin: new Thickness(12, 0, 12, 0));
                status.Foreground = Res(tab.IsMissing ? "Warn" : "Ok", Brushes.White);
                status.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(details, 1);
                Grid.SetColumn(status, 2);
                row.Children.Add(badge);
                row.Children.Add(details);
                row.Children.Add(status);
                if (tab.IsMissing)
                {
                    var find = Action("Cari…", false, (_, _) =>
                    {
                        if (pickFile(tab) is { } picked) plan = plan.WithChosenFile(index, picked, For(picked));
                        Render();
                    });
                    find.Margin = new Thickness(0);
                    Grid.SetColumn(find, 3);
                    row.Children.Add(find);
                }
                rows.Children.Add(row);
            }
            hint.Visibility = missing == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (primary is not null)
            {
                primary.Content = missing == 0 ? "Buka sesi" : "Pilih folder berkas…";
                without.Visibility = missing == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(Action("Batal", false, (_, _) => window.DialogResult = false, isCancel: true));
        without = Action("Buka tanpa berkas yang hilang", false, (_, _) => window.DialogResult = true);
        primary = Action("Pilih folder berkas…", true, (_, _) =>
        {
            if (plan.MissingCount == 0) { window.DialogResult = true; return; }
            if (pickFolder() is { } folder) plan = plan.WithFolder(folder, For(folder));
            Render();
        }, isDefault: true);
        buttons.Children.Add(without);
        buttons.Children.Add(primary);
        panel.Children.Add(buttons);
        Render();

        return window.ShowDialog() == true ? plan : null;
    }

    private static string Detail(PlannedTab tab) => tab.Match switch
    {
        SourceMatch.Original => tab.FoundPath!,
        SourceMatch.RelativeToSession => $"Ketemu lewat folder sesi: {tab.FoundPath}",
        SourceMatch.NextToSession => $"Ketemu di sebelah berkas sesi: {tab.FoundPath}",
        SourceMatch.ChosenFolder => $"Ketemu di folder pilihan: {tab.FoundPath}",
        SourceMatch.Chosen => $"Dipilih: {tab.FoundPath}",
        _ => $"Dulu: {tab.Saved.Source}",
    };
}
