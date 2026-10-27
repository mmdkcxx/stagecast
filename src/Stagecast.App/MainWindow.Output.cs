using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Stagecast.Core.Configuration;
using Stagecast.Core.Display;
using Stagecast.Core.Output;
using Stagecast.Core.Rendering;

namespace Stagecast.App;

public partial class MainWindow
{
    private const int HotkeyId = 0x5743;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000;

    private OutputConfig _outputConfig = new();
    private List<MonitorInfo> _monitors = [];
    private OutputWindow? _output;
    private bool _hotkeyRegistered;
    private bool _suppressScaleEvent;

    internal OutputWindow? Output => _output;

    internal IReadOnlyList<MonitorInfo> Monitors => _monitors;

    internal OutputConfig OutputSettings => _outputConfig;

    internal string OutputMessage => OutputNote.Text;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(OnWindowMessage);
        _hotkeyRegistered = RegisterHotKey(hwnd, HotkeyId, ModControl | ModAlt | ModNoRepeat, 'H');
        if (!_hotkeyRegistered)
            OutputNote.Text = "Pintasan darurat Ctrl+Alt+H dipakai aplikasi lain, jadi tidak aktif. Matikan output dari tombol di sini.";
        _monitors = MonitorCatalog.List();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _suppressScaleEvent = true;
        (_outputConfig.Scale switch { ScaleMode.Fit => ScaleFit, ScaleMode.OneToOne => ScaleOne, _ => ScaleSqueeze }).IsChecked = true;
        _suppressScaleEvent = false;
        OnTopBox.IsChecked = _outputConfig.AlwaysOnTop;
        ShowMonitor();
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            if (_output is not null) StopOutput("Output dimatikan dengan pintasan darurat.");
        }
        return IntPtr.Zero;
    }

    private void ShutdownOutput()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        StopOutput(null);
        if (_hotkeyRegistered) UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
        _hotkeyRegistered = false;
    }

    private MonitorInfo? ControlMonitor => MonitorCatalog.Of(_monitors, new WindowInteropHelper(this).Handle);

    /// <summary>The chosen monitor, or the first one that is not the control monitor when nothing (valid) was chosen.</summary>
    private MonitorInfo? TargetMonitor => OutputGuard.Find(_monitors, _outputConfig.MonitorId) ?? OutputGuard.Default(_monitors, ControlMonitor);

    private void ShowMonitor()
    {
        var target = TargetMonitor;
        bool missing = _outputConfig.MonitorId is not null && OutputGuard.Find(_monitors, _outputConfig.MonitorId) is null;
        MonitorButton.Content = target is null ? "Tidak ada monitor" : missing ? $"{target.Label} (monitor pilihan tidak ada)" : target.Label;
        ShowScaleInfo();
    }

    private void ShowScaleInfo()
    {
        var target = TargetMonitor;
        if (target is null || _engine is null) { ScaleInfo.Text = string.Empty; return; }
        var r = ScaleMath.Compute(_engine.Resolution, target.Size, _outputConfig.Scale);
        ScaleInfo.Text = _outputConfig.Scale switch
        {
            ScaleMode.Squeeze when Math.Abs(r.ScaleX - r.ScaleY) < 1e-9 => $"Gambar {_engine.Resolution.Width}×{_engine.Resolution.Height} dikirim ke monitor {target.Width}×{target.Height} dengan skala {r.ScaleX:P2}.",
            ScaleMode.Squeeze => $"Diperas ke {target.Width}×{target.Height}: lebar {r.ScaleX:P2}, tinggi {r.ScaleY:P2}. Di Resolume/vMix lebarkan kembali ke {r.RestoreX:P2}"
                + (Math.Abs(r.RestoreY - 1) < 1e-9 ? "." : $" dan tinggi {r.RestoreY:P2}."),
            ScaleMode.Fit => $"Skala seragam {r.ScaleX:P2} ({r.DestWidth:0}×{r.DestHeight:0}); sisa layar hitam.",
            _ => r.Cropped ? $"1:1. Gambar lebih besar dari monitor, bagian yang lewat terpotong ({_engine.Resolution.Width}×{_engine.Resolution.Height} di {target.Width}×{target.Height})." : "1:1, satu piksel render per piksel monitor.",
        };
    }

    private void OnMonitorMenu(object sender, RoutedEventArgs e)
    {
        _monitors = MonitorCatalog.List();
        var menu = new ContextMenu { PlacementTarget = MonitorButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MinWidth = MonitorButton.ActualWidth };
        var current = TargetMonitor;
        foreach (var monitor in _monitors)
        {
            var item = new MenuItem { Header = monitor.Label, IsCheckable = true, IsChecked = current?.Id == monitor.Id };
            item.Click += (_, _) => ChooseMonitor(monitor);
            menu.Items.Add(item);
        }
        if (_monitors.Count == 0) menu.Items.Add(new MenuItem { Header = "Tidak ada monitor", IsEnabled = false });
        menu.IsOpen = true;
    }

    internal void ChooseMonitor(MonitorInfo monitor)
    {
        _outputConfig = new OutputConfig { MonitorId = monitor.Id, Scale = _outputConfig.Scale, AlwaysOnTop = _outputConfig.AlwaysOnTop };
        ShowMonitor();
        ReconfigureOutput();
        SchedulePersist();
    }

    private void OnScaleChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressScaleEvent) return;
        SetScale(ScaleFit.IsChecked == true ? ScaleMode.Fit : ScaleOne.IsChecked == true ? ScaleMode.OneToOne : ScaleMode.Squeeze);
    }

    internal void SetScale(ScaleMode mode)
    {
        _suppressScaleEvent = true;
        (mode switch { ScaleMode.Fit => ScaleFit, ScaleMode.OneToOne => ScaleOne, _ => ScaleSqueeze }).IsChecked = true;
        _suppressScaleEvent = false;
        _outputConfig = new OutputConfig { MonitorId = _outputConfig.MonitorId, Scale = mode, AlwaysOnTop = _outputConfig.AlwaysOnTop };
        ShowScaleInfo();
        ReconfigureOutput();
        SchedulePersist();
    }

    private void OnOnTopChanged(object sender, RoutedEventArgs e) => SetAlwaysOnTop(OnTopBox.IsChecked == true);

    internal void SetAlwaysOnTop(bool on)
    {
        if (OnTopBox.IsChecked != on) { OnTopBox.IsChecked = on; return; }
        if (_outputConfig.AlwaysOnTop == on) return;
        _outputConfig = new OutputConfig { MonitorId = _outputConfig.MonitorId, Scale = _outputConfig.Scale, AlwaysOnTop = on };
        ReconfigureOutput();
        SchedulePersist();
    }

    private void ReconfigureOutput()
    {
        if (_output is null) return;
        if (TargetMonitor is { } target) _output.Configure(target, _outputConfig.Scale, _outputConfig.AlwaysOnTop);
    }

    // ---- Freeze / Blackout ----

    internal GateMode Gate => _engine?.Hub.Gate ?? GateMode.Live;

    /// <summary>Holds what leaves the application (screen, later NDI and OMT). The page, the preview and the clicks keep working.</summary>
    internal void SetGate(GateMode mode)
    {
        if (_engine is null) return;
        _engine.Hub.Gate = mode;
        FreezeButton.IsChecked = mode == GateMode.Freeze;
        BlackoutButton.IsChecked = mode == GateMode.Blackout;
        PagePreviewView.Gate = mode;
        _logger.LogInformation("Output gate: {Mode}", mode);
    }

    private void OnFreeze(object sender, RoutedEventArgs e) => SetGate(Gate == GateMode.Freeze ? GateMode.Live : GateMode.Freeze);

    private void OnBlackout(object sender, RoutedEventArgs e) => SetGate(Gate == GateMode.Blackout ? GateMode.Live : GateMode.Blackout);

    private void OnToggleOutput(object sender, RoutedEventArgs e)
    {
        if (_output is null) StartOutput();
        else StopOutput(null);
    }

    /// <summary>Shows the picture on the chosen monitor. Asks first when that would cover this window.</summary>
    internal bool StartOutput()
    {
        if (_engine is null || _output is not null) return _output is not null;
        _monitors = MonitorCatalog.List();
        if (TargetMonitor is not { } target)
        {
            OutputNote.Text = "Tidak ada monitor yang bisa dipakai.";
            return false;
        }
        var warning = OutputGuard.Check(_monitors, target, ControlMonitor);
        if (warning != OutputWarning.None && !Confirm(OutputGuard.Message(warning))) return false;

        _output = new OutputWindow(target, _outputConfig.Scale, _outputConfig.AlwaysOnTop, _engine.Resolution);
        _engine.Hub.AddSink(_output);
        _output.Show();
        OutputButton.Content = "Matikan output";
        OutputNote.Text = $"Output nyala di {target.Label}. Matikan darurat: Ctrl+Alt+H.";
        _logger.LogInformation("Screen output on: {Monitor} {Mode} onTop={OnTop}", target.Id, _outputConfig.Scale, _outputConfig.AlwaysOnTop);
        ShowControls();
        return true;
    }

    internal void StopOutput(string? message)
    {
        if (_output is null) return;
        _engine?.Hub.RemoveSink(_output);
        _output.Close();
        _output = null;
        OutputButton.Content = "Nyalakan output";
        OutputNote.Text = message ?? "Matikan darurat dari mana saja: Ctrl+Alt+H.";
        _logger.LogInformation("Screen output off");
        if (_engine is not null) ShowControls();
    }

    /// <summary>A monitor was plugged in, unplugged or changed: follow it, or switch the output off when its monitor is gone.</summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(() => RefreshMonitors());

    internal void RefreshMonitors()
    {
        _monitors = MonitorCatalog.List();
        ShowMonitor();
        if (_output is null) return;
        var still = OutputGuard.Find(_monitors, _output.Monitor.Id);
        if (still is null) StopOutput($"Monitor output ({_output.Monitor.Label}) tidak ditemukan lagi, jadi output dimatikan. Pilih monitor lalu nyalakan lagi.");
        else _output.Configure(still, _outputConfig.Scale, _outputConfig.AlwaysOnTop);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
