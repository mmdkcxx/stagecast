using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Stagecast.Rendering.Ndi;

public readonly record struct NdiVideoSample(int Width, int Height, byte Red, byte Green, byte Blue);

/// <summary>A minimal NDI receiver used by the self test to look at this application's own NDI source, the way vMix would.</summary>
public sealed class NdiProbe : IDisposable
{
    private IntPtr _receiver;
    private IntPtr _namePtr, _addressPtr;

    private NdiProbe()
    {
    }

    /// <summary>All NDI sources visible now, as "MACHINE (name)".</summary>
    public static List<string> Sources(TimeSpan wait)
    {
        var names = new List<string>();
        var settings = new NdiNative.FindCreate { ShowLocalSources = true };
        IntPtr finder = NdiNative.find_create_v2(ref settings);
        if (finder == IntPtr.Zero) return names;
        try
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < wait)
            {
                NdiNative.find_wait_for_sources(finder, 500);
                names = Read(finder, out _);
                if (names.Count > 0 && clock.Elapsed > TimeSpan.FromSeconds(1)) break;
            }
            return names;
        }
        finally
        {
            NdiNative.find_destroy(finder);
        }
    }

    private static List<string> Read(IntPtr finder, out List<(IntPtr Name, IntPtr Address)> raw)
    {
        raw = [];
        var names = new List<string>();
        IntPtr array = NdiNative.find_get_current_sources(finder, out uint count);
        int size = Marshal.SizeOf<NdiNative.Source>();
        for (int i = 0; i < count; i++)
        {
            var source = Marshal.PtrToStructure<NdiNative.Source>(array + i * size);
            names.Add(Marshal.PtrToStringUTF8(source.Name) ?? string.Empty);
            raw.Add((source.Name, source.Address));
        }
        return names;
    }

    /// <summary>Connects to the first source whose full name contains <paramref name="nameContains"/>, or returns null if none shows up in time.</summary>
    public static NdiProbe? Connect(string nameContains, TimeSpan timeout)
    {
        var settings = new NdiNative.FindCreate { ShowLocalSources = true };
        IntPtr finder = NdiNative.find_create_v2(ref settings);
        if (finder == IntPtr.Zero) return null;
        try
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < timeout)
            {
                NdiNative.find_wait_for_sources(finder, 500);
                var names = Read(finder, out var raw);
                int index = names.FindIndex(n => n.Contains(nameContains, StringComparison.Ordinal));
                if (index < 0) continue;

                var probe = new NdiProbe
                {
                    // The finder owns its strings and frees them with it, so keep our own copies for the receiver.
                    _namePtr = Marshal.StringToCoTaskMemUTF8(names[index]),
                    _addressPtr = Marshal.StringToCoTaskMemUTF8(Marshal.PtrToStringUTF8(raw[index].Address) ?? string.Empty),
                };
                var create = new NdiNative.RecvCreate
                {
                    SourceToConnectTo = new NdiNative.Source { Name = probe._namePtr, Address = probe._addressPtr },
                    ColorFormat = 0,
                    Bandwidth = 100,
                    AllowVideoFields = false,
                    RecvName = IntPtr.Zero,
                };
                probe._receiver = NdiNative.recv_create_v3(ref create);
                if (probe._receiver == IntPtr.Zero)
                {
                    probe.Dispose();
                    return null;
                }
                return probe;
            }
            return null;
        }
        finally
        {
            NdiNative.find_destroy(finder);
        }
    }

    /// <summary>Waits for a video frame and reports its size and the colour of a pixel near the top-left corner (the centre of a test page often holds text).</summary>
    public NdiVideoSample? ReadVideo(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            var video = new NdiNative.VideoFrame();
            var audio = new NdiNative.AudioFrame();
            int type = NdiNative.recv_capture_v2(_receiver, ref video, ref audio, IntPtr.Zero, 200);
            if (type == NdiNative.FrameVideo)
            {
                try
                {
                    long offset = (long)(video.Yres / 20) * video.LineStrideInBytes + (long)(video.Xres / 20) * 4;
                    return new NdiVideoSample(video.Xres, video.Yres,
                        Marshal.ReadByte(video.Data, (int)offset + 2), Marshal.ReadByte(video.Data, (int)offset + 1), Marshal.ReadByte(video.Data, (int)offset));
                }
                finally
                {
                    NdiNative.recv_free_video_v2(_receiver, ref video);
                }
            }
            if (type == NdiNative.FrameAudio) NdiNative.recv_free_audio_v2(_receiver, ref audio);
        }
        return null;
    }

    /// <summary>Reads frames for <paramref name="duration"/> and returns the last one, so changes made a moment ago are what is reported.</summary>
    public NdiVideoSample? ReadLatestVideo(TimeSpan duration)
    {
        var clock = Stopwatch.StartNew();
        NdiVideoSample? last = null;
        while (clock.Elapsed < duration)
        {
            if (ReadVideo(TimeSpan.FromMilliseconds(300)) is { } sample) last = sample;
        }
        return last;
    }

    /// <summary>Listens for <paramref name="duration"/> and returns the peak of each channel in dBFS (null when no audio frame arrived).</summary>
    public (double Left, double Right, int Samples)? ReadAudioPeaks(TimeSpan duration)
    {
        var clock = Stopwatch.StartNew();
        float left = 0, right = 0;
        int samples = 0;
        while (clock.Elapsed < duration)
        {
            var video = new NdiNative.VideoFrame();
            var audio = new NdiNative.AudioFrame();
            int type = NdiNative.recv_capture_v2(_receiver, ref video, ref audio, IntPtr.Zero, 100);
            if (type == NdiNative.FrameVideo) NdiNative.recv_free_video_v2(_receiver, ref video);
            if (type != NdiNative.FrameAudio) continue;
            try
            {
                int n = audio.NoSamples;
                var channel = new float[n];
                for (int c = 0; c < Math.Min(2, audio.NoChannels); c++)
                {
                    Marshal.Copy(audio.Data + c * audio.ChannelStrideInBytes, channel, 0, n);
                    float peak = channel.Max(Math.Abs);
                    if (c == 0) left = Math.Max(left, peak); else right = Math.Max(right, peak);
                }
                samples += n;
            }
            finally
            {
                NdiNative.recv_free_audio_v2(_receiver, ref audio);
            }
        }
        if (samples == 0) return null;
        static double Db(float v) => v <= 1e-5f ? -90 : 20 * Math.Log10(v);
        return (Db(left), Db(right), samples);
    }

    public void Dispose()
    {
        if (_receiver != IntPtr.Zero) NdiNative.recv_destroy(_receiver);
        _receiver = IntPtr.Zero;
        if (_namePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(_namePtr);
        if (_addressPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(_addressPtr);
        _namePtr = _addressPtr = IntPtr.Zero;
    }
}
