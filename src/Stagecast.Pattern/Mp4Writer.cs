using Vortice.MediaFoundation;

namespace Stagecast.Pattern;

/// <summary>
/// Writes an MP4 (H.264 video, AAC audio) through the Windows Media Foundation sink writer, so nothing has to be installed next to the app.
/// Video goes in as top-down BGRA frames, audio as 16-bit stereo PCM at 48 kHz; the writer encodes both.
/// </summary>
public sealed class Mp4Writer : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private static int _started;

    private readonly IMFSinkWriter _writer;
    private readonly int _videoStream, _audioStream, _frameBytes;
    private readonly int _fps;
    private bool _finished;

    public Mp4Writer(string path, int width, int height, int fps, bool withAudio)
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) MediaFactory.MFStartup().CheckError();
        _fps = fps;
        _frameBytes = width * height * 4;
        WithAudio = withAudio;

        using IMFAttributes attributes = MediaFactory.MFCreateAttributes(2);
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
        attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4);
        _writer = MediaFactory.MFCreateSinkWriterFromURL(path, null, attributes);

        // Video: H.264 out, BGRA in.
        uint bitrate = (uint)VideoBitrate(width, height, fps);
        using (IMFMediaType output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate);
            output.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            output.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            output.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            output.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            _videoStream = _writer.AddStream(output);
        }

        using (IMFMediaType input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            input.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            input.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            input.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            input.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(width * 4)); // positive: the rows run top-down
            _writer.SetInputMediaType(_videoStream, input, null);
        }

        if (withAudio)
        {
            using (IMFMediaType output = MediaFactory.MFCreateMediaType())
            {
                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                output.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                output.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                output.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)SampleRate);
                output.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)Channels);
                output.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 24000u); // 192 kbit/s
                output.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
                _audioStream = _writer.AddStream(output);
            }

            using (IMFMediaType input = MediaFactory.MFCreateMediaType())
            {
                input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                input.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                input.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                input.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)SampleRate);
                input.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)Channels);
                input.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(Channels * 2));
                input.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(SampleRate * Channels * 2));
                _writer.SetInputMediaType(_audioStream, input, null);
            }
        }

        _writer.BeginWriting();
    }

    public bool WithAudio { get; }

    /// <summary>About 0.12 bits per pixel keeps the sharp edges of the pattern clean.</summary>
    public static long VideoBitrate(int width, int height, int fps) => Math.Clamp((long)(width * (long)height * fps * 0.12), 2_000_000L, 100_000_000L);

    /// <summary>Adds frame number <paramref name="index"/> (BGRA, top-down, tightly packed).</summary>
    public void WriteVideo(byte[] bgra, long index)
    {
        long start = index * 10_000_000L / _fps, end = (index + 1) * 10_000_000L / _fps;
        WriteSample(_videoStream, bgra, _frameBytes, start, end - start);
    }

    /// <summary>Adds 16-bit stereo PCM that begins at <paramref name="firstSample"/> samples from the start.</summary>
    public void WriteAudio(byte[] pcm, int bytes, long firstSample)
    {
        long start = firstSample * 10_000_000L / SampleRate;
        long duration = (long)bytes / (Channels * 2) * 10_000_000L / SampleRate;
        WriteSample(_audioStream, pcm, bytes, start, duration);
    }

    /// <summary>Closes the file; without this call the MP4 has no index and does not play.</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        _writer.Finalize();
    }

    public void Dispose()
    {
        if (!_finished)
        {
            _finished = true;
            try { _writer.Finalize(); } catch (Exception) { /* cancelled or failed: the partial file is deleted by the caller */ }
        }

        _writer.Dispose();
    }

    private void WriteSample(int stream, byte[] data, int length, long time, long duration)
    {
        using IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(length);
        buffer.Lock(out IntPtr target, out _, out _);
        try { System.Runtime.InteropServices.Marshal.Copy(data, 0, target, length); }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = length;
        using IMFSample sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = duration;
        _writer.WriteSample(stream, sample);
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;
}
