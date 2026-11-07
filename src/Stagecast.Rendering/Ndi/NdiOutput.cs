using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Audio;
using Stagecast.Core.Frames;

namespace Stagecast.Rendering.Ndi;

/// <summary>
/// An NDI sender: the program picture at its native render size (never squeezed) and the program audio. The video is handed to a worker
/// thread that sends only the newest frame, so a slow network never stalls the frame hub that also feeds the screen.
/// </summary>
public sealed class NdiOutput : IFrameSink, IAudioSink, IDisposable
{
    private readonly ILogger _logger;
    private readonly Func<int> _fps;
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private IntPtr _sender;
    private IntPtr _namePtr;
    private Thread? _worker;
    private volatile bool _stop;
    private Frame? _pending;
    private long _videoSent, _audioSent, _videoSkipped;
    private float[] _planar = new float[AudioFormat.SampleRate / 10 * AudioFormat.Channels];

    public NdiOutput(ILogger logger, Func<int> fps)
    {
        _logger = logger;
        _fps = fps;
    }

    string IFrameSink.Name => "ndi";

    string IAudioSink.Name => "ndi";

    public string SourceName { get; private set; } = string.Empty;

    public bool IsRunning => _sender != IntPtr.Zero;

    public long VideoSent => Interlocked.Read(ref _videoSent);

    public long AudioSent => Interlocked.Read(ref _audioSent);

    /// <summary>Frames replaced by a newer one before the worker could send them.</summary>
    public long VideoSkipped => Interlocked.Read(ref _videoSkipped);

    /// <summary>Null when the NDI runtime is installed and usable, else what to tell the operator.</summary>
    public static string? CheckRuntime() => NdiNative.CheckRuntime();

    /// <summary>Opens the sender under <paramref name="name"/>. Returns an error message, or null on success.</summary>
    public string? Start(string name)
    {
        if (IsRunning) return null;
        if (NdiNative.CheckRuntime() is { } problem) return problem;
        _namePtr = Marshal.StringToCoTaskMemUTF8(name);
        var settings = new NdiNative.SendCreate { Name = _namePtr, Groups = IntPtr.Zero, ClockVideo = false, ClockAudio = false };
        IntPtr sender = NdiNative.send_create(ref settings);
        if (sender == IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_namePtr);
            _namePtr = IntPtr.Zero;
            return "NDI tidak bisa membuat sumber (nama dipakai atau jaringan tidak tersedia).";
        }
        SourceName = name;
        _stop = false;
        _sender = sender;
        _worker = new Thread(SendLoop) { IsBackground = true, Name = "ndi-video" };
        _worker.Start();
        _logger.LogInformation("NDI sender '{Name}' started", name);
        return null;
    }

    public void Stop()
    {
        IntPtr sender;
        lock (_gate)
        {
            sender = _sender;
            _sender = IntPtr.Zero;
        }
        if (sender == IntPtr.Zero) return;
        _stop = true;
        _wake.Set();
        _worker?.Join(2000);
        _worker = null;
        Interlocked.Exchange(ref _pending, null)?.Release();
        NdiNative.send_destroy(sender);
        if (_namePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(_namePtr);
        _namePtr = IntPtr.Zero;
        _logger.LogInformation("NDI sender '{Name}' stopped (video {Video}, audio {Audio})", SourceName, VideoSent, AudioSent);
    }

    /// <summary>How many receivers are connected right now.</summary>
    public int Connections
    {
        get
        {
            lock (_gate) return _sender == IntPtr.Zero ? 0 : NdiNative.send_get_no_connections(_sender, 0);
        }
    }

    /// <summary>Whether the picture goes out with its transparency. Takes effect from the next frame.</summary>
    public volatile bool SendAlpha;

    private long _wantsCheckedAt = -1000; // so the first look happens at once
    private volatile bool _wants;

    /// <summary>Only while a receiver is connected (looked at every 250 ms); without one the hub neither sends nor copies frames.</summary>
    public bool WantsFrames
    {
        get
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _wantsCheckedAt) >= 250)
            {
                _wants = IsRunning && Connections > 0;
                Interlocked.Exchange(ref _wantsCheckedAt, now);
            }
            return _wants;
        }
    }

    public void Deliver(Frame frame, bool repeated)
    {
        if (!IsRunning) return;
        frame.Retain();
        var old = Interlocked.Exchange(ref _pending, frame);
        if (old is not null)
        {
            old.Release();
            Interlocked.Increment(ref _videoSkipped);
        }
        _wake.Set();
    }

    private void SendLoop()
    {
        while (!_stop)
        {
            _wake.WaitOne(200);
            var frame = Interlocked.Exchange(ref _pending, null);
            if (frame is null) continue;
            try
            {
                var handle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
                try
                {
                    var video = new NdiNative.VideoFrame
                    {
                        Xres = frame.Width,
                        Yres = frame.Height,
                        // BGRX tells receivers not to blend the picture with anything; BGRA lets the see-through bars of a document show what is behind.
                        FourCC = SendAlpha ? NdiNative.FourCcBgra : NdiNative.FourCcBgrx,
                        FrameRateN = Math.Max(1, _fps()),
                        FrameRateD = 1,
                        PictureAspectRatio = (float)frame.Width / frame.Height,
                        FrameFormatType = 1,
                        Timecode = NdiNative.TimecodeSynthesize,
                        Data = handle.AddrOfPinnedObject(),
                        LineStrideInBytes = frame.Stride,
                    };
                    lock (_gate)
                    {
                        if (_sender != IntPtr.Zero) NdiNative.send_send_video_v2(_sender, ref video);
                    }
                    Interlocked.Increment(ref _videoSent);
                }
                finally
                {
                    handle.Free();
                }
            }
            finally
            {
                frame.Release();
            }
        }
    }

    public void Write(float[] samples, int frames)
    {
        if (!IsRunning || frames <= 0) return;
        // NDI wants planar floats: all left samples, then all right samples.
        if (_planar.Length < frames * 2) _planar = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            _planar[i] = samples[i * 2];
            _planar[frames + i] = samples[i * 2 + 1];
        }
        var handle = GCHandle.Alloc(_planar, GCHandleType.Pinned);
        try
        {
            var audio = new NdiNative.AudioFrame
            {
                SampleRate = AudioFormat.SampleRate,
                NoChannels = 2,
                NoSamples = frames,
                Timecode = NdiNative.TimecodeSynthesize,
                Data = handle.AddrOfPinnedObject(),
                ChannelStrideInBytes = frames * 4,
            };
            lock (_gate)
            {
                if (_sender != IntPtr.Zero) NdiNative.send_send_audio_v2(_sender, ref audio);
            }
            Interlocked.Increment(ref _audioSent);
        }
        finally
        {
            handle.Free();
        }
    }

    public void Dispose() => Stop();
}
