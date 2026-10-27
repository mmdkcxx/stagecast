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

    public ContinuousCapture(Windows.UI.Composition.ContainerVisual visual, int targetFps, Action<byte[], int, int, int>? onPreview)
    {
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
        _session.MinUpdateInterval = TimeSpan.FromMilliseconds(1);
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

        // A few times per second, read the frame back for the preview window. This is the slow path, so it is
        // kept out of the statistics above.
        long now = Stopwatch.GetTimestamp();
        if (deliver && _onPreview is not null && Stopwatch.GetElapsedTime(_lastPreviewTicks, now).TotalMilliseconds >= 250)
        {
            _lastPreviewTicks = now;
            ReadBack(frame);
        }
    }

    private void ReadBack(Direct3D11CaptureFrame frame)
    {
        using var texture = FrameCapture.TextureOf(frame.Surface);
        var desc = texture.Description;
        var stagingDesc = desc with
        {
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };
        using var staging = _device!.CreateTexture2D(stagingDesc);
        _context!.CopyResource(staging, texture);
        var map = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var pixels = new byte[(int)(desc.Height * map.RowPitch)];
            Marshal.Copy(map.DataPointer, pixels, 0, pixels.Length);
            _onPreview!(pixels, (int)desc.Width, (int)desc.Height, (int)map.RowPitch);
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
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
        _context?.Dispose();
        _device?.Dispose();
    }
}
