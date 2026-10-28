using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Frames;
using Stagecast.Rendering.Capture;
using Vortice.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.UI.Composition;

namespace Stagecast.Rendering;

public enum PowerPointOpenResult
{
    Opened,

    /// <summary>PowerPoint is not installed (or automation is blocked on this PC).</summary>
    NotInstalled,

    /// <summary>Missing, damaged, protected or not a .pptx; <see cref="PowerPointSource.Problem"/> says what happened.</summary>
    Failed,
}

/// <summary>
/// Plays a .pptx on a <see cref="DocumentTab"/> with the installed PowerPoint: the file is opened from a temporary copy, its slide
/// show runs full screen in PowerPoint's own window, which is moved off every screen so nobody sees it, and Windows Graphics
/// Capture carries that window into the tab's visual (scale to fit, see <see cref="FitComposer"/>) and from there to every output.
/// Animations, transitions and video are PowerPoint's own. Slides move only when told to (timings in the file are ignored).
/// Every call into PowerPoint runs on one thread of its own, one at a time, and is repeated while PowerPoint says it is busy.
/// A PowerPoint the operator already runs is borrowed: only this file's presentation is closed, never the program itself.
/// </summary>
public sealed class PowerPointSource : IDocumentSource, IDisposable
{
    private const string TempPrefix = "stagecast-pptx-";
    private const uint RetryLater = 0x8001010A;
    private const uint CallRejected = 0x80010001;
    private static bool _tempCleaned;

    // Two updates of the picture a few milliseconds apart (a slide change makes two frames) can leave the stage capture with the first one:
    // a still slide makes no frame to correct it. This long after the last frame the picture is touched, which makes one more.
    private const int SettleMilliseconds = 150;

    private readonly Compositor _compositor;
    private readonly DocumentSurfaces _surfaces;
    private readonly SpriteVisual _page;
    private readonly ILogger _logger;
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private readonly object _frameGate = new();

    // Used on the PowerPoint thread only.
    private dynamic? _app;
    private dynamic? _presentation;
    private dynamic? _window;
    private string? _tempFile;
    private string? _thumbDir;
    private IntPtr _hwnd;
    private static readonly object StartedGate = new();
    private static int s_startedPid; // the PowerPoint process a deck of ours started, 0 when the operator's own runs
    private bool _ownApp;
    private int _ownPid;
    private bool[] _hidden = [];

    // Used by the frame callback (a capture thread) under _frameGate.
    private WindowCapturer? _capturer;
    private CompositionDrawingSurface? _surface;
    private int _surfaceWidth;
    private int _surfaceHeight;
    private double _slideWidth;
    private double _slideHeight;
    private int _canvasWidth;
    private int _canvasHeight;
    private bool _disposed;
    private Timer? _settle;
    private bool _nudged;

    private volatile int _processId;
    private volatile int _pageCount;
    private volatile int _pageIndex = -1;
    private volatile string? _problem;
    private volatile IReadOnlyList<string> _warnings = [];

    internal PowerPointSource(Compositor compositor, DocumentSurfaces surfaces, ContainerVisual host, int canvasWidth, int canvasHeight, ILogger logger)
    {
        _compositor = compositor;
        _surfaces = surfaces;
        _logger = logger;
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
        _page = compositor.CreateSpriteVisual();
        _page.IsVisible = false;
        host.Children.InsertAtTop(_page);
        _settle = new Timer(_ => Nudge(), null, Timeout.Infinite, Timeout.Infinite);
        _thread = new Thread(Pump) { IsBackground = true, Name = "PowerPoint" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public int PageCount => _pageCount;

    public int PageIndex => _pageIndex;

    public int PageNumber => _pageIndex + 1;

    public string? Problem => _problem;

    /// <summary>What may look different at the event: fonts that are not embedded and not installed here, and linked media whose file is missing. Empty when all is well.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>The PowerPoint process that plays this show (0 when none): what the audio capture listens to. A PowerPoint the operator already runs is shared, so its other decks are heard too.</summary>
    public int ProcessId => _processId;

    /// <summary>True when the operator's own PowerPoint was already running and this source uses it without ever closing it.</summary>
    public bool Borrowed { get; private set; }

    public event Action? PageChanged;

    public event Action<int>? PageFailed;

    /// <summary>The picture was laid out again after a render size change; the engine restarts the stage capture because a still slide makes no new frames.</summary>
    internal event Action? Redrawn;

    /// <summary>Whether slide <paramref name="index"/> (zero-based) is hidden in the file. Hidden slides are skipped by Next and Previous.</summary>
    public bool IsHidden(int index)
    {
        bool[] hidden = _hidden;
        return index >= 0 && index < hidden.Length && hidden[index];
    }

    internal static void CleanOldTempFiles()
    {
        if (_tempCleaned) return;
        _tempCleaned = true;
        try
        {
            DateTime limit = DateTime.UtcNow.AddHours(-12);
            foreach (string entry in Directory.EnumerateFileSystemEntries(Path.GetTempPath(), TempPrefix + "*"))
            {
                if (Directory.Exists(entry) && Directory.GetLastWriteTimeUtc(entry) < limit) Directory.Delete(entry, true);
                else if (File.Exists(entry) && File.GetLastWriteTimeUtc(entry) < limit) File.Delete(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- the PowerPoint thread ----

    private void Pump()
    {
        foreach (Action action in _work.GetConsumingEnumerable()) action();
    }

    private Task<T> Run<T>(Func<T> job)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _work.Add(() =>
            {
                try { done.SetResult(job()); }
                catch (Exception ex) { done.SetException(ex); }
            });
        }
        catch (InvalidOperationException) // the queue is closed: the source was disposed
        {
            done.SetException(new ObjectDisposedException(nameof(PowerPointSource)));
        }
        return done.Task;
    }

    // PowerPoint answers "busy" for a moment while it animates or changes slide.
    private static T Retry<T>(Func<T> call)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return call(); }
            catch (COMException ex) when ((uint)ex.HResult is RetryLater or CallRejected && attempt < 100) { Thread.Sleep(50); }
        }
    }

    private static void Retry(Action call) => Retry(() => { call(); return 0; });

    // ---- opening ----

    /// <summary>
    /// Opens <paramref name="path"/> and starts its show at slide <paramref name="startSlide"/> (zero-based; -1, the default, is the first slide that
    /// is not hidden). A file that cannot be opened changes nothing: a deck that is already showing
    /// keeps showing, so a failed reload does not blank a tab that is on air.
    /// </summary>
    public async Task<PowerPointOpenResult> OpenAsync(string path, int startSlide = -1)
    {
        if (_disposed) return PowerPointOpenResult.Failed;
        string extension = Path.GetExtension(path);
        if (!string.Equals(extension, ".pptx", StringComparison.OrdinalIgnoreCase))
            return Fail(".pptm, .ppsx dan jenis lain belum didukung. Simpan sebagai .pptx.");

        PowerPointOpenResult result;
        try
        {
            result = await Run(() => OpenCore(path, startSlide));
        }
        catch (ObjectDisposedException)
        {
            return PowerPointOpenResult.Failed;
        }
        if (result != PowerPointOpenResult.Opened) return result;

        _logger.LogInformation("PowerPoint opened: {Slides} slides, {Borrowed}", _pageCount, Borrowed ? "borrowed instance" : "own instance");
        PageChanged?.Invoke();
        return result;
    }

    private PowerPointOpenResult Fail(string problem, PowerPointOpenResult result = PowerPointOpenResult.Failed)
    {
        _problem = problem;
        return result;
    }

    // On the PowerPoint thread.
    private PowerPointOpenResult OpenCore(string path, int startSlide)
    {
        string temp = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N") + ".pptx");
        try
        {
            File.Copy(path, temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not copy PowerPoint file {Path}", path);
            return Fail(ex is FileNotFoundException or DirectoryNotFoundException ? "The file was not found." : "The file could not be read.");
        }

        dynamic? app = null;
        dynamic? presentation = null;
        bool newApp = false;
        try
        {
            if (_app is null)
            {
                Type? type = Type.GetTypeFromProgID("PowerPoint.Application");
                if (type is null) return Fail("PowerPoint tidak terpasang di PC ini.", PowerPointOpenResult.NotInstalled);
                var before = Process.GetProcessesByName("POWERPNT").Select(p => p.Id).ToHashSet();
                app = Activator.CreateInstance(type);
                newApp = true;
                var started = Process.GetProcessesByName("POWERPNT").Select(p => p.Id).Where(id => !before.Contains(id)).ToList();
                lock (StartedGate)
                {
                    // The PowerPoint a deck started is shared with the decks opened after it: whichever closes last ends it.
                    if (before.Count == 0 && started.Count > 0) s_startedPid = started[0];
                    else if (s_startedPid != 0 && !before.Contains(s_startedPid)) s_startedPid = 0;
                    _ownApp = s_startedPid != 0;
                    _ownPid = s_startedPid;
                }

                Borrowed = !_ownApp;
                Retry(() => app!.AutomationSecurity = 3); // msoAutomationSecurityForceDisable: no macros run
            }
            else
            {
                app = _app;
            }

            presentation = Retry(() => app!.Presentations.Open(temp, -1, 0, 0)); // read only, untitled, no window
            int count = Retry(() => (int)presentation!.Slides.Count);
            if (count < 1) throw new InvalidOperationException("The presentation has no slides.");
            double width = Retry(() => (double)presentation!.PageSetup.SlideWidth);
            double height = Retry(() => (double)presentation!.PageSetup.SlideHeight);
            var hidden = new bool[count];
            for (int i = 0; i < count; i++)
            {
                int index = i + 1;
                hidden[i] = Retry(() => (int)presentation!.Slides[index].SlideShowTransition.Hidden) != 0;
            }

            if (hidden.All(h => h)) throw new InvalidOperationException("Every slide is hidden.");
            IReadOnlyList<string> warnings = CheckDeck(presentation, count);

            // The new file is good: the show that was running gives way to it.
            CloseShow();
            _app = app;
            _presentation = presentation;
            _tempFile = temp;
            _hidden = hidden;
            _pageCount = count;
            _problem = null;
            _warnings = warnings;
            lock (_frameGate)
            {
                _slideWidth = width;
                _slideHeight = height;
            }

            StartShow(startSlide < 0 ? -1 : Math.Min(startSlide, count - 1));
            return PowerPointOpenResult.Opened;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            _logger.LogWarning(ex, "Could not open PowerPoint file {Path}", path);
            try { presentation?.Close(); } catch (COMException) { }
            if (newApp && _presentation is null) TryQuit(app, _ownPid);
            if (newApp && _presentation is null) { _app = null; _ownApp = false; Borrowed = false; }
            try { File.Delete(temp); } catch (IOException) { }
            return Fail(ex is COMException ? "Berkas ini tidak bisa dibuka PowerPoint (rusak, terlindungi, atau bukan .pptx)." : ex.Message);
        }
    }

    // On the PowerPoint thread: looks for fonts and linked files the event PC may lack. A failed look is no warning.
    private IReadOnlyList<string> CheckDeck(dynamic presentation, int count)
    {
        var warnings = new List<string>();
        try
        {
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Windows.Media.FontFamily family in System.Windows.Media.Fonts.SystemFontFamilies)
                foreach (string name in family.FamilyNames.Values) installed.Add(name);
            // faces such as "Calibri Light" belong to a family of another name: Windows lists them by face
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (key is null) continue;
                foreach (string entry in key.GetValueNames())
                {
                    int paren = entry.LastIndexOf(" (", StringComparison.Ordinal);
                    installed.Add(paren > 0 ? entry[..paren] : entry);
                }
            }

            var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            int fonts = Retry(() => (int)presentation.Fonts.Count);
            for (int i = 1; i <= fonts; i++)
            {
                int index = i;
                dynamic font = Retry(() => presentation.Fonts[index]);
                string name = Retry(() => (string)font.Name);
                if (Retry(() => (int)font.Embedded) == 0 && !installed.Contains(name)) missing.Add(name);
            }

            if (missing.Count > 0) warnings.Add("Font tidak terpasang dan tidak disertakan di berkas: " + string.Join(", ", missing));

            var lost = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i <= count; i++)
            {
                int slide = i;
                int shapes = Retry(() => (int)presentation.Slides[slide].Shapes.Count);
                for (int j = 1; j <= shapes; j++)
                {
                    int shape = j;
                    int type = Retry(() => (int)presentation.Slides[slide].Shapes[shape].Type);
                    if (type is not (10 or 11 or 16)) continue; // linked OLE object, linked picture, media
                    try
                    {
                        string source = (string)presentation.Slides[slide].Shapes[shape].LinkFormat.SourceFullName;
                        if (!string.IsNullOrEmpty(source) && !File.Exists(source)) lost.Add(Path.GetFileName(source));
                    }
                    catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                    {
                        // an embedded one has no link
                    }
                }
            }

            if (lost.Count > 0) warnings.Add("File tertaut tidak ditemukan: " + string.Join(", ", lost));
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            _logger.LogWarning(ex, "Could not check the fonts and links of the deck");
        }

        foreach (string warning in warnings) _logger.LogWarning("PowerPoint deck: {Warning}", warning);
        return warnings;
    }

    // On the PowerPoint thread: runs the show of the open presentation off screen and starts capturing it.
    private void StartShow(int startSlide)
    {
        dynamic presentation = _presentation!;
        string marker = Path.GetFileNameWithoutExtension(_tempFile!);
        dynamic settings = Retry(() => presentation.SlideShowSettings);
        Retry(() => settings.ShowType = 1);          // full screen: a clean picture, unlike the framed window
        Retry(() => settings.ShowPresenterView = 0);
        Retry(() => settings.AdvanceMode = 1);       // slides move only on command
        Retry(() => settings.ShowWithAnimation = -1);
        Retry(() => settings.LoopUntilStopped = 0);
        _window = Retry(() => settings.Run());

        IntPtr hwnd = IntPtr.Zero;
        for (int i = 0; i < 100 && hwnd == IntPtr.Zero; i++)
        {
            hwnd = FindShowWindow(marker);
            if (hwnd == IntPtr.Zero) Thread.Sleep(50);
        }

        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("The slide show window was not found.");
        _hwnd = hwnd;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        _processId = (int)pid;
        MoveOffScreen(hwnd);
        if (startSlide >= 0 && startSlide != FirstVisible)
        {
            int target = startSlide + 1;
            Retry(() => _window!.View.GotoSlide(target));
        }

        _pageIndex = Math.Max(Position() - 1, 0);

        lock (_frameGate)
        {
            _capturer = new WindowCapturer(hwnd, _surfaces.WinRtDevice, OnFrame);
        }
    }

    // On the PowerPoint thread. Shows the slide show window nowhere: a window moved off every screen is still drawn and captured.
    private void MoveOffScreen(IntPtr hwnd)
    {
        NativeMethods.GetWindowRect(hwnd, out var rect);
        int width = rect.Right - rect.Left;
        int left = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen);
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, left - width - 400, rect.Top, 0, 0, NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        _logger.LogInformation("PowerPoint show window {Width}x{Height} moved off screen", width, rect.Bottom - rect.Top);
    }

    private static IntPtr FindShowWindow(string marker)
    {
        var pids = Process.GetProcessesByName("POWERPNT").Select(p => (uint)p.Id).ToHashSet();
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((handle, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(handle, out uint pid);
            if (!pids.Contains(pid) || !NativeMethods.IsWindowVisible(handle)) return true;
            var title = new System.Text.StringBuilder(512);
            NativeMethods.GetWindowText(handle, title, title.Capacity);
            string text = title.ToString();
            if (text.StartsWith("PowerPoint Slide Show", StringComparison.Ordinal) && text.Contains(marker, StringComparison.Ordinal)) found = handle;
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ---- the picture ----

    // On a capture thread. The frame is the whole show window; the slide is the largest 4:3 (or whatever the file is) rectangle that fits in it.
    private void OnFrame(ID3D11Texture2D texture, int width, int height)
    {
        lock (_frameGate)
        {
            if (_disposed || width <= 0 || height <= 0 || !(_slideWidth > 0)) return;
            try
            {
                FitLayout crop = FitComposer.Fit(_slideWidth, _slideHeight, width, height);
                if (_surface is null || _surfaceWidth != crop.Width || _surfaceHeight != crop.Height)
                {
                    _surface?.Dispose();
                    _surface = _surfaces.CreateSurface(crop.Width, crop.Height);
                    _surfaceWidth = crop.Width;
                    _surfaceHeight = crop.Height;
                    CompositionSurfaceBrush brush = _compositor.CreateSurfaceBrush(_surface);
                    brush.Stretch = CompositionStretch.Fill;
                    _page.Brush = brush;
                }

                _surfaces.CopyFrom(_surface, texture, crop.X, crop.Y, crop.Width, crop.Height);
                Place();
                _page.IsVisible = true;
                _settle?.Change(SettleMilliseconds, Timeout.Infinite);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
            {
                _logger.LogWarning(ex, "Could not draw a PowerPoint frame");
            }
        }
    }

    // Touches the picture without changing a pixel (opacity 1 and 0.9999 round to the same colour) so the compositor makes a new frame.
    private void Nudge()
    {
        lock (_frameGate)
        {
            if (_disposed || _surface is null) return;
            _nudged = !_nudged;
            _page.Opacity = _nudged ? 0.9999f : 1f;
        }
    }

    // Under _frameGate. The slide fills the canvas as far as its shape allows; the rest is the black of the tab.
    private void Place()
    {
        FitLayout layout = FitComposer.Fit(_slideWidth, _slideHeight, _canvasWidth, _canvasHeight);
        _page.Size = new Vector2(layout.Width, layout.Height);
        _page.Offset = new Vector3(layout.X, layout.Y, 0);
    }

    internal void Resize(int canvasWidth, int canvasHeight)
    {
        lock (_frameGate)
        {
            if (_disposed) return;
            _canvasWidth = canvasWidth;
            _canvasHeight = canvasHeight;
            if (_slideWidth > 0 && _surface is not null) Place();
        }

        if (_pageCount > 0) Redrawn?.Invoke();
    }

    // ---- moving ----

    // On the PowerPoint thread.
    private int Position() => Retry(() => (int)_window!.View.CurrentShowPosition);

    private int LastVisible()
    {
        for (int i = _hidden.Length - 1; i >= 0; i--) if (!_hidden[i]) return i + 1;
        return _hidden.Length;
    }

    private bool IsOpen => _window is not null && _pageCount > 0;

    public async Task<bool> NextAsync() => await Move("Next", () =>
    {
        // Past the last slide PowerPoint shows "end of show" and the next step closes the show: stay on the last slide.
        int before = Position();
        if (before >= LastVisible() && !MoreClicks()) return false;
        Retry(() => _window!.View.Next());
        if (Position() > _hidden.Length)
        {
            Retry(() => _window!.View.Previous());
            return false;
        }

        return true;
    });

    public async Task<bool> PreviousAsync() => await Move("Previous", () =>
    {
        Retry(() => _window!.View.Previous());
        return true;
    });

    // Exact: a hidden slide can be asked for by number. "The first" and "the last" the operator wants are FirstVisible and LastVisibleIndex.
    public async Task<bool> GoToAsync(int index) => await Move("GoTo", () =>
    {
        int slide = Math.Clamp(index, 0, _hidden.Length - 1) + 1;
        Retry(() => _window!.View.GotoSlide(slide));
        return true;
    });

    /// <summary>Zero-based index of the first slide that is not hidden: where a show starts and "start over" goes.</summary>
    public int FirstVisible
    {
        get
        {
            bool[] hidden = _hidden;
            for (int i = 0; i < hidden.Length; i++) if (!hidden[i]) return i;
            return 0;
        }
    }

    /// <summary>Zero-based index of the last slide that is not hidden: where "the end" goes.</summary>
    public int LastVisibleIndex => LastVisible() - 1;

    // Whether the slide on screen still has animation steps that Next plays before it leaves the slide.
    private bool MoreClicks()
    {
        try
        {
            int count = Retry(() => (int)_window!.View.GetClickCount());
            int index = Retry(() => (int)_window!.View.GetClickIndex());
            return index < count;
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return false;
        }
    }

    private async Task<bool> Move(string what, Func<bool> job)
    {
        if (_disposed) return false;
        int before = _pageIndex;
        try
        {
            bool moved = await Run(() =>
            {
                if (!IsOpen) return false;
                bool ok = job();
                _pageIndex = Math.Clamp(Position() - 1, 0, _hidden.Length - 1);
                return ok;
            });
            if (_pageIndex != before) PageChanged?.Invoke();
            return moved || _pageIndex != before;
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            _logger.LogWarning(ex, "PowerPoint did not answer to {What}", what);
            _problem = "PowerPoint tidak menjawab. Klik Muat ulang untuk membuka berkas lagi.";
            PageFailed?.Invoke(before + 1);
            return false;
        }
    }

    // ---- thumbnails ----

    public async Task<PageThumbnail?> RenderThumbnailAsync(int index, int maxWidth, int maxHeight)
    {
        if (_disposed || index < 0 || index >= _pageCount) return null;
        string? file;
        try
        {
            file = await Run(() =>
            {
                if (_presentation is null) return null;
                FitLayout size = FitComposer.Fit(_slideWidth, _slideHeight, maxWidth, maxHeight);
                _thumbDir ??= Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), TempPrefix + "thumbs-" + Guid.NewGuid().ToString("N"))).FullName;
                string target = Path.Combine(_thumbDir, $"{index + 1}-{Guid.NewGuid():N}.png"); // a name of its own: two requests for one slide must not meet in one file
                int slide = index + 1;
                Retry(() => _presentation!.Slides[slide].Export(target, "PNG", size.Width, size.Height));
                return target;
            });
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            _logger.LogWarning(ex, "Could not export a thumbnail of slide {Slide}", index + 1);
            return null;
        }

        if (file is null || !File.Exists(file)) return null;
        try
        {
            using var stream = File.OpenRead(file);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
            PixelDataProvider data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            return new PageThumbnail(data.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
        }
        catch (Exception ex) when (ex is IOException or COMException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not read the thumbnail of slide {Slide}", index + 1);
            return null;
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---- closing ----

    // On the PowerPoint thread: ends the show and closes this source's presentation only; the program stays unless it is ours and idle.
    private void CloseShow()
    {
        lock (_frameGate)
        {
            _capturer?.Dispose();
            _capturer = null;
            _page.IsVisible = false;
        }

        try { if (_window is not null) Retry(() => _window.View.Exit()); } catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        try { _presentation?.Close(); } catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        _window = null;
        _presentation = null;
        _hwnd = IntPtr.Zero;
        _processId = 0;
        _pageCount = 0;
        _pageIndex = -1;
        if (_tempFile is not null)
        {
            try { File.Delete(_tempFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _tempFile = null;
        }
    }

    private void TryQuit(dynamic? app, int ownPid)
    {
        if (app is null || !_ownApp) return;
        bool quitting = false;
        try
        {
            // Another deck of ours may still be open in it: then it stays, and the last one to close ends it.
            if ((int)app.Presentations.Count == 0)
            {
                app.Quit();
                quitting = true;
            }
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }

        // PowerPoint is slow to leave: wait, and only for an instance our decks started, end it.
        if (!quitting || ownPid == 0) return;
        for (int i = 0; i < 40; i++)
        {
            if (!ProcessRunning(ownPid)) return;
            Thread.Sleep(100);
        }

        try { Process.GetProcessById(ownPid).Kill(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static bool ProcessRunning(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_frameGate) _disposed = true;
        _settle?.Dispose();
        _settle = null;
        Task closing = Run(() =>
        {
            CloseShow();
            TryQuit(_app, _ownPid);
            _app = null;
            if (_thumbDir is not null)
            {
                try { Directory.Delete(_thumbDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                _thumbDir = null;
            }

            return 0;
        });
        _work.CompleteAdding();
        try { closing.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
        lock (_frameGate)
        {
            _surface?.Dispose();
            _surface = null;
        }

        _page.Dispose();
    }

    private static class NativeMethods
    {
        public const int SmXVirtualScreen = 76;
        public const uint SwpNoSize = 0x0001;
        public const uint SwpNoZOrder = 0x0004;
        public const uint SwpNoActivate = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        public delegate bool EnumProc(IntPtr handle, IntPtr parameter);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr parameter);

        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr handle, System.Text.StringBuilder text, int max);

        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle, out Rect rect);

        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    }
}
