using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Stagecast.Core.Display;
using Stagecast.Core.Frames;
using Stagecast.Core.Rendering;
using Frame = Stagecast.Core.Frames.Frame;

namespace Stagecast.App;

/// <summary>
/// The picture on a monitor: a borderless window that never takes keyboard focus. It is a frame sink; each new frame is copied into a
/// bitmap on the UI thread (only the newest one, older ones are dropped) and drawn at the size <see cref="ScaleMath"/> gives.
/// </summary>
internal sealed class OutputWindow : Window, IFrameSink
{
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000, WsExToolWindow = 0x80, WsExTransparent = 0x20;
    private static readonly IntPtr HwndTopmost = new(-1), HwndNoTopmost = new(-2);
    private const uint SwpNoActivate = 0x0010, SwpShowWindow = 0x0040;

    private readonly Image _image = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true };
    private MonitorInfo _monitor;
    private ScaleMode _mode;
    private bool _alwaysOnTop;
    private Resolution _render;
    private WriteableBitmap? _bitmap;
    private Frame? _pending;
    private int _scheduled;
    private long _shown;

    public OutputWindow(MonitorInfo monitor, ScaleMode mode, bool alwaysOnTop, Resolution render)
    {
        _monitor = monitor;
        _mode = mode;
        _alwaysOnTop = alwaysOnTop;
        _render = render;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = Brushes.Black;
        Topmost = alwaysOnTop;
        UseLayoutRounding = true;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(_image, EdgeMode.Aliased);
        Content = new Grid { Background = Brushes.Black, ClipToBounds = true, Children = { _image } };
        // The first position only has to be on the right monitor; the exact pixels are set with SetWindowPos once the handle exists.
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = monitor.X;
        Top = monitor.Y;
        Width = 200;
        Height = 200;
        DpiChanged += (_, _) => Place();
    }

    string IFrameSink.Name => "screen";

    public MonitorInfo Monitor => _monitor;

    /// <summary>Frames drawn so far (for the self test and the statistics).</summary>
    public long FramesShown => Interlocked.Read(ref _shown);

    public ScaleResult Scale => ScaleMath.Compute(CurrentSource, _monitor.Size, _mode);

    /// <summary>Where Windows really put the window, in physical pixels (for the self test).</summary>
    public (int X, int Y, int Width, int Height) PlacementInPixels()
    {
        GetWindowRect(new WindowInteropHelper(this).Handle, out NativeRect r);
        return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    private Resolution CurrentSource => _bitmap is null ? _render : new Resolution(_bitmap.PixelWidth, _bitmap.PixelHeight);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // No focus, no Alt+Tab entry, and the mouse goes through to whatever is below so a stray click cannot hit the output.
        long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | WsExNoActivate | WsExToolWindow | WsExTransparent));
        Place();
    }

    public void Configure(MonitorInfo monitor, ScaleMode mode, bool alwaysOnTop)
    {
        _monitor = monitor;
        _mode = mode;
        _alwaysOnTop = alwaysOnTop;
        Topmost = alwaysOnTop;
        Place();
    }

    public void SetRender(Resolution render)
    {
        _render = render;
        Place();
    }

    /// <summary>Covers the monitor exactly (physical pixels) and sizes the image for the scale mode.</summary>
    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, _alwaysOnTop ? HwndTopmost : HwndNoTopmost, _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height, SwpNoActivate | SwpShowWindow);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var scale = ScaleMath.Compute(CurrentSource, _monitor.Size, _mode);
        _image.Width = scale.DestWidth / dpi;
        _image.Height = scale.DestHeight / dpi;
    }

    public void Deliver(Frame frame, bool repeated)
    {
        if (repeated && _bitmap is not null) return; // nothing new to draw
        frame.Retain();
        Frame? old = Interlocked.Exchange(ref _pending, frame);
        old?.Release();
        if (Interlocked.Exchange(ref _scheduled, 1) == 0)
            Dispatcher.BeginInvoke(Draw, DispatcherPriority.Render);
    }

    private void Draw()
    {
        Interlocked.Exchange(ref _scheduled, 0);
        Frame? frame = Interlocked.Exchange(ref _pending, null);
        if (frame is null) return;
        try
        {
            if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
            {
                _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
                _image.Source = _bitmap;
                Place();
            }
            _bitmap.Lock();
            try
            {
                int rowBytes = frame.Width * 4;
                if (_bitmap.BackBufferStride == frame.Stride)
                    Marshal.Copy(frame.Pixels, 0, _bitmap.BackBuffer, frame.Stride * frame.Height);
                else
                    for (int y = 0; y < frame.Height; y++)
                        Marshal.Copy(frame.Pixels, y * frame.Stride, _bitmap.BackBuffer + y * _bitmap.BackBufferStride, rowBytes);
                _bitmap.AddDirtyRect(new Int32Rect(0, 0, frame.Width, frame.Height));
            }
            finally
            {
                _bitmap.Unlock();
            }
            Interlocked.Increment(ref _shown);
        }
        finally
        {
            frame.Release();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Interlocked.Exchange(ref _pending, null)?.Release();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
