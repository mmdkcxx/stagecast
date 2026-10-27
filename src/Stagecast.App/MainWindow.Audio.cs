using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Audio;
using Stagecast.Core.Configuration;
using Stagecast.Core.Output;
using Stagecast.Rendering.Audio;

namespace Stagecast.App;

public partial class MainWindow
{
    private AudioConfig _audioConfig = new();
    private AudioHub? _audio;
    private BrowserAudioCapture? _audioCapture;
    private LocalAudioOutput? _localAudio;
    private DispatcherTimer? _audioTimer;
    private int _audioTick;
    private bool _suppressVolumeEvent;

    internal AudioHub? Audio => _audio;

    internal BrowserAudioCapture? AudioCapture => _audioCapture;

    internal LocalAudioOutput? LocalAudio => _localAudio;

    internal AudioConfig AudioSettings => _audioConfig;

    private void StartAudio()
    {
        if (_engine is null || _audio is not null) return;
        _audioConfig = _config.Audio;
        _audio = new AudioHub { Volume = _audioConfig.Volume, Muted = _audioConfig.Muted, Silenced = () => _engine?.Hub.Gate == GateMode.Freeze };
        _audioCapture = new BrowserAudioCapture(_audio, _context.LoggerFactory.CreateLogger("Audio"));
        _localAudio = new LocalAudioOutput();
        _audio.AddSink(_localAudio);
        _localAudio.Open(_audioConfig.DeviceId);
        _audio.Start();

        _suppressVolumeEvent = true;
        VolumeSlider.Value = _audioConfig.Volume * 100;
        _suppressVolumeEvent = false;
        MuteButton.IsChecked = _audioConfig.Muted;
        ShowAudioDevice();
        SyncAudioSources();
        InitNdiCard();
        InitOmtCard();

        _audioTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _audioTimer.Tick += (_, _) => OnAudioTick();
        _audioTimer.Start();
    }

    private void ShutdownAudio()
    {
        _audioTimer?.Stop();
        _audio?.Dispose();
        _localAudio?.Dispose();
        _audioCapture?.Dispose();
        _audio = null;
    }

    /// <summary>Makes the captures match the browser processes that exist now (a new profile starts a new process).</summary>
    internal void SyncAudioSources()
    {
        if (_engine is null || _audioCapture is null) return;
        try
        {
            _audioCapture.Sync(_engine.BrowserProcessIds);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Reading the browser processes failed");
        }
    }

    private void OnAudioTick()
    {
        if (_audio is null) return;
        if (++_audioTick % 40 == 0) SyncAudioSources();
        if (_audioTick % 20 == 0) { ShowNdiStatus(); ShowOmtStatus(); }

        var levels = _audio.Levels;
        double width = (MeterLeft.Parent as Grid)?.ActualWidth ?? 0;
        MeterLeft.Width = Math.Max(0, width * MeterFraction(levels.LeftDb));
        MeterRight.Width = Math.Max(0, width * MeterFraction(levels.RightDb));

        string text;
        if (_audio.Muted) text = "SENYAP · BISU";
        else if (_audio.IsSilenced) text = "SENYAP · FREEZE";
        else text = $"L {Format(levels.LeftDb)}  R {Format(levels.RightDb)} dBFS";
        if (_audio.ToneActive) text += $"  · NADA UJI ({ToneName(_audio.ToneChannel)})";
        AudioStatus.Text = text;
        ToneButton.Content = _audio.ToneActive ? "Hentikan nada uji" : "Nada uji";

        if (_localAudio?.Error is { } error) AudioNote.Text = error;
    }

    /// <summary>-60 dBFS and below is an empty bar, 0 dBFS a full one.</summary>
    private static double MeterFraction(double db) => Math.Clamp((db + 60) / 60, 0, 1);

    private static string Format(double db) => db <= LevelMeter.FloorDb + 0.5 ? " -∞" : $"{db,3:0}";

    private static string ToneName(ToneChannel channel) => channel switch { ToneChannel.Left => "kiri", ToneChannel.Right => "kanan", _ => "kiri + kanan" };

    private void ShowAudioDevice()
    {
        string name = _localAudio?.DeviceName ?? "tidak ada";
        AudioDeviceButton.Content = _audioConfig.DeviceId is null ? $"Bawaan Windows ({name})" : name;
    }

    private void OnAudioDeviceMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AudioDeviceButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MinWidth = AudioDeviceButton.ActualWidth };
        var auto = new MenuItem { Header = "Bawaan Windows", IsCheckable = true, IsChecked = _audioConfig.DeviceId is null };
        auto.Click += (_, _) => ChooseAudioDevice(null);
        menu.Items.Add(auto);
        foreach (var device in LocalAudioOutput.Devices())
        {
            var item = new MenuItem { Header = device.Name, IsCheckable = true, IsChecked = device.Id == _audioConfig.DeviceId };
            item.Click += (_, _) => ChooseAudioDevice(device.Id);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    internal bool ChooseAudioDevice(string? deviceId)
    {
        if (_localAudio is null) return false;
        _audioConfig = new AudioConfig { DeviceId = deviceId, Volume = _audioConfig.Volume, Muted = _audioConfig.Muted };
        bool opened = _localAudio.Open(deviceId);
        AudioNote.Text = opened ? "Hanya tab yang tayang yang terdengar. Saat Freeze audio senyap; Blackout tidak memengaruhi audio." : _localAudio.Error ?? string.Empty;
        ShowAudioDevice();
        SchedulePersist();
        return opened;
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressVolumeEvent) return;
        SetVolume(e.NewValue / 100);
    }

    internal void SetVolume(double volume)
    {
        volume = Math.Clamp(volume, 0, 1);
        if (_audio is not null) _audio.Volume = volume;
        _audioConfig = new AudioConfig { DeviceId = _audioConfig.DeviceId, Volume = volume, Muted = _audioConfig.Muted };
        SchedulePersist();
    }

    private void OnMuteClick(object sender, RoutedEventArgs e) => SetMuted(MuteButton.IsChecked == true);

    internal void SetMuted(bool muted)
    {
        if (_audio is not null) _audio.Muted = muted;
        if (MuteButton.IsChecked != muted) MuteButton.IsChecked = muted;
        _audioConfig = new AudioConfig { DeviceId = _audioConfig.DeviceId, Volume = _audioConfig.Volume, Muted = muted };
        SchedulePersist();
    }

    private void OnToneMenu(object sender, RoutedEventArgs e)
    {
        if (_audio is null) return;
        if (_audio.ToneActive) { StopTone(); return; }
        var menu = new ContextMenu { PlacementTarget = ToneButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var channel in new[] { ToneChannel.Both, ToneChannel.Left, ToneChannel.Right })
        {
            var item = new MenuItem { Header = $"Kanal {ToneName(channel)}" };
            item.Click += (_, _) => StartTone(channel);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>The tone replaces the program audio, so it reaches NDI (and OMT later) too; those ask first when they are on.</summary>
    internal bool StartTone(ToneChannel channel)
    {
        if (_audio is null) return false;
        if (_ndi is not null && !Confirm("NDI sedang nyala. Nada uji akan terkirim ke semua penerima NDI (misalnya vMix) dan bisa ikut tersiar. Lanjutkan?")) return false;
        if (_omt is not null && !Confirm("OMT sedang nyala. Nada uji akan terkirim ke semua penerima OMT (vMix) dan bisa ikut tersiar. Lanjutkan?")) return false;
        _audio.StartTone(channel);
        return true;
    }

    internal void StopTone() => _audio?.StopTone();
}
