using System.Diagnostics;
using libomtnet;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Audio;
using Stagecast.Core.Frames;

namespace Stagecast.Rendering.Omt;

/// <summary>
/// An Open Media Transport sender (for vMix): the program picture at its native render size and the program audio. Like the NDI output,
/// video goes through a worker thread that sends only the newest frame. Frames are stamped from one clock so picture and sound line up.
/// </summary>
public sealed class OmtOutput : IFrameSink, IAudioSink, IDisposable
{
    private readonly ILogger _logger;
    private readonly Func<int> _fps;
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Stopwatch _clock = new();
    private OMTSend? _sender;
    private Thread? _worker;
    private volatile bool _stop;
    private Frame? _pending;
    private long _videoSent, _audioSent, _videoSkipped;
    private float[] _planar = new float[AudioFormat.SampleRate / 10 * AudioFormat.Channels];

    public OmtOutput(ILogger logger, Func<int> fps)
    {
        _logger = logger;
        _fps = fps;
    }

    string IFrameSink.Name => "omt";

    string IAudioSink.Name => "omt";

    public string SourceName { get; private set; } = string.Empty;

    public bool IsRunning => _sender is not null;

    public long VideoSent => Interlocked.Read(ref _videoSent);

    public long AudioSent => Interlocked.Read(ref _audioSent);

    public long VideoSkipped => Interlocked.Read(ref _videoSkipped);

    /// <summary>Receivers connected now. Each receiver opens two connections (video and audio).</summary>
    public int Receivers
    {
        get
        {
            lock (_gate) return ((_sender?.Connections ?? 0) + 1) / 2;
        }
    }

    /// <summary>Opens the sender under <paramref name="name"/> (the machine name is added by OMT). Returns an error message, or null on success.</summary>
    public string? Start(string name)
    {
        if (IsRunning) return null;
        try
        {
            _sender = new OMTSend(name, OMTQuality.Default);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or DllNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            return $"OMT tidak bisa membuat sumber: {ex.Message}";
        }
        SourceName = name;
        _stop = false;
        _clock.Restart();
        _worker = new Thread(SendLoop) { IsBackground = true, Name = "omt-video" };
        _worker.Start();
        _logger.LogInformation("OMT sender '{Name}' started on {Url}", name, _sender.URL);
        return null;
    }

    public void Stop()
    {
        OMTSend? sender;
        lock (_gate)
        {
            sender = _sender;
            _sender = null;
        }
        if (sender is null) return;
        _stop = true;
        _wake.Set();
        _worker?.Join(2000);
        _worker = null;
        Interlocked.Exchange(ref _pending, null)?.Release();
        sender.Dispose();
        _logger.LogInformation("OMT sender '{Name}' stopped (video {Video}, audio {Audio})", SourceName, VideoSent, AudioSent);
    }

    private long Now => _clock.ElapsedTicks * 10_000_000L / Stopwatch.Frequency;

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
                _wants = IsRunning && Receivers > 0;
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
                    var media = new OMTMediaFrame
                    {
                        Type = OMTFrameType.Video,
                        Timestamp = Now,
                        Codec = (int)OMTCodec.BGRA,
                        Width = frame.Width,
                        Height = frame.Height,
                        Stride = frame.Stride,
                        // Without the Alpha flag BGRA is treated as opaque BGRX. The stage picture is premultiplied.
                        Flags = SendAlpha ? OMTVideoFlags.Alpha | OMTVideoFlags.PreMultiplied : OMTVideoFlags.None,
                        FrameRateN = Math.Max(1, _fps()),
                        FrameRateD = 1,
                        AspectRatio = (float)frame.Width / frame.Height,
                        ColorSpace = OMTColorSpace.BT709,
                        Data = handle.AddrOfPinnedObject(),
                        DataLength = frame.Stride * frame.Height,
                    };
                    lock (_gate) _sender?.Send(media);
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
        if (_planar.Length < frames * 2) _planar = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            _planar[i] = samples[i * 2];
            _planar[frames + i] = samples[i * 2 + 1];
        }
        var handle = GCHandle.Alloc(_planar, GCHandleType.Pinned);
        try
        {
            var media = new OMTMediaFrame
            {
                Type = OMTFrameType.Audio,
                Timestamp = Now,
                Codec = (int)OMTCodec.FPA1,
                SampleRate = AudioFormat.SampleRate,
                Channels = 2,
                SamplesPerChannel = frames,
                Data = handle.AddrOfPinnedObject(),
                DataLength = frames * 2 * 4,
            };
            lock (_gate) _sender?.Send(media);
            Interlocked.Increment(ref _audioSent);
        }
        finally
        {
            handle.Free();
        }
    }

    public void Dispose() => Stop();
}
