using System.Diagnostics;
using libomtnet;

namespace Stagecast.Rendering.Omt;

public readonly record struct OmtVideoSample(int Width, int Height, byte Red, byte Green, byte Blue);

/// <summary>A minimal OMT receiver used by the self test to look at this application's own OMT source, the way vMix would.</summary>
public sealed class OmtProbe : IDisposable
{
    private readonly OMTReceive _receive;

    private OmtProbe(OMTReceive receive) => _receive = receive;

    /// <summary>Full names ("MACHINE (name)") of the OMT sources on the network.</summary>
    public static string[] Sources() => OMTDiscovery.GetInstance().GetAddresses();

    /// <summary>Waits until a source whose full name contains <paramref name="nameContains"/> is announced, then connects to it.</summary>
    public static OmtProbe? Connect(string nameContains, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            string? full = Sources().FirstOrDefault(s => s.Contains(nameContains, StringComparison.Ordinal));
            if (full is not null)
                return new OmtProbe(new OMTReceive(full, OMTFrameType.Video | OMTFrameType.Audio, OMTPreferredVideoFormat.BGRA, OMTReceiveFlags.None));
            Thread.Sleep(250);
        }
        return null;
    }

    /// <summary>Reads for <paramref name="duration"/> and returns the last picture (size and a pixel near the top-left corner).</summary>
    public OmtVideoSample? ReadLatestVideo(TimeSpan duration)
    {
        var clock = Stopwatch.StartNew();
        OmtVideoSample? last = null;
        while (clock.Elapsed < duration)
        {
            var frame = new OMTMediaFrame();
            if (!_receive.Receive(OMTFrameType.Video, 200, ref frame) || frame.Type != OMTFrameType.Video) continue;
            long offset = (long)(frame.Height / 20) * frame.Stride + (long)(frame.Width / 20) * 4;
            last = new OmtVideoSample(frame.Width, frame.Height,
                System.Runtime.InteropServices.Marshal.ReadByte(frame.Data, (int)offset + 2),
                System.Runtime.InteropServices.Marshal.ReadByte(frame.Data, (int)offset + 1),
                System.Runtime.InteropServices.Marshal.ReadByte(frame.Data, (int)offset));
        }
        return last;
    }

    /// <summary>Reads audio for <paramref name="duration"/> and returns the peak of each channel in dBFS (null when none arrived).</summary>
    public (double Left, double Right, int Samples)? ReadAudioPeaks(TimeSpan duration)
    {
        // Audio that arrived while we were looking at pictures is still queued in the receiver; throw it away so only new sound is measured.
        var stale = new OMTMediaFrame();
        while (_receive.Receive(OMTFrameType.Audio, 0, ref stale) && stale.Type == OMTFrameType.Audio)
        {
        }
        var clock = Stopwatch.StartNew();
        float left = 0, right = 0;
        int samples = 0;
        while (clock.Elapsed < duration)
        {
            var frame = new OMTMediaFrame();
            if (!_receive.Receive(OMTFrameType.Audio, 100, ref frame) || frame.Type != OMTFrameType.Audio) continue;
            int n = frame.SamplesPerChannel;
            var channel = new float[n];
            for (int c = 0; c < Math.Min(2, frame.Channels); c++)
            {
                System.Runtime.InteropServices.Marshal.Copy(frame.Data + c * n * 4, channel, 0, n);
                float peak = channel.Max(Math.Abs);
                if (c == 0) left = Math.Max(left, peak); else right = Math.Max(right, peak);
            }
            samples += n;
        }
        if (samples == 0) return null;
        static double Db(float v) => v <= 1e-5f ? -90 : 20 * Math.Log10(v);
        return (Db(left), Db(right), samples);
    }

    public void Dispose() => _receive.Dispose();
}
