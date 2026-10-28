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

    // A strip of thousands of pages costs more than it helps: above this the strip is left out, and only a window around the current page gets thumbnails.
    private const int MaxStripPages = 500;
    private const int StripWindow = 150;

    private static readonly Brush NextFrame = Freeze(new SolidColorBrush(Color.FromRgb(0x4D, 0xA3, 0xFF)));

    private readonly Dictionary<Guid, string> _docMessages = [];
    private readonly HashSet<Guid> _hookedPdfs = [];
    private readonly HashSet<Guid> _pageFailed = [];
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

    /// <summary>Asks the operator for a PowerPoint file; null when they cancel. Replaceable so tests need no dialog.</summary>
    internal Func<string?> PickPowerPoint { get; set; } = () =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Presentasi PowerPoint (*.pptx)|*.pptx", CheckFileExists = true, Title = "Pilih berkas PowerPoint" };
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

    private IDocumentSource? ActivePdf => ActiveDocument?.Document;

    private static string FileLabel(string? source) => string.IsNullOrEmpty(source) ? "Berkas" : Path.GetFileName(source);

    /// <summary>What one step of the tab's file is called: a page of a PDF, a slide of a deck.</summary>
    private static string UnitName(DocumentTab tab) => tab.Kind == TabKind.PowerPoint ? "Slide" : "Halaman";

    // ---- opening ----

    private void OnAddPowerPoint(object sender, RoutedEventArgs e) => _ = AddPowerPointTabAsync();

    /// <summary>Adds a PowerPoint tab for <paramref name="path"/> (asks for the file when null) and shows its first slide.</summary>
    internal async Task<bool> AddPowerPointTabAsync(string? path = null)
    {
        if (_engine is null) return false;
        path ??= PickPowerPoint();
        if (path is null) return false;
        if (!await AddDocumentTabAsync(TabKind.PowerPoint, path)) return false;
        var info = _tabs.Items[^1];
        string name = Path.GetFileNameWithoutExtension(path);
        _tabs.Rename(info.Id, name.Length > TabRules.MaxNameLength ? name[..TabRules.MaxNameLength] : name);
        if (_engine.TabById(info.Id) is DocumentTab tab) await OpenPowerPointAsync(tab, path);
        RebuildTabList();
        SchedulePersist();
        return true;
    }

    /// <summary>Opens <paramref name="path"/> in <paramref name="tab"/> at slide <paramref name="startSlide"/> (zero-based; -1 is the first slide that is not hidden).</summary>
    internal async Task<PowerPointOpenResult> OpenPowerPointAsync(DocumentTab tab, string path, int startSlide = -1)
    {
        PowerPointSource deck = _engine!.AttachPowerPoint(tab);
        HookDocument(tab, deck);
        PowerPointOpenResult result = await deck.OpenAsync(path, startSlide);
        bool hadDocument = tab.PowerPoint is { PageCount: > 0 } && result != PowerPointOpenResult.Opened;
        if (result == PowerPointOpenResult.Opened)
            SetDocMessage(tab, deck.Warnings.Count == 0 ? null : "Perhatian: " + string.Join(" ", deck.Warnings) + " Tampilan di acara bisa berbeda.");
        else
        {
            string message = result == PowerPointOpenResult.NotInstalled
                ? "PowerPoint tidak terpasang di PC ini, jadi berkas ini tidak bisa diputar."
                : deck.Problem ?? (File.Exists(path) ? "Berkas ini tidak bisa dibuka PowerPoint." : "Berkas tidak ditemukan. Klik Pilih berkas… untuk memilih ulang.");
            SetDocMessage(tab, hadDocument ? message + " Yang tampil masih berkas sebelumnya." : message);
        }

        if (result == PowerPointOpenResult.Opened)
        {
            _pageThumbs.Remove(tab.Id);
            _stripTab = null;
            Activate(); // the show window flashes up while it starts: the operator's window gets the keyboard back
            SyncAudioSources(); // the deck's PowerPoint process may be new: its sound is captured from the first note
        }

        UpdateNavigationState();
        return result;
    }

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

    /// <summary>Opens the files of tabs restored from the saved settings. A PDF password is not asked at start; "Muat ulang" asks for it.</summary>
    private async Task OpenStoredPdfsAsync()
    {
        if (_engine is null) return;
        foreach (var info in _tabs.Items.Where(t => t.Kind is TabKind.Pdf or TabKind.PowerPoint).ToList())
        {
            if (_engine.TabById(info.Id) is not DocumentTab tab) continue;
            if (string.IsNullOrEmpty(info.Source)) { SetDocMessage(tab, "Belum ada berkas. Klik Pilih berkas…"); continue; }
            if (tab.Kind == TabKind.PowerPoint) await OpenPowerPointAsync(tab, info.Source);
            else await OpenPdfAsync(tab, info.Source, askPassword: false);
        }
        UpdateNavigationState();
    }

    /// <summary>Opens <paramref name="path"/> in <paramref name="tab"/> at page <paramref name="startPage"/> (zero-based); asks for a password when needed and allowed.</summary>
    internal async Task<PdfOpenResult> OpenPdfAsync(DocumentTab tab, string path, int startPage = 0, bool askPassword = true)
    {
        PdfSource pdf = _engine!.AttachPdf(tab);
        HookDocument(tab, pdf);
        string? password = null;
        PdfOpenResult result;
        while (true)
        {
            result = await pdf.OpenAsync(path, password, startPage);
            if (result is not (PdfOpenResult.PasswordRequired or PdfOpenResult.WrongPassword) || !askPassword) break;
            password = (AskPassword ?? ((name, wrong) => PasswordDialog.Ask(this, name, wrong)))(Path.GetFileName(path), result == PdfOpenResult.WrongPassword);
            if (password is null) break; // cancelled: the tab stays closed with the message below
        }

        bool hadDocument = tab.Pdf is { PageCount: > 0 } && result != PdfOpenResult.Opened;
        if (result == PdfOpenResult.Opened && !_pageFailed.Contains(tab.Id)) SetDocMessage(tab, null);
        else if (result != PdfOpenResult.Opened)
        {
            string message = result switch
            {
                PdfOpenResult.PasswordRequired => "Berkas ini diberi kata sandi. Klik Muat ulang untuk memasukkannya.",
                PdfOpenResult.WrongPassword => "Kata sandi salah. Klik Muat ulang untuk mencoba lagi.",
                _ => File.Exists(path) ? "Berkas rusak atau bukan PDF yang bisa dibuka." : "Berkas tidak ditemukan. Klik Pilih berkas… untuk memilih ulang.",
            };
            // A file that cannot be opened changes nothing on screen: say so, so the operator knows which document is showing.
            SetDocMessage(tab, hadDocument ? message + " Yang tampil masih berkas sebelumnya." : message);
        }

        if (result == PdfOpenResult.Opened)
        {
            _pageThumbs.Remove(tab.Id);
            _stripTab = null;
        }
        UpdateNavigationState();
        return result;
    }

    // The page controls and the strip follow the source of a tab, once.
    private void HookDocument(DocumentTab tab, IDocumentSource source)
    {
        if (!_hookedPdfs.Add(tab.Id)) return;
        source.PageChanged += () => OnPdfPageChanged(tab);
        source.PageFailed += page =>
        {
            _pageFailed.Add(tab.Id);
            SetDocMessage(tab, tab.Kind == TabKind.PowerPoint
                ? "PowerPoint tidak menjawab. Klik Muat ulang untuk membuka berkas lagi."
                : $"Halaman {page} tidak bisa digambar. Gambar yang tampil tetap halaman sebelumnya.");
            UpdateNavigationState();
        };
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
        _pageFailed.Remove(id);
        if (_stripTab == id) _stripTab = null;
    }

    private void OnDocPick(object sender, RoutedEventArgs e) => _ = ChangePdfAsync();

    /// <summary>Chooses another file for the active document tab.</summary>
    internal async Task<bool> ChangePdfAsync(string? path = null)
    {
        if (ActiveDocument is not { Kind: TabKind.Pdf or TabKind.PowerPoint } tab) return false;
        path ??= tab.Kind == TabKind.PowerPoint ? PickPowerPoint() : PickPdf();
        if (path is null) return false;
        if (IsOnAir(tab.Id) && tab.Document is { PageCount: > 0 } && !Confirm("Tab ini sedang tayang. Mengganti berkas mengubah apa yang dilihat penonton. Lanjutkan?")) return false;
        bool opened = tab.Kind == TabKind.PowerPoint
            ? await OpenPowerPointAsync(tab, path) == PowerPointOpenResult.Opened
            : await OpenPdfAsync(tab, path) == PdfOpenResult.Opened;
        if (!opened) return false; // the old file stays the tab's file
        _tabs.SetSource(tab.Id, path);
        tab.SetSource(path);
        SchedulePersist();
        return true;
    }

    private void OnDocReload(object sender, RoutedEventArgs e) => _ = ReloadPdfAsync();

    /// <summary>Opens the file again from disk and returns to the same page (asks first when the tab is on air).</summary>
    internal async Task<bool> ReloadPdfAsync()
    {
        if (ActiveDocument is not { Kind: TabKind.Pdf or TabKind.PowerPoint } tab || string.IsNullOrEmpty(tab.Source)) return false;
        int page = Math.Max(tab.Document?.PageIndex ?? 0, 0);
        if (IsOnAir(tab.Id) && tab.Document is { PageCount: > 0 } && !Confirm($"Tab ini sedang tayang. Berkas dibuka lagi dan kembali ke {UnitName(tab).ToLowerInvariant()} {page + 1}. Lanjutkan?")) return false;
        if (tab.Kind == TabKind.PowerPoint) await OpenPowerPointAsync(tab, tab.Source, page);
        else await OpenPdfAsync(tab, tab.Source, page);
        return true;
    }

    private bool IsOnAir(Guid id) => !_tabs.PreviewMode || id == _tabs.ProgramId;

    // ---- page controls ----

    // "The first" and "the last" of a deck skip the slides hidden at its ends; of a PDF they are its first and last page.
    internal static int FirstIndex(IDocumentSource? source) => source is PowerPointSource deck ? deck.FirstVisible : 0;

    internal static int LastIndex(IDocumentSource? source) => source is PowerPointSource deck ? deck.LastVisibleIndex : int.MaxValue;

    private void OnDocFirst(object sender, RoutedEventArgs e) => _ = GoToPageAsync(FirstIndex(ActivePdf));

    private void OnDocPrev(object sender, RoutedEventArgs e) => _ = ActivePdf?.PreviousAsync();

    private void OnDocNext(object sender, RoutedEventArgs e) => _ = ActivePdf?.NextAsync();

    private void OnDocLast(object sender, RoutedEventArgs e) => _ = GoToPageAsync(LastIndex(ActivePdf));

    /// <summary>Shows a page of the active PDF (zero-based; out of range goes to the nearest end).</summary>
    internal async Task<bool> GoToPageAsync(int index, IDocumentSource? target = null)
    {
        if ((target ?? ActivePdf) is not { } source) return false;
        // A slide that is hidden in the file may be hidden on purpose: ask before putting it on the screen.
        if (source is PowerPointSource { PageCount: > 0 } deck && index >= 0 && index < deck.PageCount && deck.IsHidden(index)
            && !Confirm($"Slide {index + 1} disembunyikan di berkas. Tayangkan?")) return false;
        return await source.GoToAsync(index);
    }

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
        return await GoToPageAsync(index);
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

    private void OnPdfPageChanged(DocumentTab tab)
    {
        if (_pageFailed.Remove(tab.Id)) SetDocMessage(tab, null); // a page drew fine again
        UpdateDocumentControls();
        UpdateStripHighlight();
        UpdateProgramBar();
        EnsureStripAround(tab);
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

        IDocumentSource? pdf = tab.Document;
        int count = pdf?.PageCount ?? 0;
        bool open = count > 0;
        if (!DocPageBox.IsKeyboardFocusWithin) DocPageBox.Text = open ? pdf!.PageNumber.ToString() : "";
        DocCountText.Text = open ? $"/ {count}" : "";
        DocPageBox.IsEnabled = open && count > 1;
        DocFirstButton.IsEnabled = DocPrevButton.IsEnabled = open && pdf!.PageIndex > FirstIndex(pdf);
        DocNextButton.IsEnabled = DocLastButton.IsEnabled = open && pdf!.PageIndex < (pdf is PowerPointSource ? LastIndex(pdf) : count - 1);
        DocReloadButton.IsEnabled = !string.IsNullOrEmpty(tab.Source);
        DocStatusText.Foreground = (Brush)FindResource(_docMessages.ContainsKey(tab.Id) ? "Warn" : "Muted");
        DocStatusText.Text = _docMessages.TryGetValue(tab.Id, out var message) ? message : open ? FileLabel(tab.Source) : "";
        DocStatusText.ToolTip = tab.Source;

        bool strip = open && count <= MaxStripPages;
        DocStrip.Visibility = strip ? Visibility.Visible : Visibility.Collapsed;
        if (open && !strip) DocStatusText.Text += $"  ·  Dokumen besar ({count} halaman): strip thumbnail dimatikan.";
        if (strip && (_stripTab != tab.Id || _stripPages != count)) BuildStrip(tab, pdf!);
        UpdateStripHighlight();
    }

    // ---- page strip ----

    private void BuildStrip(DocumentTab tab, IDocumentSource pdf)
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
                Cursor = Cursors.Hand, Tag = picture, ToolTip = $"{UnitName(tab)} {i + 1}",
            };
            if (pdf is PowerPointSource deck && deck.IsHidden(i))
            {
                // A hidden slide stays in the strip, dimmed and marked: Next skips it, a click asks first.
                item.Opacity = 0.5;
                item.BorderThickness = new Thickness(2);
                number.Text = $"{i + 1} TERSEMBUNYI";
                item.ToolTip = $"Slide {i + 1} (disembunyikan di berkas)";
            }
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
    private async Task LoadStripAsync(Guid id, IDocumentSource pdf, int version)
    {
        if (!_pageThumbs.TryGetValue(id, out var known)) return;
        int start = Math.Max(pdf.PageIndex, 0);
        foreach (int index in Enumerable.Range(Math.Max(0, start - StripWindow), Math.Min(pdf.PageCount, start + StripWindow + 1) - Math.Max(0, start - StripWindow))
                     .OrderBy(i => Math.Abs(i - start)).ThenBy(i => i))
        {
            if (known.ContainsKey(index)) continue;
            PageThumbnail? thumb = await pdf.RenderThumbnailAsync(index, StripThumbWidth * 2, StripThumbHeight * 2);
            if (version != _stripVersion || _stripTab != id) return; // another tab or a reload took over the strip
            if (thumb is null) continue;
            var bitmap = BitmapSource.Create(thumb.Width, thumb.Height, 96, 96, PixelFormats.Pbgra32, null, thumb.Pixels, thumb.Width * 4);
            bitmap.Freeze();
            known[index] = bitmap;
            if (index < _stripItems.Count && _stripItems[index].Tag is Image picture) picture.Source = bitmap;
        }
    }

    // The pages near the one on screen need their pictures even when the strip started at another page.
    private void EnsureStripAround(DocumentTab tab)
    {
        if (_stripTab != tab.Id || tab.Document is not { PageIndex: >= 0 } pdf || !_pageThumbs.TryGetValue(tab.Id, out var known)) return;
        if (known.ContainsKey(pdf.PageIndex) && known.ContainsKey(Math.Min(pdf.PageIndex + 3, pdf.PageCount - 1))) return;
        _ = LoadStripAsync(tab.Id, pdf, ++_stripVersion);
    }

    private void UpdateStripHighlight()
    {
        if (ActivePdf is not { } pdf || _stripItems.Count == 0) return;
        for (int i = 0; i < _stripItems.Count; i++)
            _stripItems[i].BorderBrush = i == pdf.PageIndex ? (Brush)FindResource("Live") : i == pdf.PageIndex + 1 ? NextFrame : (Brush)FindResource("Line");
        if (pdf.PageIndex >= 0 && pdf.PageIndex < _stripItems.Count) _stripItems[pdf.PageIndex].BringIntoView();
    }
}
