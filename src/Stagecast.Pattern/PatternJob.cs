using Stagecast.Core.Frames;

namespace Stagecast.Pattern;

/// <summary>Renders the pattern frame by frame, with the beep, into an MP4 file.</summary>
public static class PatternJob
{
    /// <param name="progress">Receives the number of frames written so far.</param>
    public static void Run(PatternSettings s, string path, IProgress<long>? progress, CancellationToken cancel)
    {
        var pattern = new TestPattern(s.Title, s.Subtitle);
        using var writer = new Mp4Writer(path, s.Width, s.Height, s.Fps, withAudio: true);
        long total = s.TotalFrames, audioDone = 0;
        double amplitude = Math.Pow(10, s.LevelDb / 20.0), step = 2 * Math.PI * s.ToneHz / Mp4Writer.SampleRate;
        var pcm = new byte[(Mp4Writer.SampleRate / s.Fps + 2) * Mp4Writer.Channels * 2];

        for (long i = 0; i < total; i++)
        {
            cancel.ThrowIfCancellationRequested();

            // 0.1 ms past the exact time, so that rounding can never put the clock on the frame before.
            Frame frame = pattern.Render(s.Width, s.Height, s.Fps, TimeSpan.FromTicks(i * 10_000_000L / s.Fps + 1000));
            try { writer.WriteVideo(frame.Pixels, i); }
            finally { frame.Release(); }

            // The audio of this frame: the samples up to the end of the frame, counted from the start so nothing drifts.
            long end = (i + 1) * Mp4Writer.SampleRate / s.Fps;
            int count = (int)(end - audioDone);
            for (int n = 0; n < count; n++)
            {
                long sample = audioDone + n;
                bool on = TestPattern.BeepOn(TimeSpan.FromTicks(sample * 10_000_000L / Mp4Writer.SampleRate));
                short v = on ? (short)Math.Round(Math.Sin(step * (sample % Mp4Writer.SampleRate)) * amplitude * short.MaxValue) : (short)0;
                int o = n * 4;
                pcm[o] = pcm[o + 2] = (byte)v;
                pcm[o + 1] = pcm[o + 3] = (byte)(v >> 8);
            }

            writer.WriteAudio(pcm, count * 4, audioDone);
            audioDone = end;
            progress?.Report(i + 1);
        }

        writer.Finish();
    }
}
