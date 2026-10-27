using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Display;
using Stagecast.Core.Frames;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Stagecast.Rendering.Capture;

/// <summary>A small copy of the page for the operator's preview window (BGRA).</summary>
/// <summary>A copy of part of the page for the operator. <see cref="SourceX"/>/<see cref="SourceY"/> are the page pixel of its top-left corner and each of its pixels covers 2^<see cref="Mip"/> page pixels.</summary>
public sealed record PreviewImage(byte[] Pixels, int Width, int Height, int Stride, int SourceX, int SourceY, int Mip);

/// <summary>What the capture sees, before the hub's pacing.</summary>
public sealed record CaptureStats(double ArrivedPerSecond, double ReadbackAverageMs, double ReadbackMaxMs, int Width, int Height);

/// <summary>
/// Captures a composition visual with Windows Graphics Capture. Windows delivers a frame whenever the page draws (65-140 per second
/// depending on load, none at all for a still page); this class picks the ones nearest to the target schedule, copies them to the
/// CPU at full size and publishes them to the hub, and separately shrinks a copy on the GPU for the preview.
/// </summary>
public sealed class FrameCapturer : IDisposable
{
    private const int DefaultPreviewMip = 2;
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

    private PreviewRegion? _previewTarget;
    private readonly object _deviceGate = new(); // the D3D context is not thread safe: capture thread and preview refreshes take turns
    private ID3D11Texture2D? _previewSource; // copy of the newest frame used for previews
    private int _refreshQueued;
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

    /// <summary>What the next previews should contain; null means the whole page at a quarter of the size.</summary>
    public PreviewRegion? PreviewTarget
    {
        get { lock (_gate) return _previewTarget; }
        set
        {
            bool changed;
            lock (_gate)
            {
                changed = _previewTarget != value;
                _previewTarget = value;
            }
            // A still page produces no frames, so a new view has to be cut from the copy of the last one.
            if (changed && Interlocked.Exchange(ref _refreshQueued, 1) == 0) ThreadPool.QueueUserWorkItem(_ => RefreshPreview());
        }
    }

    private void RefreshPreview()
    {
        Interlocked.Exchange(ref _refreshQueued, 0);
        try
        {
            lock (_deviceGate)
            {
                if (_previewSource is null || _context is null) return;
                PreviewReady?.Invoke(ReadPreview(_previewSource));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Refreshing the preview failed");
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
            lock (_deviceGate)
            {
                if (deliver) PublishFull(texture);
                long now = Stopwatch.GetTimestamp();
                if (PreviewReady is { } handler && Stopwatch.GetElapsedTime(_lastPreviewTick, now).TotalMilliseconds >= PreviewIntervalMs)
                {
                    _lastPreviewTick = now;
                    UpdatePreviewSource(texture);
                    handler(ReadPreview(_previewSource!));
                }
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

    private void UpdatePreviewSource(ID3D11Texture2D texture)
    {
        var desc = texture.Description;
        if (_previewSource is null || _previewSource.Description.Width != desc.Width || _previewSource.Description.Height != desc.Height)
        {
            _previewSource?.Dispose();
            _previewSource = _device!.CreateTexture2D(desc with
            {
                MipLevels = 1, Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource, CPUAccessFlags = CpuAccessFlags.None, MiscFlags = ResourceOptionFlags.None,
            });
        }
        _context!.CopyResource(_previewSource, texture);
    }

    // The GPU shrinks the frame with mipmaps first, so the CPU copy for the preview is a fraction of the full size.
    private PreviewImage ReadPreview(ID3D11Texture2D texture)
    {
        var desc = texture.Description;
        int width = (int)desc.Width, height = (int)desc.Height;
        var wanted = PreviewTarget ?? new PreviewRegion(0, 0, width, height, DefaultPreviewMip);
        int mip = Math.Clamp(wanted.Mip, 0, PreviewRegion.MaxMip);

        ID3D11Texture2D source = texture;
        if (mip > 0)
        {
            if (_mipTexture is null || _mipSize != (width, height))
            {
                _mipView?.Dispose();
                _mipTexture?.Dispose();
                _mipTexture = _device!.CreateTexture2D(desc with
                {
                    MipLevels = PreviewRegion.MaxMip + 1,
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
            source = _mipTexture;
        }

        // The region is given in page pixels; at a deeper level the same area has fewer pixels.
        int levelWidth = Math.Max(1, width >> mip), levelHeight = Math.Max(1, height >> mip);
        int x = Math.Clamp(wanted.X >> mip, 0, levelWidth - 1), y = Math.Clamp(wanted.Y >> mip, 0, levelHeight - 1);
        int w = Math.Clamp(Math.Max(1, wanted.Width >> mip), 1, levelWidth - x), h = Math.Clamp(Math.Max(1, wanted.Height >> mip), 1, levelHeight - y);

        var staging = StagingFor(desc, w, h, mip);
        _context!.CopySubresourceRegion(staging, 0, 0, 0, 0, source, (uint)mip, new Box(x, y, 0, x + w, y + h, 1));
        var map = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var pixels = new byte[(int)map.RowPitch * h];
            Marshal.Copy(map.DataPointer, pixels, 0, pixels.Length);
            return new PreviewImage(pixels, w, h, (int)map.RowPitch, x << mip, y << mip, mip);
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
        _previewSource?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
