using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Configuration;
using Stagecast.Core.Output;
using Stagecast.Rendering.Omt;

namespace Stagecast.App;

public partial class MainWindow
{
    private OmtConfig _omtConfig = new();
    private OmtOutput? _omt;
    private string? _omtError;

    internal OmtOutput? Omt => _omt;

    internal OmtConfig OmtSettings => _omtConfig;

    internal string OmtMessage => OmtStatus.Text;

    private void InitOmtCard()
    {
        OmtNameBox.Text = _omtConfig.Name;
        OmtAlphaBox.IsChecked = _omtConfig.Alpha;
        ApplyAlpha();
        ShowOmtStatus();
    }

    private void OnOmtAlphaChanged(object sender, RoutedEventArgs e) => SetOmtAlpha(OmtAlphaBox.IsChecked == true);

    /// <summary>Chooses whether OMT sends transparency. Works while the output runs; only the choice is saved.</summary>
    internal void SetOmtAlpha(bool alpha)
    {
        if (_omtConfig.Alpha == alpha) return;
        _omtConfig = new OmtConfig { Name = _omtConfig.Name, Alpha = alpha };
        if (OmtAlphaBox.IsChecked != alpha) OmtAlphaBox.IsChecked = alpha;
        if (_omt is not null) _omt.SendAlpha = alpha;
        ApplyAlpha();
        SchedulePersist();
    }

    private void OnToggleOmt(object sender, RoutedEventArgs e)
    {
        if (_omt is null) StartOmt();
        else StopOmt(null);
    }

    /// <summary>Sends the program picture and audio as an OMT source under the saved name.</summary>
    internal bool StartOmt()
    {
        if (_engine is null || _audio is null) return false;
        if (_omt is not null) return true;
        var error = SourceNameRules.Validate(_omtConfig.Name);
        if (error != SourceNameError.None)
        {
            _omtError = SourceNameRules.Message(error);
            ShowOmtStatus();
            return false;
        }
        var omt = new OmtOutput(_context.LoggerFactory.CreateLogger("Omt"), () => _engine?.Fps ?? 60);
        if (omt.Start(_omtConfig.Name.Trim()) is { } problem)
        {
            _omtError = problem;
            ShowOmtStatus();
            return false;
        }
        omt.SendAlpha = _omtConfig.Alpha;
        _omt = omt;
        _omtError = null;
        _engine.Hub.AddSink(omt);
        _audio.AddSink(omt);
        OmtButton.Content = "Matikan OMT";
        ShowOmtStatus();
        ShowControls();
        return true;
    }

    internal void StopOmt(string? message)
    {
        if (_omt is null) return;
        _engine?.Hub.RemoveSink(_omt);
        _audio?.RemoveSink(_omt);
        _omt.Dispose();
        _omt = null;
        _omtError = message;
        OmtButton.Content = "Nyalakan OMT";
        ShowOmtStatus();
        if (_engine is not null) ShowControls();
    }

    private void OnOmtNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        RenameOmt(OmtNameBox.Text);
        e.Handled = true;
    }

    private void OnOmtRename(object sender, RoutedEventArgs e) => RenameOmt(OmtNameBox.Text);

    /// <summary>The sender's name is fixed when it is created, so renaming a running sender closes it and makes a new one: receivers drop and must pick it again.</summary>
    internal bool RenameOmt(string name)
    {
        name = name.Trim();
        var error = SourceNameRules.Validate(name);
        if (error != SourceNameError.None)
        {
            _omtError = SourceNameRules.Message(error);
            ShowOmtStatus();
            return false;
        }
        if (name == _omtConfig.Name) { _omtError = null; ShowOmtStatus(); return true; }
        bool running = _omt is not null;
        if (running && !Confirm("Mengganti nama membuat sumber OMT dibuat ulang. vMix akan terputus dan harus memilih sumber yang baru. Lanjutkan?"))
        {
            OmtNameBox.Text = _omtConfig.Name;
            return false;
        }
        _omtConfig = new OmtConfig { Name = name, Alpha = _omtConfig.Alpha };
        OmtNameBox.Text = name;
        _omtError = null;
        SchedulePersist();
        if (!running) { ShowOmtStatus(); return true; }
        StopOmt(null);
        return StartOmt();
    }

    private void ShowOmtStatus()
    {
        if (_omt is { } omt)
            OmtStatus.Text = $"Nyala sebagai “{omt.SourceName}” · {omt.Receivers} penerima · {omt.VideoSent} frame terkirim";
        else
            OmtStatus.Text = _omtError ?? "Mati.";
    }
}
