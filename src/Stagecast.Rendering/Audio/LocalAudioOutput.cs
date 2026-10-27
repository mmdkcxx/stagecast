using NAudio.CoreAudioApi;
using NAudio.Wave;
using Stagecast.Core.Audio;

namespace Stagecast.Rendering.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>Plays the program audio on a chosen Windows output device (WASAPI shared mode; Windows converts to the device format).</summary>
public sealed class LocalAudioOutput : IAudioSink, IDisposable
{
    private const double MaxBufferedMs = 150;

    private readonly object _gate = new();
    private WasapiOut? _out;
    private BufferedWaveProvider? _buffer;
    private byte[] _bytes = new byte[AudioFormat.SampleRate / 10 * 8];

    string IAudioSink.Name => "local";

    /// <summary>Why playback is not running (device missing or failed), or null.</summary>
    public string? Error { get; private set; }

    public string? DeviceName { get; private set; }

    public bool IsPlaying
    {
        get { lock (_gate) return _out is not null; }
    }

    public static List<AudioDeviceInfo> Devices()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try
        {
            using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = def.ID;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // no default device
        }
        var list = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device) list.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
        }
        return list;
    }

    /// <summary>Starts playing on <paramref name="deviceId"/>, or on the Windows default device when null. Returns false and sets <see cref="Error"/> on failure.</summary>
    public bool Open(string? deviceId)
    {
        Close();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = deviceId is null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(deviceId);
            if (device.State != DeviceState.Active) throw new InvalidOperationException("perangkat audio tidak aktif");
            var buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(AudioFormat.SampleRate, AudioFormat.Channels))
            {
                BufferDuration = TimeSpan.FromMilliseconds(300),
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 60);
            output.PlaybackStopped += OnStopped;
            output.Init(buffer);
            output.Play();
            lock (_gate)
            {
                _buffer = buffer;
                _out = output;
            }
            DeviceName = device.FriendlyName;
            Error = null;
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException or NAudio.MmException)
        {
            Error = $"Audio lokal gagal dibuka: {ex.Message}";
            DeviceName = null;
            return false;
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) Error = $"Audio lokal berhenti: {e.Exception.Message}";
        lock (_gate)
        {
            _out = null;
            _buffer = null;
        }
    }

    public void Close()
    {
        WasapiOut? output;
        lock (_gate)
        {
            output = _out;
            _out = null;
            _buffer = null;
        }
        if (output is null) return;
        output.PlaybackStopped -= OnStopped;
        try
        {
            output.Stop();
        }
        finally
        {
            output.Dispose();
        }
    }

    public void Write(float[] samples, int frames)
    {
        BufferedWaveProvider? buffer;
        lock (_gate) buffer = _buffer;
        if (buffer is null) return;
        // The hub clock and the device clock drift apart a little; never let the queue grow, a short gap is better than a growing delay.
        if (buffer.BufferedDuration.TotalMilliseconds > MaxBufferedMs) return;
        int bytes = frames * AudioFormat.Channels * 4;
        if (_bytes.Length < bytes) _bytes = new byte[bytes];
        Buffer.BlockCopy(samples, 0, _bytes, 0, bytes);
        buffer.AddSamples(_bytes, 0, bytes);
    }

    public void Dispose() => Close();
}
