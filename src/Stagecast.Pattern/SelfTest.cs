using System.IO;
using System.Text;
using Vortice.MediaFoundation;

namespace Stagecast.Pattern;

/// <summary>Renders short videos with different sizes and checks that each file looks like an MP4 with both an H.264 and an AAC track.</summary>
internal static class SelfTest
{
    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var log = new StringBuilder();
        bool failed = false;
        (int W, int H, int Fps, int Sec)[] cases = [(640, 360, 25, 3), (1920, 1080, 50, 3), (3840, 2160, 30, 2), (3840, 1080, 60, 2), (1080, 1920, 30, 2), (1000, 1000, 24, 2)];
        foreach (var c in cases)
        {
            string path = Path.Combine(folder, $"t_{c.W}x{c.H}_{c.Fps}.mp4");
            var s = new PatternSettings { Width = c.W, Height = c.H, Fps = c.Fps, Seconds = c.Sec, Title = "UJI SUARA & GAMBAR", Subtitle = "KAMERA 3 (UTAMA)" };
            try
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                PatternJob.Run(s, path, null, CancellationToken.None);
                byte[] bytes = File.ReadAllBytes(path);
                string text = Encoding.Latin1.GetString(bytes);
                bool good = text.Contains("ftyp") && text.Contains("moov") && text.Contains("avc1") && text.Contains("mp4a") && bytes.Length > 20_000;
                log.AppendLine($"{(good ? "ok  " : "FAIL")} {c.W}x{c.H} {c.Fps}fps {c.Sec}s -> {bytes.Length:N0} bytes in {t.Elapsed.TotalSeconds:0.0}s");
                failed |= !good;
                string? readBack = ReadBack(path, s);
                if (readBack is not null)
                {
                    log.AppendLine($"FAIL {readBack}");
                    failed = true;
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"FAIL {c.W}x{c.H} {c.Fps}fps: {ex.GetType().Name}: {ex.Message}");
                failed = true;
            }
        }

        log.AppendLine(failed ? "selftest FAILED" : "selftest ok");
        File.WriteAllText(Path.Combine(folder, "selftest.log"), log.ToString());
        return failed ? 1 : 0;
    }

    // Decodes the file again: the first picture must show the white colour bar and the first audio block must contain the beep, a block half a second later must be silent.
    private static string? ReadBack(string path, PatternSettings s)
    {
        using IMFAttributes attributes = MediaFactory.MFCreateAttributes(1);
        attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);
        using IMFSourceReader reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);

        using (IMFMediaType video = MediaFactory.MFCreateMediaType())
        {
            video.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            video.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, video);
        }

        using (IMFMediaType audio = MediaFactory.MFCreateMediaType())
        {
            audio.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            audio.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, audio);
        }

        // Picture: the middle of the top-left colour bar is white. The decoder may pad each row, which the media type tells us.
        int stride;
        using (IMFMediaType current = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
            stride = (int)current.GetUInt32(MediaTypeAttributeKeys.DefaultStride);
        IMFSample? picture = reader.ReadSample(SourceReaderIndex.FirstVideoStream, 0, out _, out _, out _);
        if (picture is null) return $"{path}: no picture could be read";
        using (picture)
        {
            using IMFMediaBuffer buffer = picture.ConvertToContiguousBuffer();
            buffer.Lock(out IntPtr data, out _, out int length);
            try
            {
                var all = new byte[length];
                System.Runtime.InteropServices.Marshal.Copy(data, all, 0, length);
                // The decoder pads width and height up to a multiple of 16 and DefaultStride does not say so; the pitch is the buffer length over the padded height.
                int usable = Math.Abs(stride);
                foreach (int rows in new[] { s.Height, (s.Height + 15) / 16 * 16 })
                    if (length % rows == 0 && length / rows >= s.Width * 4 && length / rows % 4 == 0) { usable = length / rows; break; }
                stride = usable;
                var bmp = System.Windows.Media.Imaging.BitmapSource.Create(s.Width, s.Height, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, all, usable);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var f = File.Create(path + ".png")) enc.Save(f);
                if (stride <= 0) return $"{path}: stride {stride} (rows not top-down), length {length}";
                int x = s.Width / 14, y = s.Height / 5;
                int o = y * stride + x * 4;
                if (all[o] < 230 || all[o + 1] < 230 || all[o + 2] < 230) return $"{path}: the white bar is not white ({all[o + 2]},{all[o + 1]},{all[o]}) stride {stride} length {length} rows {length / (double)stride:0.0}";
            }
            finally { buffer.Unlock(); }
        }

        // Sound: peak of each audio block by its start time.
        double early = 0, late = 0;
        long samplesSeen = 0;
        while (true)
        {
            IMFSample? block = reader.ReadSample(SourceReaderIndex.FirstAudioStream, 0, out _, out SourceReaderFlag flags, out long time);
            if ((flags & SourceReaderFlag.EndOfStream) != 0 || block is null) break;
            using (block)
            {
                using IMFMediaBuffer buffer = block.ConvertToContiguousBuffer();
                buffer.Lock(out IntPtr data, out _, out int length);
                try
                {
                    var pcm = new short[length / 2];
                    System.Runtime.InteropServices.Marshal.Copy(data, pcm, 0, pcm.Length);
                    double peak = pcm.Length == 0 ? 0 : pcm.Max(v => Math.Abs((double)v)) / short.MaxValue;
                    double seconds = time / 1e7;
                    if (seconds < 0.08) early = Math.Max(early, peak);
                    else if (seconds > 0.2 && seconds < 0.9) late = Math.Max(late, peak);
                    samplesSeen += pcm.Length / 2;
                }
                finally { buffer.Unlock(); }
            }
        }

        double want = Math.Pow(10, s.LevelDb / 20.0);
        if (early < want * 0.5) return $"{path}: no beep at the start (peak {early:0.000}, wanted about {want:0.000})";
        if (late > 0.01) return $"{path}: sound where there should be silence (peak {late:0.000})";
        if (Math.Abs(samplesSeen - (long)s.Seconds * Mp4Writer.SampleRate) > Mp4Writer.SampleRate / 10) return $"{path}: audio length is off ({samplesSeen} samples)";
        return null;
    }
}
