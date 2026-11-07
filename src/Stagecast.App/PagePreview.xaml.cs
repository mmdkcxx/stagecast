using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Stagecast.Core.Metrics;
using Stagecast.Core.Display;
using Stagecast.Core.Output;
using Stagecast.Core.Rendering;
using Stagecast.Rendering;
using Stagecast.Rendering.Capture;

namespace Stagecast.App;

/// <summary>How the operator's preview kept up: pictures shown, pictures replaced before they could be shown, and read-to-shown delay.</summary>
public sealed record PreviewStats(double ShownPerSecond, double SkippedPerSecond, TimingSnapshot Lag);

/// <summary>
/// The operator's view of the page: the rendered page fitted or at 100% (draggable, with a mini-map), and, when Interaksi is on,
/// the mouse and keyboard of the operator forwarded to the page. It only changes what the operator sees; the output is untouched.
/// </summary>
public partial class PagePreview : UserControl
{
    private const double MiniMapWidth = 168;

    private readonly PreviewView _view = new();
    private RenderEngine? _engine;
    private Resolution _resolution = new(2520, 1080);
    private bool _interactive;
    private bool _spaceDown;
    private bool _panning;
    private Point _panLast;
    private sealed record Shot(PreviewImage Image, BitmapSource Bitmap);

    private Shot? _pendingShot;
    private int _imageScheduled;
    private (int X, int Y, int Mip, int Width, int Height) _shown;
    private readonly TimingStats _lag = new();
    private long _shownCount, _skippedCount, _statsStart = System.Diagnostics.Stopwatch.GetTimestamp();

    public PagePreview() => InitializeComponent();

    /// <summary>Raised when the view mode or position changed, so the toolbar can follow.</summary>
    public event Action? ViewChanged;

    public PreviewView View => _view;

    public bool Interactive
    {
        get => _interactive;
        set
        {
            _interactive = value;
            UpdateOverlays();
            UpdateCursor();
        }
    }

    /// <summary>The region of the page the last shown picture covers; the self test uses it.</summary>
    internal (int X, int Y, int Mip, int Width, int Height) ShownPicture => _shown;

    private bool _previewMode;

    /// <summary>In Preview + Take this shows the preview tab (green frame, PROGRAM box); otherwise it shows what is live (LIVE).</summary>
    public bool PreviewMode
    {
        get => _previewMode;
        set
        {
            _previewMode = value;
            ModePill.Background = (Brush)FindResource(value ? "Ok" : "Live");
            ModeText.Text = value ? "PREVIEW" : "LIVE";
            ModeText.Foreground = value ? new SolidColorBrush(Color.FromRgb(0x0C, 0x0B, 0x09)) : Brushes.White;
            Gate = _gate; // redraws the frame and the box for the new mode
            if (!value) ProgramImage.Source = null;
        }
    }

    private GateMode _gate = GateMode.Live;

    /// <summary>Freeze and Blackout: the page here keeps running, only the output is held. Yellow frame, banner, and the output box show it.</summary>
    public GateMode Gate
    {
        get => _gate;
        set
        {
            _gate = value;
            bool held = value != GateMode.Live;
            var live = (Brush)FindResource("Live");
            var warn = (Brush)FindResource("Warn");
            ModeFrame.BorderBrush = held ? warn : _previewMode ? (Brush)FindResource("Ok") : Brushes.Transparent;
            GateBanner.Visibility = held ? Visibility.Visible : Visibility.Collapsed;
            GateText.Text = value == GateMode.Freeze
                ? "OUTPUT BEKU · halaman tetap berjalan, penonton melihat gambar terakhir"
                : "OUTPUT HITAM · halaman tetap berjalan, penonton melihat layar hitam";
            ProgramBox.Visibility = held || _previewMode ? Visibility.Visible : Visibility.Collapsed;
            ProgramBox.BorderBrush = held ? warn : live;
            ProgramTitle.Background = held ? warn : live;
            ProgramTitle.Foreground = held ? new SolidColorBrush(Color.FromRgb(0x0C, 0x0B, 0x09)) : Brushes.White;
            ProgramTitle.Text = value == GateMode.Freeze ? "KELUARAN (BEKU)" : value == GateMode.Blackout ? "KELUARAN (HITAM)" : _previewMode ? "PROGRAM (TAYANG)" : "KELUARAN";
            if (value == GateMode.Blackout) ProgramImage.Source = null;
        }
    }

    /// <summary>True while 100% shows only part of the page, so Space is used to drag.</summary>
    public bool CanPanNow => CanPan;

    private void OnProgramThumb(PreviewImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        if (_gate != GateMode.Live) return; // frozen: the box keeps the picture the audience has; black: it stays black
        Dispatcher.BeginInvoke(() => ProgramImage.Source = _previewMode && _gate == GateMode.Live ? bitmap : ProgramImage.Source, System.Windows.Threading.DispatcherPriority.Background);
    }

    public void Attach(RenderEngine engine)
    {
        _engine = engine;
        _resolution = engine.Resolution;
        engine.PreviewReady += OnPreviewReady;
        engine.ProgramThumbReady += OnProgramThumb;
        PreviewMode = false;
        engine.CursorChanged += () => Dispatcher.BeginInvoke(UpdateCursor);
        engine.PageFocusChanged += _ => Dispatcher.BeginInvoke(UpdateOverlays);
        engine.EscapePressed += ReleaseKeyboard;
        Refresh();
    }

    public PreviewStats Stats()
    {
        double seconds = Math.Max(0.001, System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _statsStart)).TotalSeconds);
        return new PreviewStats(Interlocked.Read(ref _shownCount) / seconds, Interlocked.Read(ref _skippedCount) / seconds, _lag.Snapshot());
    }

    public void ResetStats()
    {
        Interlocked.Exchange(ref _shownCount, 0);
        Interlocked.Exchange(ref _skippedCount, 0);
        _lag.Reset();
        Interlocked.Exchange(ref _statsStart, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    public void SetResolution(Resolution resolution)
    {
        _resolution = resolution;
        if (_view.Mode == ViewMode.Actual) _view.ShowActualAt(resolution, PhysicalWidth, PhysicalHeight, _view.CenterX, _view.CenterY);
        Refresh();
    }

    public void ShowFit()
    {
        _view.ShowFit();
        Refresh();
        ViewChanged?.Invoke();
    }

    public void ShowActual()
    {
        _view.ShowActual(_resolution, PhysicalWidth, PhysicalHeight);
        Refresh();
        ViewChanged?.Invoke();
    }

    /// <summary>Esc: the page stops receiving keystrokes; the mouse keeps working until Interaksi is switched off.</summary>
    public void ReleaseKeyboard()
    {
        Focus();
        Keyboard.Focus(this);
        if (Window.GetWindow(this) is { } window) SetFocus(new System.Windows.Interop.WindowInteropHelper(window).Handle);
        UpdateOverlays();
    }

    // ---- geometry ----

    private double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private double PhysicalWidth => Math.Max(1, Surface.ActualWidth * Dpi);

    private double PhysicalHeight => Math.Max(1, Surface.ActualHeight * Dpi);

    private ViewTransform Transform => _view.Transform(_resolution, PhysicalWidth, PhysicalHeight);

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_view.Mode == ViewMode.Actual) _view.ShowActualAt(_resolution, PhysicalWidth, PhysicalHeight, _view.CenterX, _view.CenterY);
        Refresh();
    }

    /// <summary>Tells the engine what to read back for the new view, and redraws what is already here.</summary>
    private void Refresh()
    {
        if (Surface.ActualWidth <= 0 || Surface.ActualHeight <= 0) return;
        if (_engine is not null) _engine.PreviewTarget = _view.Region(_resolution, PhysicalWidth, PhysicalHeight);
        PlacePicture();
        UpdateOverlays();
    }

    private void PlacePicture()
    {
        if (Picture.Source is null) return;
        var t = Transform;
        double dpi = Dpi, span = 1 << _shown.Mip;
        var (left, top) = t.ToControl(_shown.X, _shown.Y);
        Canvas.SetLeft(Picture, left / dpi);
        Canvas.SetTop(Picture, top / dpi);
        Picture.Width = _shown.Width * span * t.Scale / dpi;
        Picture.Height = _shown.Height * span * t.Scale / dpi;
    }

    private void UpdateOverlays()
    {
        bool cropped = _view.IsCropped(_resolution, PhysicalWidth, PhysicalHeight);

        Badge.Visibility = _interactive ? Visibility.Visible : Visibility.Collapsed;
        BadgeText.Text = _interactive ? (_engine?.PageHasFocus == true ? "INTERAKSI · PREVIEW · KEYBOARD KE HALAMAN" : "INTERAKSI · PREVIEW") : "";

        HintText.Visibility = cropped ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = _interactive ? "100%  ·  geser: tahan Space lalu seret (Esc dulu bila keyboard ada di halaman)" : "100%  ·  geser: seret, atau tahan Space lalu seret";

        MiniMap.Visibility = cropped ? Visibility.Visible : Visibility.Collapsed;
        if (!cropped) return;
        double height = Math.Max(24, MiniMapWidth * _resolution.Height / _resolution.Width);
        MiniMap.Width = MiniMapWidth + 2;
        MiniMap.Height = height + 2;
        MiniCanvas.Width = MiniMapWidth;
        MiniCanvas.Height = height;
        var (x, y, w, h) = _view.Visible(_resolution, PhysicalWidth, PhysicalHeight);
        double k = MiniMapWidth / _resolution.Width;
        Canvas.SetLeft(MiniView, x * k);
        Canvas.SetTop(MiniView, y * k);
        MiniView.Width = Math.Max(2, w * k);
        MiniView.Height = Math.Max(2, h * k);
    }

    // ---- picture from the engine ----

    // Raised on a capture thread. Building the bitmap here keeps the UI thread free; only the newest picture is ever shown.
    private void OnPreviewReady(PreviewImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        // A picture still waiting when the next one arrives was never shown: the window is not keeping up.
        if (Interlocked.Exchange(ref _pendingShot, new Shot(image, bitmap)) is not null) Interlocked.Increment(ref _skippedCount);
        if (Interlocked.Exchange(ref _imageScheduled, 1) == 0) Dispatcher.BeginInvoke(ShowPending);
    }

    private void ShowPending()
    {
        Interlocked.Exchange(ref _imageScheduled, 0);
        var shot = Interlocked.Exchange(ref _pendingShot, null);
        if (shot is null) return;
        var image = shot.Image;
        Picture.Source = shot.Bitmap;
        Interlocked.Increment(ref _shownCount);
        if (image.CapturedAt != 0) _lag.Add(System.Diagnostics.Stopwatch.GetElapsedTime(image.CapturedAt).TotalMilliseconds);
        _shown = (image.SourceX, image.SourceY, image.Mip, image.Width, image.Height);
        PlacePicture();
    }

    // ---- pointer ----

    private Point Physical(MouseEventArgs e)
    {
        var p = e.GetPosition(Surface);
        double dpi = Dpi;
        return new Point(p.X * dpi, p.Y * dpi);
    }

    private bool CanPan => _view.IsCropped(_resolution, PhysicalWidth, PhysicalHeight);

    private bool PageOwnsKeyboard => _engine?.PageHasFocus == true;

    private void SurfaceMouseEnter(object sender, MouseEventArgs e)
    {
        if (!PageOwnsKeyboard) Focus();
        UpdateCursor();
    }

    private void SurfaceMouseLeave(object sender, MouseEventArgs e)
    {
        if (_interactive && !_panning) _engine?.SendMouse(CoreWebView2MouseEventKind.Leave, CoreWebView2MouseEventVirtualKeys.None, 0, new System.Drawing.Point(-1, -1));
    }

    private void SurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = Physical(e);
        if (CanPan && (_spaceDown || !_interactive) && e.ChangedButton == MouseButton.Left)
        {
            _panning = true;
            _panLast = p;
            Surface.CaptureMouse();
            UpdateCursor();
            e.Handled = true;
            return;
        }
        if (!_interactive || _engine is null) return;
        if (e.ChangedButton == MouseButton.Left) _engine.FocusPage();
        Forward(ButtonKind(e.ChangedButton, down: true), p, 0, e);
        Surface.CaptureMouse();
        e.Handled = true;
    }

    private void SurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        var p = Physical(e);
        if (_panning)
        {
            _panning = false;
            Surface.ReleaseMouseCapture();
            UpdateCursor();
            return;
        }
        if (!_interactive || _engine is null) return;
        Forward(ButtonKind(e.ChangedButton, down: false), p, 0, e);
        if (Mouse.LeftButton == MouseButtonState.Released && Mouse.RightButton == MouseButtonState.Released && Mouse.MiddleButton == MouseButtonState.Released)
            Surface.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void SurfaceMouseMove(object sender, MouseEventArgs e)
    {
        var p = Physical(e);
        if (_panning)
        {
            _view.PanBy(_resolution, PhysicalWidth, PhysicalHeight, p.X - _panLast.X, p.Y - _panLast.Y);
            _panLast = p;
            Refresh();
            ViewChanged?.Invoke();
            return;
        }
        if (_interactive && _engine is not null) Forward(CoreWebView2MouseEventKind.Move, p, 0, e);
    }

    private void SurfaceMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_interactive || _engine is null) return;
        Forward(CoreWebView2MouseEventKind.Wheel, Physical(e), unchecked((uint)e.Delta), e);
        e.Handled = true;
    }

    private static CoreWebView2MouseEventKind ButtonKind(MouseButton button, bool down) => (button, down) switch
    {
        (MouseButton.Right, true) => CoreWebView2MouseEventKind.RightButtonDown,
        (MouseButton.Right, false) => CoreWebView2MouseEventKind.RightButtonUp,
        (MouseButton.Middle, true) => CoreWebView2MouseEventKind.MiddleButtonDown,
        (MouseButton.Middle, false) => CoreWebView2MouseEventKind.MiddleButtonUp,
        (_, true) => CoreWebView2MouseEventKind.LeftButtonDown,
        _ => CoreWebView2MouseEventKind.LeftButtonUp,
    };

    private void Forward(CoreWebView2MouseEventKind kind, Point physical, uint data, MouseEventArgs e)
    {
        var t = Transform;
        var (px, py) = t.ToPage(physical.X, physical.Y);
        // While a button is held the pointer may leave the page; keep sending, clamped, so drags and releases are not lost.
        bool held = Surface.IsMouseCaptured;
        if (!held && !t.Contains(_resolution, physical.X, physical.Y))
        {
            if (kind == CoreWebView2MouseEventKind.Move) _engine!.SendMouse(CoreWebView2MouseEventKind.Leave, CoreWebView2MouseEventVirtualKeys.None, 0, new System.Drawing.Point(-1, -1));
            return;
        }
        int x = (int)Math.Clamp(Math.Round(px), 0, _resolution.Width - 1), y = (int)Math.Clamp(Math.Round(py), 0, _resolution.Height - 1);
        var keys = CoreWebView2MouseEventVirtualKeys.None;
        if (e.LeftButton == MouseButtonState.Pressed || kind == CoreWebView2MouseEventKind.LeftButtonDown) keys |= CoreWebView2MouseEventVirtualKeys.LeftButton;
        if (e.RightButton == MouseButtonState.Pressed || kind == CoreWebView2MouseEventKind.RightButtonDown) keys |= CoreWebView2MouseEventVirtualKeys.RightButton;
        if (e.MiddleButton == MouseButtonState.Pressed || kind == CoreWebView2MouseEventKind.MiddleButtonDown) keys |= CoreWebView2MouseEventVirtualKeys.MiddleButton;
        if (kind == CoreWebView2MouseEventKind.LeftButtonUp) keys &= ~CoreWebView2MouseEventVirtualKeys.LeftButton;
        if (kind == CoreWebView2MouseEventKind.RightButtonUp) keys &= ~CoreWebView2MouseEventVirtualKeys.RightButton;
        if (kind == CoreWebView2MouseEventKind.MiddleButtonUp) keys &= ~CoreWebView2MouseEventVirtualKeys.MiddleButton;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) keys |= CoreWebView2MouseEventVirtualKeys.Shift;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) keys |= CoreWebView2MouseEventVirtualKeys.Control;
        _engine!.SendMouse(kind, keys, data, new System.Drawing.Point(x, y));
    }

    /// <summary>Sends a click at a point of the control (device-independent pixels) exactly as the mouse handlers would. Used by the self test.</summary>
    internal void InjectClick(Point controlDip)
    {
        var t = Transform;
        double dpi = Dpi;
        var (px, py) = t.ToPage(Math.Round(controlDip.X * dpi), Math.Round(controlDip.Y * dpi));
        var point = new System.Drawing.Point((int)Math.Round(px), (int)Math.Round(py));
        _engine!.SendMouse(CoreWebView2MouseEventKind.LeftButtonDown, CoreWebView2MouseEventVirtualKeys.LeftButton, 0, point);
        _engine.SendMouse(CoreWebView2MouseEventKind.LeftButtonUp, CoreWebView2MouseEventVirtualKeys.None, 0, point);
    }

    /// <summary>Where a page point is drawn, in device-independent pixels of this control.</summary>
    internal Point ControlPointOf(double pageX, double pageY)
    {
        var (x, y) = Transform.ToControl(pageX, pageY);
        return new Point(x / Dpi, y / Dpi);
    }

    internal bool PageContainsControlPoint(Point controlDip) => Transform.Contains(_resolution, controlDip.X * Dpi, controlDip.Y * Dpi);

    internal void PanByForTest(double dx, double dy)
    {
        _view.PanBy(_resolution, PhysicalWidth, PhysicalHeight, dx, dy);
        Refresh();
        ViewChanged?.Invoke();
    }

    internal void ShowActualAtForTest(double pageX, double pageY)
    {
        _view.ShowActualAt(_resolution, PhysicalWidth, PhysicalHeight, pageX, pageY);
        Refresh();
        ViewChanged?.Invoke();
    }

    internal Size SurfaceSize => new(Surface.ActualWidth, Surface.ActualHeight);

    // ---- keyboard ----

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Space && !PageOwnsKeyboard && !e.IsRepeat)
        {
            _spaceDown = true;
            UpdateCursor();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _interactive && !PageOwnsKeyboard)
        {
            e.Handled = true; // nothing to release; the page does not have the keyboard
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (e.Key != Key.Space) return;
        _spaceDown = false;
        UpdateCursor();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _spaceDown = false;
        UpdateCursor();
    }

    // ---- mini-map ----

    private void OnMiniMapMouse(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(MiniCanvas);
        double k = _resolution.Width / MiniMapWidth;
        _view.ShowActualAt(_resolution, PhysicalWidth, PhysicalHeight, p.X * k, p.Y * k);
        Refresh();
        ViewChanged?.Invoke();
        e.Handled = true;
    }

    // ---- pointer shape ----

    private void UpdateCursor()
    {
        if (_panning) Surface.Cursor = Cursors.SizeAll;
        else if (_spaceDown && CanPan) Surface.Cursor = Cursors.Hand;
        else if (_interactive && _engine is not null) Surface.Cursor = CursorFor(_engine.CursorId);
        else if (CanPan) Surface.Cursor = Cursors.Hand;
        else Surface.Cursor = Cursors.Arrow;
    }

    // Windows IDC_ ids reported by WebView2.
    private static Cursor CursorFor(uint id) => id switch
    {
        32513 => Cursors.IBeam,
        32514 => Cursors.Wait,
        32515 => Cursors.Cross,
        32516 => Cursors.UpArrow,
        32642 => Cursors.SizeNWSE,
        32643 => Cursors.SizeNESW,
        32644 => Cursors.SizeWE,
        32645 => Cursors.SizeNS,
        32646 => Cursors.SizeAll,
        32648 => Cursors.No,
        32649 => Cursors.Hand,
        32650 => Cursors.AppStarting,
        _ => Cursors.Arrow,
    };

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);
}
