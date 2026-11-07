using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Display;
using Stagecast.Core.Frames;
using Stagecast.Core.Metrics;
using Stagecast.Rendering.Interop;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Stagecast.Rendering.Capture;

/// <summary>
/// Where things are on the stage when the program and the preview share it (Preview + Take): the frames for the output are cut
/// from the program rectangle, and the operator's preview is read from the preview origin. Origins that the preview is read
/// from must be multiples of 16 so shrunk previews stay aligned with the page.
/// </summary>
public sealed record StageLayout(int ProgramX, int ProgramY, int ProgramWidth, int ProgramHeight, int PreviewX, int PreviewY);

/// <summary>A copy of part of the page for the operator. <see cref="SourceX"/>/<see cref="SourceY"/> are the page pixel of its top-left corner and each of its pixels covers 2^<see cref="Mip"/> page pixels.</summary>
public sealed record PreviewImage(byte[] Pixels, int Width, int Height, int Stride, int SourceX, int SourceY, int Mip, long CapturedAt = 0);

/// <summary>
/// What the capture sees, before the hub's pacing. <see cref="ReadbackAverageMs"/> and <see cref="ReadbackMaxMs"/> are the time from
/// asking the GPU for a frame until it is published (the copy runs while the next frames are captured); <see cref="ReadbackSkipped"/>
/// counts frames not copied because the copies in flight had not finished.
/// </summary>
public sealed record CaptureStats(double ArrivedPerSecond, double ReadbackAverageMs, double ReadbackMaxMs, int Width, int Height,
    TimingSnapshot Handler = default, TimingSnapshot LockWait = default, TimingSnapshot PreviewCapture = default, long ReadbackSkipped = 0);

/// <summary>
/// Captures a composition visual with Windows Graphics Capture. Windows delivers a frame whenever the page draws (65-140 per second
/// depending on load, none at all for a still page); this class picks the ones nearest to the target schedule and asks the GPU to
/// copy them to system memory. The capture callback only issues the copy; a reader thread waits for it (without blocking the Direct3D
/// context) and publishes the frame to the hub. The operator's preview is cut and shrunk on a second thread, off the output's path.
/// </summary>
public sealed class FrameCapturer : IDisposable
{
    private const int DefaultPreviewMip = 2;
    private const int ThumbnailMip = 3;
    private const int ThumbnailIntervalMs = 264; // the small picture of the program in Preview + Take; it does not need the output rate
    private const int MaxOutputSlots = 4; // copies to system memory in flight at once
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(0.25);

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

    // Output copies: staging textures the GPU fills and the reader thread maps.
    private sealed class Slot(ID3D11Texture2D texture, int width, int height) : IDisposable
    {
        public ID3D11Texture2D Texture { get; } = texture;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public void Dispose() => Texture.Dispose();
    }

    private readonly record struct OutputJob(Slot Slot, int Width, int Height, long IssuedAt, long CapturedAt, long Sequence);

    private readonly object _slotGate = new();
    private readonly Stack<Slot> _idleSlots = new();
    private int _slotsAlive;
    private readonly BlockingCollection<OutputJob> _outputJobs = new(MaxOutputSlots);
    private Thread? _outputThread, _previewThread;

    // Preview: staging textures reused by the preview thread only.
    private readonly Dictionary<(int Width, int Height, int Mip), ID3D11Texture2D> _staging = new();
    private ID3D11Texture2D? _mipTexture;
    private ID3D11ShaderResourceView? _mipView;
    private (int Width, int Height) _mipSize;
    private readonly AutoResetEvent _previewWake = new(false);
    private int _previewDirty, _refreshQueued;
    private long _lastThumbTick;

    private PreviewRegion? _previewTarget;
    private StageLayout? _layout;
    private readonly object _deviceGate = new(); // the D3D context is not thread safe: every call on it is made while holding this, and only briefly
    private ID3D11Texture2D? _previewSource; // copy of the newest frame used for previews
    private volatile bool _stopping;
    private int _targetFps;
    private TimeSpan _lastDelivered = TimeSpan.MinValue;
    private TimeSpan _nextDue;
    private long _arrived, _sequence, _readbackSkipped;
    private long _statsStart;
    private long _readbackTicks, _readbackCount, _readbackMaxTicks;
    private int _width, _height;
    private readonly TimingStats _handlerTimes = new(), _lockWaitTimes = new(), _previewCaptureTimes = new();

    public FrameCapturer(Windows.UI.Composition.ContainerVisual visual, FrameHub hub, FramePool pool, int targetFps, TimeProvider time, ILogger logger)
    {
        _visual = visual;
        _hub = hub;
        _pool = pool;
        _targetFps = targetFps;
        _time = time;
        _logger = logger;
    }

    /// <summary>Called on the preview thread once for every frame sent to the output (so at the output's frame rate) with a quarter-size copy of the page.</summary>
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
            if (changed) RequestRefresh();
        }
    }

    /// <summary>Null when the whole stage is the program (Direct mode). Changing it refreshes the preview at once.</summary>
    public StageLayout? Layout
    {
        get { lock (_gate) return _layout; }
        set
        {
            lock (_gate) _layout = value;
            RequestRefresh();
        }
    }

    /// <summary>A small picture of the program, a few times a second, raised on the preview thread. Only while a layout is set.</summary>
    public event Action<PreviewImage>? ProgramThumbReady;

    private void RequestRefresh()
    {
        Volatile.Write(ref _refreshQueued, 1);
        _previewWake.Set();
    }

    public void Start()
    {
        (_device, _context, _winrtDevice) = Direct3D.CreateDevice();
        try
        {
            using var dxgi = _device.QueryInterface<Vortice.DXGI.IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            var description = adapter.Description;
            // On a laptop with two GPUs this shows which one the capture and the copies run on.
            _logger.LogInformation("Capture device on adapter {Adapter} (luid 0x{High:X8}_0x{Low:X8})", description.Description, description.Luid.HighPart, description.Luid.LowPart);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or SharpGen.Runtime.SharpGenException)
        {
            _logger.LogDebug(ex, "Could not read the capture adapter");
        }
        _outputThread = new Thread(OutputLoop) { IsBackground = true, Name = "capture-readback", Priority = ThreadPriority.AboveNormal };
        _previewThread = new Thread(PreviewLoop) { IsBackground = true, Name = "capture-preview" };
        _outputThread.Start();
        _previewThread.Start();
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
            Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _readbackMaxTicks)).TotalMilliseconds, _width, _height,
            _handlerTimes.Snapshot(), _lockWaitTimes.Snapshot(), _previewCaptureTimes.Snapshot(), Interlocked.Read(ref _readbackSkipped));
    }

    public void ResetStats()
    {
        Interlocked.Exchange(ref _arrived, 0);
        Interlocked.Exchange(ref _readbackTicks, 0);
        Interlocked.Exchange(ref _readbackCount, 0);
        Interlocked.Exchange(ref _readbackMaxTicks, 0);
        Interlocked.Exchange(ref _readbackSkipped, 0);
        _handlerTimes.Reset();
        _lockWaitTimes.Reset();
        _previewCaptureTimes.Reset();
        _statsStart = _time.GetTimestamp();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
    {
        try
        {
            long handlerStart = Stopwatch.GetTimestamp();
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
            if (deliver)
            {
                long lockStart = Stopwatch.GetTimestamp();
                lock (_deviceGate)
                {
                    _lockWaitTimes.Add(Stopwatch.GetElapsedTime(lockStart).TotalMilliseconds);
                    var layout = Layout;
                    // The size the output has (or would have) is known even while nobody wants frames; the statistics show it.
                    _width = layout?.ProgramWidth ?? (int)texture.Description.Width;
                    _height = layout?.ProgramHeight ?? (int)texture.Description.Height;
                    bool wanted = _hub.WantsFrames;
                    if (wanted) IssueOutputCopy(texture, layout);
                    // The copy kept for previews also serves RequestFrame when nobody wanted frames until now.
                    if (PreviewReady is not null || !wanted) UpdatePreviewSource(texture);
                    if (PreviewReady is not null) Volatile.Write(ref _previewDirty, 1);
                    _context!.Flush(); // start the copies now; the threads below wait for them
                }
                if (PreviewReady is not null) _previewWake.Set();
            }
            _handlerTimes.Add(Stopwatch.GetElapsedTime(handlerStart).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Capturing a frame failed");
        }
    }

    // ---- output: the GPU copies the frame to system memory while the next ones are captured ----

    /// <summary>
    /// Copies the newest frame to the hub now. Used when an output starts to want frames (a receiver connects): a page that does
    /// not change delivers no frames of its own, so without this the receiver would wait for the page to move.
    /// </summary>
    public void RequestFrame()
    {
        try
        {
            lock (_deviceGate)
            {
                if (_previewSource is null || _context is null) return;
                IssueOutputCopy(_previewSource, Layout);
                _context.Flush();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Requesting a frame failed");
        }
    }

    /// <summary>Asks the GPU to copy the frame to a staging texture and hands the copy to the reader thread. Caller holds <see cref="_deviceGate"/>.</summary>
    private void IssueOutputCopy(ID3D11Texture2D texture, StageLayout? layout)
    {
        var desc = texture.Description;
        // Only the program part of the stage goes to the output; the rest of a shared stage is the operator's preview.
        int width = layout?.ProgramWidth ?? (int)desc.Width, height = layout?.ProgramHeight ?? (int)desc.Height;
        var slot = TakeSlot(desc, width, height);
        if (slot is null)
        {
            Interlocked.Increment(ref _readbackSkipped); // the reader is behind: the hub repeats the last frame instead of queueing latency
            return;
        }
        long issued = Stopwatch.GetTimestamp();
        if (layout is null) _context!.CopyResource(slot.Texture, texture);
        else _context!.CopySubresourceRegion(slot.Texture, 0, 0, 0, 0, texture, 0, new Box(layout.ProgramX, layout.ProgramY, 0, layout.ProgramX + width, layout.ProgramY + height, 1));
        _width = width;
        _height = height;
        var job = new OutputJob(slot, width, height, issued, _time.GetTimestamp(), Interlocked.Increment(ref _sequence));
        try
        {
            if (_outputJobs.TryAdd(job)) return;
        }
        catch (InvalidOperationException)
        {
            // shutting down
        }
        ReturnSlot(slot);
        Interlocked.Increment(ref _readbackSkipped);
    }

    private Slot? TakeSlot(Texture2DDescription source, int width, int height)
    {
        lock (_slotGate)
        {
            while (_idleSlots.Count > 0)
            {
                var idle = _idleSlots.Pop();
                if (idle.Width == width && idle.Height == height) return idle;
                idle.Dispose(); // the resolution changed
                _slotsAlive--;
            }
            if (_slotsAlive >= MaxOutputSlots) return null;
            _slotsAlive++;
        }
        try
        {
            var texture = _device!.CreateTexture2D(source with
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1,
                Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None,
            });
            return new Slot(texture, width, height);
        }
        catch
        {
            lock (_slotGate) _slotsAlive--;
            throw;
        }
    }

    private void ReturnSlot(Slot slot)
    {
        lock (_slotGate)
        {
            if (!_stopping && _idleSlots.Count < MaxOutputSlots)
            {
                _idleSlots.Push(slot);
                return;
            }
            _slotsAlive--;
        }
        slot.Dispose();
    }

    private void OutputLoop()
    {
        using var timer = new HighResolutionTimer();
        try
        {
            foreach (var job in _outputJobs.GetConsumingEnumerable())
            {
                try
                {
                    CompleteOutput(job, timer);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogError(ex, "Publishing a frame failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private void CompleteOutput(OutputJob job, HighResolutionTimer timer)
    {
        Frame? frame = null;
        try
        {
            MappedSubresource? mapped = WaitForCopy(job.Slot.Texture, timer);
            if (mapped is { } map)
            {
                try
                {
                    int stride = (int)map.RowPitch;
                    frame = _pool.Rent(job.Width, job.Height, stride);
                    Marshal.Copy(map.DataPointer, frame.Pixels, 0, stride * job.Height);
                }
                finally
                {
                    lock (_deviceGate) _context!.Unmap(job.Slot.Texture, 0);
                }
            }
        }
        catch
        {
            frame?.Release();
            throw;
        }
        finally
        {
            ReturnSlot(job.Slot); // always: a slot that is never returned would stop the output after a few failures
        }
        if (frame is null) return;

        frame.CapturedAt = job.CapturedAt;
        frame.Sequence = job.Sequence;
        _hub.Publish(frame);

        long took = Stopwatch.GetTimestamp() - job.IssuedAt;
        Interlocked.Add(ref _readbackTicks, took);
        Interlocked.Increment(ref _readbackCount);
        long seen;
        while (took > (seen = Interlocked.Read(ref _readbackMaxTicks)) && Interlocked.CompareExchange(ref _readbackMaxTicks, took, seen) != seen) { }
    }

    /// <summary>
    /// Waits until the GPU has finished filling <paramref name="staging"/> and maps it for reading, or returns null when stopping.
    /// The Direct3D context is locked only for each attempt (which does not wait), so the capture thread is never held up by a copy.
    /// </summary>
    private MappedSubresource? WaitForCopy(ID3D11Texture2D staging, HighResolutionTimer timer)
    {
        while (!_stopping)
        {
            SharpGen.Runtime.Result result;
            MappedSubresource map;
            lock (_deviceGate)
            {
                if (_context is null) return null;
                result = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out map);
            }
            if (result.Success) return map;
            if (result.Code != Vortice.DXGI.ResultCode.WasStillDrawing.Code) result.CheckError();
            timer.Wait(PollInterval);
        }
        return null;
    }

    // ---- preview: cut and shrunk on its own thread ----

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

    private readonly record struct PreviewJob(ID3D11Texture2D Staging, int Width, int Height, int SourceX, int SourceY, int Mip);

    private void PreviewLoop()
    {
        using var timer = new HighResolutionTimer();
        while (!_stopping)
        {
            _previewWake.WaitOne();
            if (_stopping) break;
            bool frameDirty = Interlocked.Exchange(ref _previewDirty, 0) == 1;
            bool refresh = Interlocked.Exchange(ref _refreshQueued, 0) == 1;
            if (!frameDirty && !refresh) continue;
            try
            {
                long start = Stopwatch.GetTimestamp();
                var handler = PreviewReady;
                if (handler is not null && ReadPreview(timer, thumbnail: false) is { } image) handler(image);

                var layout = Layout;
                var thumbHandler = ProgramThumbReady;
                if (frameDirty && layout is not null && thumbHandler is not null
                    && Stopwatch.GetElapsedTime(_lastThumbTick, start).TotalMilliseconds >= ThumbnailIntervalMs)
                {
                    _lastThumbTick = start;
                    if (ReadPreview(timer, thumbnail: true) is { } thumb) thumbHandler(thumb);
                }
                if (frameDirty) _previewCaptureTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "Making the preview failed");
            }
        }
    }

    /// <summary>One picture for the operator: the copy is issued under the lock, then waited for and read outside it.</summary>
    private PreviewImage? ReadPreview(HighResolutionTimer timer, bool thumbnail)
    {
        PreviewJob job;
        lock (_deviceGate)
        {
            if (_previewSource is null || _context is null) return null;
            job = IssuePreview(_previewSource, PreviewTarget, Layout, thumbnail);
        }
        MappedSubresource? mapped = WaitForCopy(job.Staging, timer);
        if (mapped is not { } map) return null;
        try
        {
            var pixels = new byte[(int)map.RowPitch * job.Height];
            Marshal.Copy(map.DataPointer, pixels, 0, pixels.Length);
            return new PreviewImage(pixels, job.Width, job.Height, (int)map.RowPitch, job.SourceX, job.SourceY, job.Mip, Stopwatch.GetTimestamp());
        }
        finally
        {
            lock (_deviceGate) _context!.Unmap(job.Staging, 0);
        }
    }

    // The GPU shrinks the frame with mipmaps first, so the CPU copy for the preview is a fraction of the full size.
    // `target` is relative to the page being previewed; with a layout the page sits at the preview origin of the stage.
    // Caller holds _deviceGate.
    private PreviewJob IssuePreview(ID3D11Texture2D texture, PreviewRegion? target, StageLayout? layout, bool thumbnail)
    {
        var desc = texture.Description;
        int width = (int)desc.Width, height = (int)desc.Height;
        PreviewRegion wanted;
        int originX, originY;
        if (thumbnail)
        {
            // The program, small, for the operator to see what is on air.
            wanted = new PreviewRegion(0, 0, layout!.ProgramWidth, layout.ProgramHeight, ThumbnailMip);
            (originX, originY) = (layout.ProgramX, layout.ProgramY);
        }
        else
        {
            wanted = target ?? new PreviewRegion(0, 0, layout?.ProgramWidth ?? width, layout?.ProgramHeight ?? height, DefaultPreviewMip);
            (originX, originY) = layout is null ? (0, 0) : (layout.PreviewX, layout.PreviewY);
        }
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
        int x = Math.Clamp((wanted.X + originX) >> mip, 0, levelWidth - 1), y = Math.Clamp((wanted.Y + originY) >> mip, 0, levelHeight - 1);
        int w = Math.Clamp(Math.Max(1, wanted.Width >> mip), 1, levelWidth - x), h = Math.Clamp(Math.Max(1, wanted.Height >> mip), 1, levelHeight - y);

        var staging = StagingFor(desc, w, h, mip);
        _context!.CopySubresourceRegion(staging, 0, 0, 0, 0, source, (uint)mip, new Box(x, y, 0, x + w, y + h, 1));
        _context.Flush();
        return new PreviewJob(staging, w, h, (x << mip) - originX, (y << mip) - originY, mip);
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
        _stopping = true;
        if (_framePool is not null) _framePool.FrameArrived -= OnFrameArrived;
        _session?.Dispose();
        _framePool?.Dispose();
        _outputJobs.CompleteAdding();
        _previewWake.Set();
        _outputThread?.Join(2000);
        _previewThread?.Join(2000);
        _previewWake.Dispose();
        lock (_slotGate)
        {
            foreach (var slot in _idleSlots) slot.Dispose();
            _idleSlots.Clear();
        }
        foreach (var t in _staging.Values) t.Dispose();
        _mipView?.Dispose();
        _mipTexture?.Dispose();
        _previewSource?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
