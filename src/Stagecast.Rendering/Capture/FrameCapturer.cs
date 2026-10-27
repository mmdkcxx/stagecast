using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Frames;
using Vortice.Direct3D11;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Stagecast.Rendering.Capture;

/// <summary>A small copy of the page for the operator's preview window (BGRA).</summary>
public sealed record PreviewImage(byte[] Pixels, int Width, int Height, int Stride);

/// <summary>What the capture sees, before the hub's pacing.</summary>
public sealed record CaptureStats(double ArrivedPerSecond, double ReadbackAverageMs, double ReadbackMaxMs, int Width, int Height);

/// <summary>
/// Captures a composition visual with Windows Graphics Capture. Windows delivers a frame whenever the page draws (65-140 per second
/// depending on load, none at all for a still page); this class picks the ones nearest to the target schedule, copies them to the
/// CPU at full size and publishes them to the hub, and separately shrinks a copy on the GPU for the preview.
/// </summary>
public sealed class FrameCapturer : IDisposable
{
    private const int PreviewMipLevel = 2;
    private const int PreviewIntervalMs = 66;

    private readonly Windows.UI.Composition.ContainerVisual _visual;
    private readonly FrameHub _hub;
    private readonly FramePool _pool;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;

    private readonly Dictionary<(int Width, int Height, int Mip), ID3D11Texture2D> _staging = new();
    private ID3D11Texture2D? _mipTexture;
    private ID3D11ShaderResourceView? _mipView;
    private (int Width, int Height) _mipSize;

    private int _targetFps;
    private TimeSpan _lastDelivered = TimeSpan.MinValue;
    private TimeSpan _nextDue;
    private long _arrived, _sequence, _lastPreviewTick;
    private long _statsStart;
    private long _readbackTicks, _readbackCount, _readbackMaxTicks;
    private int _width, _height;

    public FrameCapturer(Windows.UI.Composition.ContainerVisual visual, FrameHub hub, FramePool pool, int targetFps, TimeProvider time, ILogger logger)
    {
        _visual = visual;
        _hub = hub;
        _pool = pool;
        _targetFps = targetFps;
        _time = time;
        _logger = logger;
    }

    /// <summary>Called on a capture thread about 15 times a second with a quarter-size copy of the page.</summary>
    public event Action<PreviewImage>? PreviewReady;

    public int TargetFps
    {
        get => Volatile.Read(ref _targetFps);
        set
        {
            lock (_gate)
            {
                _targetFps = value;
                _lastDelivered = TimeSpan.MinValue;
            }
        }
    }

    public void Start()
    {
        (_device, _context, _winrtDevice) = Direct3D.CreateDevice();
        StartSession();
    }

    /// <summary>
    /// A capture item keeps the size the visual had when it was created, so after the visual is resized a new item, pool and
    /// session are needed. Frames of the old size keep flowing until the first frame of the new session arrives.
    /// </summary>
    public void Restart()
    {
        var oldSession = _session;
        var oldPool = _framePool;
        StartSession();
        if (oldPool is not null) oldPool.FrameArrived -= OnFrameArrived;
        oldSession?.Dispose();
        oldPool?.Dispose();
    }

    private void StartSession()
    {
        var item = GraphicsCaptureItem.CreateFromVisual(_visual);
        _poolSize = item.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, item.Size);
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(item);
        // By default Windows paces itself to about 48 fps; ask for every compositor frame and pace in OnFrameArrived instead.
        _session.MinUpdateInterval = TimeSpan.FromMilliseconds(1);
        _session.IsCursorCaptureEnabled = false;
        _statsStart = _time.GetTimestamp();
        _lastDelivered = TimeSpan.MinValue;
        _session.StartCapture();
        _logger.LogInformation("Capture started on a {Width}x{Height} visual, target {Fps} fps", item.Size.Width, item.Size.Height, _targetFps);
    }

    public CaptureStats Stats()
    {
        double seconds = Math.Max(0.001, _time.GetElapsedTime(_statsStart).TotalSeconds);
        long count = Interlocked.Read(ref _readbackCount);
        double avg = count == 0 ? 0 : Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _readbackTicks)).TotalMilliseconds / count;
        return new CaptureStats(Interlocked.Read(ref _arrived) / seconds, avg,
            Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _readbackMaxTicks)).TotalMilliseconds, _width, _height);
    }

    public void ResetStats()
    {
        Interlocked.Exchange(ref _arrived, 0);
        Interlocked.Exchange(ref _readbackTicks, 0);
        Interlocked.Exchange(ref _readbackCount, 0);
        Interlocked.Exchange(ref _readbackMaxTicks, 0);
        _statsStart = _time.GetTimestamp();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
    {
        try
        {
            using var frame = pool.TryGetNextFrame();
            if (frame is null) return;
            Interlocked.Increment(ref _arrived);

            // The visual was resized: new frames have the new size, the pool must be told.
            if (frame.ContentSize.Width != _poolSize.Width || frame.ContentSize.Height != _poolSize.Height)
            {
                _poolSize = frame.ContentSize;
                pool.Recreate(_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, _poolSize);
                _logger.LogInformation("Capture size is now {Width}x{Height}", _poolSize.Width, _poolSize.Height);
            }

            bool deliver;
            lock (_gate)
            {
                // Take the frame nearest to each ideal time. Compositor frames come every ~7 ms (144 Hz), so 60 fps alternates
                // 14 and 21 ms gaps but averages exactly 60.
                var t = frame.SystemRelativeTime;
                double interval = 1000.0 / _targetFps;
                deliver = _lastDelivered == TimeSpan.MinValue || (t - _nextDue).TotalMilliseconds >= -3.5;
                if (deliver)
                {
                    _nextDue = _lastDelivered == TimeSpan.MinValue ? t + TimeSpan.FromMilliseconds(interval) : _nextDue + TimeSpan.FromMilliseconds(interval);
                    if ((t - _nextDue).TotalMilliseconds > interval) _nextDue = t + TimeSpan.FromMilliseconds(interval); // fell behind: resync
                    _lastDelivered = t;
                }
            }

            using var texture = Direct3D.TextureOf(frame.Surface);
            if (deliver) PublishFull(texture);
            long now = Stopwatch.GetTimestamp();
            if (PreviewReady is { } handler && Stopwatch.GetElapsedTime(_lastPreviewTick, now).TotalMilliseconds >= PreviewIntervalMs)
            {
                _lastPreviewTick = now;
                handler(ReadPreview(texture));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Capturing a frame failed");
        }
    }

    private void PublishFull(ID3D11Texture2D texture)
    {
        long t0 = Stopwatch.GetTimestamp();
        var desc = texture.Description;
        int width = (int)desc.Width, height = (int)desc.Height;
        var staging = StagingFor(desc, width, height, 0);
        _context!.CopyResource(staging, texture);
        var map = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        Frame frame;
        try
        {
            int stride = (int)map.RowPitch;
            frame = _pool.Rent(width, height, stride);
            Marshal.Copy(map.DataPointer, frame.Pixels, 0, stride * height);
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
        frame.CapturedAt = _time.GetTimestamp();
        frame.Sequence = Interlocked.Increment(ref _sequence);
        _width = width;
        _height = height;
        _hub.Publish(frame);

        long took = Stopwatch.GetTimestamp() - t0;
        Interlocked.Add(ref _readbackTicks, took);
        Interlocked.Increment(ref _readbackCount);
        long seen;
        while (took > (seen = Interlocked.Read(ref _readbackMaxTicks)) && Interlocked.CompareExchange(ref _readbackMaxTicks, took, seen) != seen) { }
    }

    // The GPU shrinks the frame with mipmaps first, so the CPU copy for the preview is a sixteenth of the size.
    private PreviewImage ReadPreview(ID3D11Texture2D texture)
    {
        var desc = texture.Description;
        int width = (int)desc.Width, height = (int)desc.Height;
        if (_mipTexture is null || _mipSize != (width, height))
        {
            _mipView?.Dispose();
            _mipTexture?.Dispose();
            _mipTexture = _device!.CreateTexture2D(desc with
            {
                MipLevels = PreviewMipLevel + 1,
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.GenerateMips,
            });
            _mipView = _device.CreateShaderResourceView(_mipTexture);
            _mipSize = (width, height);
        }
        _context!.CopySubresourceRegion(_mipTexture, 0, 0, 0, 0, texture, 0);
        _context.GenerateMips(_mipView!);

        int w = Math.Max(1, width >> PreviewMipLevel), h = Math.Max(1, height >> PreviewMipLevel);
        var staging = StagingFor(desc, w, h, PreviewMipLevel);
        _context.CopySubresourceRegion(staging, 0, 0, 0, 0, _mipTexture, PreviewMipLevel);
        var map = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var pixels = new byte[(int)map.RowPitch * h];
            Marshal.Copy(map.DataPointer, pixels, 0, pixels.Length);
            return new PreviewImage(pixels, w, h, (int)map.RowPitch);
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    private ID3D11Texture2D StagingFor(Texture2DDescription source, int width, int height, int mip)
    {
        if (_staging.TryGetValue((width, height, mip), out var existing)) return existing;
        // A resolution change makes the old staging textures useless.
        if (_staging.Count > 4) { foreach (var t in _staging.Values) t.Dispose(); _staging.Clear(); }
        var created = _device!.CreateTexture2D(source with
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1,
            Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None,
        });
        _staging[(width, height, mip)] = created;
        return created;
    }

    public void Dispose()
    {
        if (_framePool is not null) _framePool.FrameArrived -= OnFrameArrived;
        _session?.Dispose();
        _framePool?.Dispose();
        foreach (var t in _staging.Values) t.Dispose();
        _mipView?.Dispose();
        _mipTexture?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
