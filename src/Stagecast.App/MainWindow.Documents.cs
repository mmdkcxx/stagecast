using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stagecast.Core.Tabs;
using Stagecast.Rendering;

namespace Stagecast.App;

// Document tabs (PDF): choosing a file, the page controls, the page strip under the preview.
public partial class MainWindow
{
    private const int StripThumbWidth = 128;
    private const int StripThumbHeight = 72;

    private static readonly Brush NextFrame = Freeze(new SolidColorBrush(Color.FromRgb(0x4D, 0xA3, 0xFF)));

    private readonly Dictionary<Guid, string> _docMessages = [];
    private readonly HashSet<Guid> _hookedPdfs = [];
    private readonly Dictionary<Guid, Dictionary<int, ImageSource>> _pageThumbs = [];
    private readonly List<Border> _stripItems = [];
    private Guid? _stripTab;
    private int _stripPages;
    private int _stripVersion;

    /// <summary>Asks the operator for a PDF file; null when they cancel. Replaceable so tests need no dialog.</summary>
    internal Func<string?> PickPdf { get; set; } = () =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Berkas PDF (*.pdf)|*.pdf", CheckFileExists = true, Title = "Pilih berkas PDF" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };

    /// <summary>Asks for a password (file name, whether the last one was wrong); null when cancelled. Replaceable for tests.</summary>
    internal Func<string, bool, string?> AskPassword { get; set; } = null!;

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private DocumentTab? ActiveDocument => _engine?.Active as DocumentTab;

    private PdfSource? ActivePdf => ActiveDocument?.Pdf;

    private static string FileLabel(string? source) => string.IsNullOrEmpty(source) ? "PDF" : Path.GetFileName(source);

    // ---- opening ----

    private void OnAddPdf(object sender, RoutedEventArgs e) => _ = AddPdfTabAsync();

    /// <summary>Adds a PDF tab for <paramref name="path"/> (asks for the file when null) and shows its first page.</summary>
    internal async Task<bool> AddPdfTabAsync(string? path = null)
    {
        if (_engine is null) return false;
        path ??= PickPdf();
        if (path is null) return false;
        if (!await AddDocumentTabAsync(TabKind.Pdf, path)) return false;
        var info = _tabs.Items[^1];
        string name = Path.GetFileNameWithoutExtension(path);
        _tabs.Rename(info.Id, name.Length > TabRules.MaxNameLength ? name[..TabRules.MaxNameLength] : name); // a name that is rejected keeps the default
        if (_engine.TabById(info.Id) is DocumentTab tab) await OpenPdfAsync(tab, path);
        RebuildTabList();
        SchedulePersist();
        return true;
    }

    /// <summary>Opens the PDF of tabs restored from the saved settings. A password is not asked at start; "Muat ulang" asks for it.</summary>
    private async Task OpenStoredPdfsAsync()
    {
        if (_engine is null) return;
        foreach (var info in _tabs.Items.Where(t => t.Kind == TabKind.Pdf).ToList())
        {
            if (_engine.TabById(info.Id) is not DocumentTab tab) continue;
            if (string.IsNullOrEmpty(info.Source)) { SetDocMessage(tab, "Belum ada berkas. Klik Pilih berkas…"); continue; }
            await OpenPdfAsync(tab, info.Source, askPassword: false);
        }
        UpdateNavigationState();
    }

    /// <summary>Opens <paramref name="path"/> in <paramref name="tab"/> at page <paramref name="startPage"/> (zero-based); asks for a password when needed and allowed.</summary>
    internal async Task<PdfOpenResult> OpenPdfAsync(DocumentTab tab, string path, int startPage = 0, bool askPassword = true)
    {
        PdfSource pdf = _engine!.AttachPdf(tab);
        if (_hookedPdfs.Add(tab.Id)) pdf.PageChanged += OnPdfPageChanged;
        string? password = null;
        PdfOpenResult result;
        while (true)
        {
            result = await pdf.OpenAsync(path, password, startPage);
            if (result is not (PdfOpenResult.PasswordRequired or PdfOpenResult.WrongPassword) || !askPassword) break;
            password = (AskPassword ?? ((name, wrong) => PasswordDialog.Ask(this, name, wrong)))(Path.GetFileName(path), result == PdfOpenResult.WrongPassword);
            if (password is null) break; // cancelled: the tab stays closed with the message below
        }

        SetDocMessage(tab, result switch
        {
            PdfOpenResult.Opened => null,
            PdfOpenResult.PasswordRequired => "Berkas ini diberi kata sandi. Klik Muat ulang untuk memasukkannya.",
            PdfOpenResult.WrongPassword => "Kata sandi salah. Klik Muat ulang untuk mencoba lagi.",
            _ => File.Exists(path) ? "Berkas rusak atau bukan PDF yang bisa dibuka." : "Berkas tidak ditemukan. Klik Pilih berkas… untuk memilih ulang.",
        });
        _pageThumbs.Remove(tab.Id);
        _stripTab = null;
        UpdateNavigationState();
        return result;
    }

    private void SetDocMessage(DocumentTab tab, string? message)
    {
        if (message is null) _docMessages.Remove(tab.Id);
        else _docMessages[tab.Id] = message;
        tab.SetProblem(message);
        RebuildTabList();
    }

    private void ForgetDocument(Guid id)
    {
        _docMessages.Remove(id);
        _hookedPdfs.Remove(id);
        _pageThumbs.Remove(id);
        if (_stripTab == id) _stripTab = null;
    }

    private void OnDocPick(object sender, RoutedEventArgs e) => _ = ChangePdfAsync();

    /// <summary>Chooses another file for the active PDF tab.</summary>
    internal async Task<bool> ChangePdfAsync(string? path = null)
    {
        if (ActiveDocument is not { Kind: TabKind.Pdf } tab) return false;
        path ??= PickPdf();
        if (path is null) return false;
        if (IsOnAir(tab.Id) && tab.Pdf is { PageCount: > 0 } && !Confirm("Tab ini sedang tayang. Mengganti berkas mengubah apa yang dilihat penonton. Lanjutkan?")) return false;
        _tabs.SetSource(tab.Id, path);
        tab.SetSource(path);
        await OpenPdfAsync(tab, path);
        SchedulePersist();
        return true;
    }

    private void OnDocReload(object sender, RoutedEventArgs e) => _ = ReloadPdfAsync();

    /// <summary>Opens the file again from disk and returns to the same page (asks first when the tab is on air).</summary>
    internal async Task<bool> ReloadPdfAsync()
    {
        if (ActiveDocument is not { Kind: TabKind.Pdf } tab || string.IsNullOrEmpty(tab.Source)) return false;
        int page = Math.Max(tab.Pdf?.PageIndex ?? 0, 0);
        if (IsOnAir(tab.Id) && tab.Pdf is { PageCount: > 0 } && !Confirm($"Tab ini sedang tayang. Berkas dibuka lagi dan kembali ke halaman {page + 1}. Lanjutkan?")) return false;
        await OpenPdfAsync(tab, tab.Source, page);
        return true;
    }

    private bool IsOnAir(Guid id) => !_tabs.PreviewMode || id == _tabs.ProgramId;

    // ---- page controls ----

    private void OnDocFirst(object sender, RoutedEventArgs e) => _ = GoToPageAsync(0);

    private void OnDocPrev(object sender, RoutedEventArgs e) => _ = ActivePdf?.PreviousAsync();

    private void OnDocNext(object sender, RoutedEventArgs e) => _ = ActivePdf?.NextAsync();

    private void OnDocLast(object sender, RoutedEventArgs e) => _ = GoToPageAsync(int.MaxValue);

    /// <summary>Shows a page of the active PDF (zero-based; out of range goes to the nearest end).</summary>
    internal async Task<bool> GoToPageAsync(int index) => ActivePdf is { } pdf && await pdf.GoToAsync(index);

    /// <summary>The operator typed a page number: goes there, or says what is accepted and leaves the page as it is.</summary>
    internal async Task<bool> JumpToTypedPageAsync(string? text)
    {
        if (ActivePdf is not { PageCount: > 0 } pdf) return false;
        if (!PageJump.TryParse(text, pdf.PageCount, out int index))
        {
            DocPageBox.Text = pdf.PageNumber.ToString();
            DocStatusText.Text = PageJump.Hint(pdf.PageCount);
            DocStatusText.Foreground = (Brush)FindResource("Warn");
            return false;
        }
        return await pdf.GoToAsync(index);
    }

    private void OnDocPageKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = JumpToTypedPageAsync(DocPageBox.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DocPageBox.Text = ActivePdf?.PageNumber.ToString() ?? "";
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void OnDocPageLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ActivePdf is { } pdf) DocPageBox.Text = pdf.PageNumber.ToString();
    }

    private void OnPdfPageChanged()
    {
        UpdateDocumentControls();
        UpdateStripHighlight();
        UpdateProgramBar();
    }

    /// <summary>Shows the document bar and the page strip for a document tab, the address bar for a web page.</summary>
    private void UpdateDocumentControls()
    {
        UpdateProgramBar();
        var tab = ActiveDocument;
        bool document = tab is not null;
        AddressRow.Visibility = document ? Visibility.Collapsed : Visibility.Visible;
        DocRow.Visibility = document ? Visibility.Visible : Visibility.Collapsed;
        InteractButton.Visibility = MoreButton.Visibility = document ? Visibility.Collapsed : Visibility.Visible;
        if (tab is null)
        {
            DocStrip.Visibility = Visibility.Collapsed;
            if (_stripTab is not null)
            {
                _stripTab = null;
                DocStripPanel.Children.Clear();
                _stripItems.Clear();
            }
            return;
        }

        PdfSource? pdf = tab.Pdf;
        int count = pdf?.PageCount ?? 0;
        bool open = count > 0;
        if (!DocPageBox.IsKeyboardFocusWithin) DocPageBox.Text = open ? pdf!.PageNumber.ToString() : "";
        DocCountText.Text = open ? $"/ {count}" : "";
        DocPageBox.IsEnabled = open && count > 1;
        DocFirstButton.IsEnabled = DocPrevButton.IsEnabled = open && pdf!.PageIndex > 0;
        DocNextButton.IsEnabled = DocLastButton.IsEnabled = open && pdf!.PageIndex < count - 1;
        DocReloadButton.IsEnabled = !string.IsNullOrEmpty(tab.Source);
        DocStatusText.Foreground = (Brush)FindResource(_docMessages.ContainsKey(tab.Id) ? "Warn" : "Muted");
        DocStatusText.Text = _docMessages.TryGetValue(tab.Id, out var message) ? message : open ? FileLabel(tab.Source) : "";
        DocStatusText.ToolTip = tab.Source;

        DocStrip.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open && (_stripTab != tab.Id || _stripPages != count)) BuildStrip(tab, pdf!);
        UpdateStripHighlight();
    }

    // ---- page strip ----

    private void BuildStrip(DocumentTab tab, PdfSource pdf)
    {
        DocStripPanel.Children.Clear();
        _stripItems.Clear();
        _stripTab = tab.Id;
        _stripPages = pdf.PageCount;
        int version = ++_stripVersion;
        if (!_pageThumbs.TryGetValue(tab.Id, out var known)) _pageThumbs[tab.Id] = known = [];

        for (int i = 0; i < pdf.PageCount; i++)
        {
            int index = i;
            var picture = new Image { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            if (known.TryGetValue(i, out var have)) picture.Source = have;
            var number = new TextBlock
            {
                Text = (i + 1).ToString(), Foreground = Brushes.White, FontFamily = (FontFamily)FindResource("FontMono"), FontSize = 11,
                Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), Padding = new Thickness(4, 0, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            };
            var grid = new Grid { Width = StripThumbWidth, Height = StripThumbHeight, Background = (Brush)FindResource("Bg") };
            grid.Children.Add(picture);
            grid.Children.Add(number);
            var item = new Border
            {
                Child = grid, Margin = new Thickness(0, 0, 8, 0), BorderThickness = new Thickness(2), BorderBrush = (Brush)FindResource("Line"),
                Cursor = Cursors.Hand, Tag = picture, ToolTip = $"Halaman {i + 1}",
            };
            item.MouseLeftButtonUp += (_, e) =>
            {
                _ = GoToPageAsync(index);
                e.Handled = true;
            };
            _stripItems.Add(item);
            DocStripPanel.Children.Add(item);
        }

        _ = LoadStripAsync(tab.Id, pdf, version);
    }

    // Thumbnails are drawn one at a time, starting at the page on screen and moving outwards, so the visible ones come first.
    private async Task LoadStripAsync(Guid id, PdfSource pdf, int version)
    {
        if (!_pageThumbs.TryGetValue(id, out var known)) return;
        int start = Math.Max(pdf.PageIndex, 0);
        foreach (int index in Enumerable.Range(0, pdf.PageCount).OrderBy(i => Math.Abs(i - start)).ThenBy(i => i))
        {
            if (known.ContainsKey(index)) continue;
            PdfThumbnail? thumb = await pdf.RenderThumbnailAsync(index, StripThumbWidth * 2, StripThumbHeight * 2);
            if (version != _stripVersion || _stripTab != id) return; // another tab or a reload took over the strip
            if (thumb is null) continue;
            var bitmap = BitmapSource.Create(thumb.Width, thumb.Height, 96, 96, PixelFormats.Pbgra32, null, thumb.Pixels, thumb.Width * 4);
            bitmap.Freeze();
            known[index] = bitmap;
            if (index < _stripItems.Count && _stripItems[index].Tag is Image picture) picture.Source = bitmap;
        }
    }

    private void UpdateStripHighlight()
    {
        if (ActivePdf is not { } pdf || _stripItems.Count == 0) return;
        for (int i = 0; i < _stripItems.Count; i++)
            _stripItems[i].BorderBrush = i == pdf.PageIndex ? (Brush)FindResource("Live") : i == pdf.PageIndex + 1 ? NextFrame : (Brush)FindResource("Line");
        if (pdf.PageIndex >= 0 && pdf.PageIndex < _stripItems.Count) _stripItems[pdf.PageIndex].BringIntoView();
    }
}
