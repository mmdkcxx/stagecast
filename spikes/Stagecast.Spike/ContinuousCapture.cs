using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Stagecast.Spike;

/// <summary>Result of one continuous-capture run.</summary>
internal sealed record CaptureSummary(
    double Seconds, int Arrived, int Delivered, double DeliveredFps, double ArrivedFps,
    double P99IntervalMs, double MaxIntervalMs, int Stutters, int Dropped, double CpuPercent);

/// <summary>
/// S5: keeps a capture session open on the composition visual and measures what arrives.
/// The target rate is enforced here by skipping frames, because WGC delivers at the compositor rate.
/// </summary>
internal sealed class ContinuousCapture : IDisposable
{
    private readonly Windows.UI.Composition.ContainerVisual _visual;
    private readonly object _gate = new();
    private readonly List<double> _intervalsMs = new();
    private readonly Action<byte[], int, int, int>? _onPreview;
    private readonly int _previewIntervalMs;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;

    private int _targetFps;
    private int _arrived, _delivered, _stutters;
    private TimeSpan _lastDelivered = TimeSpan.MinValue;
    private TimeSpan _nextDue;
    private DateTime _started;
    private TimeSpan _cpuStart;
    private long _lastPreviewTicks;
    private int _secondArrived, _secondDelivered;
    private Timer? _secondTimer;

    public ContinuousCapture(Windows.UI.Composition.ContainerVisual visual, int targetFps, Action<byte[], int, int, int>? onPreview, int previewIntervalMs = 250)
    {
        _previewIntervalMs = previewIntervalMs;
        _visual = visual;
        _targetFps = targetFps;
        _onPreview = onPreview;
    }

    public void Start()
    {
        var item = GraphicsCaptureItem.CreateFromVisual(_visual);
        var (device, context, winrtDevice) = FrameCapture.CreateDevice();
        _device = device;
        _context = context;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, item.Size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(item);
        // Without this WGC paces itself to about 48 fps here; ask for every compositor frame and pace in OnFrameArrived.
        _session.MinUpdateInterval = TimeSpan.FromMilliseconds(MinUpdateMs);
        _started = DateTime.UtcNow;
        _cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        _session.StartCapture();
        _secondTimer = new Timer(_ => LogSecond(), null, 1000, 1000);
        SpikeLog.Write($"continuous capture started, target {_targetFps} fps");
    }

    /// <summary>Switches the target rate while running; statistics restart.</summary>
    public void SetTargetFps(int fps)
    {
        lock (_gate)
        {
            _targetFps = fps;
            ResetLocked();
        }
        SpikeLog.Write($"target fps -> {fps}");
    }

    private void ResetLocked()
    {
        _arrived = _delivered = _stutters = 0;
        _intervalsMs.Clear();
        _lastDelivered = TimeSpan.MinValue;
        _started = DateTime.UtcNow;
        _cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame is null) return;

        bool deliver;
        lock (_gate)
        {
            _arrived++;
            _secondArrived++;
            // Fixed schedule: take the frame nearest to each ideal time. Compositor frames come every ~7 ms (144 Hz),
            // so 60 fps alternates 14 and 21 ms gaps but averages exactly 60.
            double interval = 1000.0 / _targetFps;
            var t = frame.SystemRelativeTime;
            deliver = _lastDelivered == TimeSpan.MinValue || (t - _nextDue).TotalMilliseconds >= -3.5;
            if (deliver)
            {
                if (_lastDelivered != TimeSpan.MinValue)
                {
                    double ms = (t - _lastDelivered).TotalMilliseconds;
                    _intervalsMs.Add(ms);
                    if (ms > interval * 1.5) _stutters++;
                    _nextDue += TimeSpan.FromMilliseconds(interval);
                    if ((t - _nextDue).TotalMilliseconds > interval) _nextDue = t + TimeSpan.FromMilliseconds(interval); // fell behind: resync
                }
                else
                {
                    _nextDue = t + TimeSpan.FromMilliseconds(interval);
                }
                _lastDelivered = t;
                _delivered++;
                _secondDelivered++;
            }
        }

        if (!deliver) return;

        // Program role: every delivered frame is copied to the CPU at full size (this is what NDI/OMT/screen output will need).
        // Preview role: a few times per second, optionally shrunk on the GPU first so the CPU copy is small.
        long now = Stopwatch.GetTimestamp();
        if (_programEnabled && OnProgramFrame is not null)
        {
            ReadBack(frame, 0, OnProgramFrame);
        }
        else if (PreviewEnabled && _onPreview is not null && Stopwatch.GetElapsedTime(_lastPreviewTicks, now).TotalMilliseconds >= _previewIntervalMs)
        {
            _lastPreviewTicks = now;
            ReadBack(frame, PreviewMipLevel, _onPreview);
        }
    }

    /// <summary>When true, every delivered frame is read back at full size and handed to <see cref="OnProgramFrame"/>.</summary>
    private volatile bool _programEnabled;
    public bool ProgramEnabled { get => _programEnabled; set => _programEnabled = value; }
    public Action<byte[], int, int, int>? OnProgramFrame { get; set; }
    public volatile bool PreviewEnabled = true;

    /// <summary>0 reads the preview at full size; 2 shrinks it to a quarter of each side with GPU mipmaps before the CPU copy.</summary>
    public int PreviewMipLevel { get; set; }

    /// <summary>Shortest time between frames asked of Windows Graphics Capture (1 = every compositor frame). Set before Start.</summary>
    public double MinUpdateMs { get; set; } = 1;

    private long _readbackTicks, _readbackCount, _readbackMaxTicks;
    private ID3D11Texture2D? _mipTexture;
    private ID3D11ShaderResourceView? _mipView;
    private readonly Dictionary<int, ID3D11Texture2D> _staging = new();

    /// <summary>Average and maximum time of the GPU-to-CPU copy since the last <see cref="ResetStats"/>, in milliseconds.</summary>
    public (double AvgMs, double MaxMs, long Count) ReadbackStats()
    {
        long n = Interlocked.Read(ref _readbackCount);
        return (n == 0 ? 0 : Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _readbackTicks)).TotalMilliseconds / n,
            Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _readbackMaxTicks)).TotalMilliseconds, n);
    }

    public void ResetStats()
    {
        lock (_gate) ResetLocked();
        Interlocked.Exchange(ref _readbackTicks, 0);
        Interlocked.Exchange(ref _readbackCount, 0);
        Interlocked.Exchange(ref _readbackMaxTicks, 0);
    }

    private void ReadBack(Direct3D11CaptureFrame frame, int mipLevel, Action<byte[], int, int, int> sink)
    {
        long t0 = Stopwatch.GetTimestamp();
        using var texture = FrameCapture.TextureOf(frame.Surface);
        var desc = texture.Description;
        ID3D11Texture2D source = texture;
        uint subresource = 0;
        int width = (int)desc.Width, height = (int)desc.Height;
        if (mipLevel > 0)
        {
            if (_mipTexture is null)
            {
                _mipTexture = _device!.CreateTexture2D(desc with
                {
                    MipLevels = (uint)mipLevel + 1,
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.GenerateMips,
                });
                _mipView = _device.CreateShaderResourceView(_mipTexture);
            }
            _context!.CopySubresourceRegion(_mipTexture, 0, 0, 0, 0, texture, 0);
            _context.GenerateMips(_mipView!);
            source = _mipTexture;
            subresource = (uint)mipLevel;
            width >>= mipLevel;
            height >>= mipLevel;
        }

        if (!_staging.TryGetValue(mipLevel, out var staging))
        {
            staging = _device!.CreateTexture2D(desc with
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1,
                Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None,
            });
            _staging[mipLevel] = staging;
        }
        _context!.CopySubresourceRegion(staging, 0, 0, 0, 0, source, subresource);
        var map = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        byte[] pixels;
        try
        {
            pixels = new byte[(int)(height * map.RowPitch)];
            Marshal.Copy(map.DataPointer, pixels, 0, pixels.Length);
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
        long took = Stopwatch.GetTimestamp() - t0;
        Interlocked.Add(ref _readbackTicks, took);
        Interlocked.Increment(ref _readbackCount);
        long seen;
        while (took > (seen = Interlocked.Read(ref _readbackMaxTicks)) && Interlocked.CompareExchange(ref _readbackMaxTicks, took, seen) != seen) { }
        sink(pixels, width, height, (int)map.RowPitch);
    }

    private void LogSecond()
    {
        int a, d;
        lock (_gate)
        {
            a = _secondArrived; d = _secondDelivered;
            _secondArrived = _secondDelivered = 0;
        }
        SpikeLog.Write($"fps arrived={a} delivered={d} target={_targetFps}");
    }

    public CaptureSummary Summarize()
    {
        lock (_gate)
        {
            double seconds = (DateTime.UtcNow - _started).TotalSeconds;
            double cpu = (Process.GetCurrentProcess().TotalProcessorTime - _cpuStart).TotalSeconds / seconds / Environment.ProcessorCount * 100;
            var sorted = _intervalsMs.OrderBy(x => x).ToArray();
            double p99 = sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))];
            double max = sorted.Length == 0 ? 0 : sorted[^1];
            int expected = (int)(seconds * _targetFps);
            return new CaptureSummary(seconds, _arrived, _delivered, _delivered / seconds, _arrived / seconds,
                p99, max, _stutters, Math.Max(0, expected - _delivered), cpu);
        }
    }

    public void Dispose()
    {
        _secondTimer?.Dispose();
        if (_pool is not null) _pool.FrameArrived -= OnFrameArrived;
        _session?.Dispose();
        _pool?.Dispose();
        foreach (var t in _staging.Values) t.Dispose();
        _mipView?.Dispose();
        _mipTexture?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
