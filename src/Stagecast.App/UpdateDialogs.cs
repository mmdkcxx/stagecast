using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Stagecast.Core.Updates;

namespace Stagecast.App;

internal enum UpdateChoice { Later, Install, Skip }

/// <summary>The two windows of an update: the details with the release notes, and the download. Built in code like <see cref="SessionDialogs"/>.</summary>
internal static class UpdateDialogs
{
    private static Brush Res(string key, Brush fallback) => Application.Current.TryFindResource(key) as Brush ?? fallback;

    private static Button Action(string text, bool primary, RoutedEventHandler click, bool isDefault = false, bool isCancel = false)
    {
        var button = new Button { Content = text, MinHeight = 34, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(8, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel };
        if (Application.Current.TryFindResource(primary ? "ActionButton" : "ToolButton") is Style style) button.Style = style;
        button.Click += click;
        return button;
    }

    private static TextBlock Text(string text, double size, bool muted = false, bool bold = false, Thickness? margin = null)
        => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Res(muted ? "Muted" : "Text", Brushes.White), Margin = margin ?? new Thickness(0) };

    private static Window Shell(Window owner, UIElement content, double width)
        => new()
        {
            Title = "Stagecast", Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.Height, Width = width,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Content = content, Background = Res("Panel", Brushes.Black),
        };

    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("id-ID")) + " MB";

    /// <summary>The release notes and the choice. With an output on, a warning says that installing stops it.</summary>
    public static UpdateChoice ShowDetails(Window owner, UpdateInfo info, string current, string liveOutputs)
    {
        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 20) };
        panel.Children.Add(Text($"Stagecast {info.Version} tersedia", 20, bold: true));
        string when = info.PublishedAt is { } p ? $" · dirilis {p.LocalDateTime.ToString("d MMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("id-ID"))}" : "";
        string size = info.InstallerSize > 0 ? $" · {Megabytes(info.InstallerSize)}" : "";
        panel.Children.Add(Text($"{current} → {info.Version}{when}{size}", 13, muted: true, margin: new Thickness(0, 6, 0, 14)));

        var notes = new StackPanel();
        foreach (var (kind, line) in ReleaseNotesText.Lines(info.Notes))
        {
            notes.Children.Add(kind switch
            {
                NoteLine.Heading => Text(line.ToUpperInvariant(), 12, muted: true, bold: true, margin: new Thickness(0, 8, 0, 4)),
                NoteLine.Bullet => Text("•  " + line, 14, margin: new Thickness(0, 0, 0, 4)),
                _ => Text(line, 14, margin: new Thickness(0, 0, 0, 4)),
            });
        }
        if (notes.Children.Count == 0) notes.Children.Add(Text("Tidak ada catatan rilis.", 14, muted: true));
        panel.Children.Add(new Border
        {
            Background = Res("Bg", Brushes.Black), BorderBrush = Res("Line", Brushes.Gray), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(14, 10, 14, 10),
            Child = new ScrollViewer { Content = notes, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        });

        if (liveOutputs.Length > 0)
        {
            var warn = Text($"Output sedang menyala ({liveOutputs}). Memasang pembaruan menutup Stagecast dan menghentikan output. Jangan lakukan ini saat acara berlangsung.", 13);
            warn.Foreground = Res("Warn", Brushes.Yellow);
            panel.Children.Add(new Border
            {
                Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(12, 9, 12, 9), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
                Background = Res("WarnBg", Brushes.Black), BorderBrush = Res("Warn", Brushes.Yellow), Child = warn,
            });
        }

        var window = Shell(owner, panel, 580);
        var choice = UpdateChoice.Later;
        var buttons = new DockPanel { Margin = new Thickness(0, 20, 0, 0), LastChildFill = false };
        var skip = Action("Lewati versi ini", false, (_, _) => { choice = UpdateChoice.Skip; window.Close(); });
        skip.Margin = new Thickness(0);
        DockPanel.SetDock(skip, Dock.Left);
        buttons.Children.Add(skip);
        var install = Action(liveOutputs.Length > 0 ? "Hentikan output dan pasang" : "Unduh dan pasang", true, (_, _) => { choice = UpdateChoice.Install; window.Close(); }, isDefault: liveOutputs.Length == 0);
        DockPanel.SetDock(install, Dock.Right);
        var later = Action("Nanti", false, (_, _) => window.Close(), isCancel: true);
        DockPanel.SetDock(later, Dock.Right);
        buttons.Children.Add(install);
        buttons.Children.Add(later);
        panel.Children.Add(buttons);
        panel.Children.Add(Text(info.PageUrl, 12, muted: true, margin: new Thickness(0, 12, 0, 0)));
        window.ShowDialog();
        return choice;
    }

    /// <summary>
    /// Downloads with a progress bar. Returns the installer path once its hash matched, or null when cancelled or failed
    /// (a failure is explained in the window first, with a way to try again or to open the release page).
    /// </summary>
    public static string? ShowDownload(Window owner, UpdateInfo info, UpdateService service)
    {
        var title = Text($"Mengunduh {info.Version}", 20, bold: true);
        var name = Text(info.InstallerName, 13, muted: true, margin: new Thickness(0, 6, 0, 14));
        var bar = new ProgressBar { Height = 8, Minimum = 0, Maximum = 100 };
        var amount = Text("", 13, muted: true, margin: new Thickness(0, 8, 0, 0));
        var status = Text("Mengunduh…", 14, margin: new Thickness(0, 14, 0, 0));
        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 20) };
        foreach (var child in new UIElement[] { title, name, bar, amount, status }) panel.Children.Add(child);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        panel.Children.Add(buttons);

        var window = Shell(owner, panel, 520);
        string? result = null;
        CancellationTokenSource? cts = null;

        async void Run()
        {
            cts = new CancellationTokenSource();
            var token = cts.Token;
            buttons.Children.Clear();
            buttons.Children.Add(Action("Batal", false, (_, _) => cts.Cancel(), isCancel: true));
            title.Text = $"Mengunduh {info.Version}";
            title.Foreground = Res("Text", Brushes.White);
            bar.Value = 0;
            status.Text = "Mengunduh…";
            status.Foreground = Res("Text", Brushes.White);
            var progress = new Progress<(long Received, long Total)>(p =>
            {
                bar.Value = p.Total > 0 ? 100.0 * p.Received / p.Total : 0;
                amount.Text = p.Total > 0 ? $"{Megabytes(p.Received)} dari {Megabytes(p.Total)}" : Megabytes(p.Received);
            });
            try
            {
                result = await service.DownloadAsync(info, progress, token);
                window.DialogResult = true;
            }
            catch (OperationCanceledException)
            {
                window.DialogResult = false;
            }
            catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or IOException or UnauthorizedAccessException)
            {
                bool hash = ex is InvalidDataException;
                title.Text = "Pembaruan dibatalkan";
                title.Foreground = Res("Warn", Brushes.Yellow);
                status.Text = hash
                    ? "Hash berkas tidak cocok, berkas dihapus dan tidak dijalankan. Coba lagi, atau unduh sendiri dari halaman rilis."
                    : $"Unduhan gagal: {ex.Message}";
                status.Foreground = Res("Warn", Brushes.Yellow);
                buttons.Children.Clear();
                buttons.Children.Add(Action("Tutup", false, (_, _) => window.DialogResult = false, isCancel: true));
                buttons.Children.Add(Action("Buka halaman rilis", false, (_, _) => Process.Start(new ProcessStartInfo(info.PageUrl) { UseShellExecute = true })));
                buttons.Children.Add(Action("Coba lagi", true, (_, _) => Run(), isDefault: true));
            }
        }

        window.Loaded += (_, _) => Run();
        window.Closed += (_, _) => cts?.Cancel();
        window.ShowDialog();
        return result;
    }
}
