using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Configuration;
using Stagecast.Core.Output;
using Stagecast.Rendering.Ndi;

namespace Stagecast.App;

public partial class MainWindow
{
    private NdiConfig _ndiConfig = new();
    private NdiOutput? _ndi;
    private string? _ndiError;

    internal NdiOutput? Ndi => _ndi;

    internal NdiConfig NdiSettings => _ndiConfig;

    internal string NdiMessage => NdiStatus.Text;

    private void InitNdiCard()
    {
        NdiNameBox.Text = _ndiConfig.Name;
        NdiAlphaBox.IsChecked = _ndiConfig.Alpha;
        ApplyAlpha();
        ShowNdiStatus();
    }

    /// <summary>Bars around documents are transparent when either network output sends alpha; the screen output stays black.</summary>
    private void ApplyAlpha()
    {
        if (_engine is not null) _engine.TransparentBars = _ndiConfig.Alpha || _omtConfig.Alpha;
    }

    private void OnNdiAlphaChanged(object sender, RoutedEventArgs e) => SetNdiAlpha(NdiAlphaBox.IsChecked == true);

    /// <summary>Chooses whether NDI sends transparency. Works while the output runs; only the choice is saved.</summary>
    internal void SetNdiAlpha(bool alpha)
    {
        if (_ndiConfig.Alpha == alpha) return;
        _ndiConfig = new NdiConfig { Name = _ndiConfig.Name, Alpha = alpha };
        if (NdiAlphaBox.IsChecked != alpha) NdiAlphaBox.IsChecked = alpha;
        if (_ndi is not null) _ndi.SendAlpha = alpha;
        ApplyAlpha();
        SchedulePersist();
    }

    private void OnToggleNdi(object sender, RoutedEventArgs e)
    {
        if (_ndi is null) StartNdi();
        else StopNdi(null);
    }

    /// <summary>Sends the program picture and audio as an NDI source under the saved name.</summary>
    internal bool StartNdi()
    {
        if (_engine is null || _audio is null) return false;
        if (_ndi is not null) return true;
        var error = SourceNameRules.Validate(_ndiConfig.Name);
        if (error != SourceNameError.None)
        {
            _ndiError = SourceNameRules.Message(error);
            ShowNdiStatus();
            return false;
        }
        var ndi = new NdiOutput(_context.LoggerFactory.CreateLogger("Ndi"), () => _engine?.Fps ?? 60);
        if (ndi.Start(_ndiConfig.Name.Trim()) is { } problem)
        {
            _ndiError = problem;
            ShowNdiStatus();
            return false;
        }
        ndi.SendAlpha = _ndiConfig.Alpha;
        _ndi = ndi;
        _ndiError = null;
        _engine.Hub.AddSink(ndi);
        _audio.AddSink(ndi);
        NdiButton.Content = "Matikan NDI";
        ShowNdiStatus();
        ShowControls();
        return true;
    }

    internal void StopNdi(string? message)
    {
        if (_ndi is null) return;
        _engine?.Hub.RemoveSink(_ndi);
        _audio?.RemoveSink(_ndi);
        _ndi.Dispose();
        _ndi = null;
        _ndiError = message;
        NdiButton.Content = "Nyalakan NDI";
        ShowNdiStatus();
        if (_engine is not null) ShowControls();
    }

    private void OnNdiNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        RenameNdi(NdiNameBox.Text);
        e.Handled = true;
    }

    private void OnNdiRename(object sender, RoutedEventArgs e) => RenameNdi(NdiNameBox.Text);

    /// <summary>NDI fixes the name when the sender is created, so renaming a running sender closes it and makes a new one: receivers drop and must pick it again.</summary>
    internal bool RenameNdi(string name)
    {
        name = name.Trim();
        var error = SourceNameRules.Validate(name);
        if (error != SourceNameError.None)
        {
            _ndiError = SourceNameRules.Message(error);
            ShowNdiStatus();
            return false;
        }
        if (name == _ndiConfig.Name) { _ndiError = null; ShowNdiStatus(); return true; }
        bool running = _ndi is not null;
        if (running && !Confirm("Mengganti nama membuat sumber NDI dibuat ulang. Penerima seperti vMix akan terputus dan harus memilih sumber yang baru. Lanjutkan?"))
        {
            NdiNameBox.Text = _ndiConfig.Name;
            return false;
        }
        _ndiConfig = new NdiConfig { Name = name, Alpha = _ndiConfig.Alpha };
        NdiNameBox.Text = name;
        _ndiError = null;
        SchedulePersist();
        if (!running) { ShowNdiStatus(); return true; }
        StopNdi(null);
        return StartNdi();
    }

    private void ShowNdiStatus()
    {
        if (_ndi is { } ndi)
            NdiStatus.Text = $"Nyala sebagai “{ndi.SourceName}” · {ndi.Connections} penerima · {ndi.VideoSent} frame terkirim";
        else
            NdiStatus.Text = _ndiError ?? "Mati.";
    }
}
