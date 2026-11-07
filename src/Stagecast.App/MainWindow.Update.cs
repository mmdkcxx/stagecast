using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Configuration;
using Stagecast.Core.Updates;

namespace Stagecast.App;

/// <summary>
/// Update notice: a check at start and every few hours, a chip in the top bar, a banner once per version, and an install that only
/// the operator starts. A beta never sees a stable version that is older than itself.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(6);

    private UpdateConfig _updateConfig = new();
    private UpdateService? _updateService;
    private UpdateInfo? _update;
    private DispatcherTimer? _updateTimer;
    private bool _installingUpdate;

    /// <summary>The update the chip offers, or null.</summary>
    internal UpdateInfo? PendingUpdate => _update;

    private AppVersion RunningVersion
    {
        get
        {
            // The assembly version has no pre-release part; the informational version ("1.2.2-beta.1+hash") does.
            string? informational = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (AppVersion.TryParse(informational, out var v) || AppVersion.TryParse(_context.Version, out v)) return v;
            AppVersion.TryParse("0.0.0", out var zero);
            return zero;
        }
    }

    private void StartUpdateChecks()
    {
        // The automatic tests never touch the network; STAGECAST_NO_UPDATE_CHECK=1 switches it off for anyone.
        if (_context.SelfTest is not null || Environment.GetEnvironmentVariable("STAGECAST_NO_UPDATE_CHECK") == "1") return;
        _updateService = new UpdateService(_context.Version, _logger);
        _ = CheckForUpdateAsync();
        _updateTimer = new DispatcherTimer { Interval = UpdateInterval };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdateAsync();
        _updateTimer.Start();
        Closed += (_, _) => _updateTimer.Stop();
    }

    private async Task CheckForUpdateAsync()
    {
        if (_updateService is null || _installingUpdate) return;
        var info = await _updateService.CheckAsync(RunningVersion, CancellationToken.None);
        if (info is null || info.Version.ToString() == _updateConfig.SkippedVersion) return;
        _update = info;
        ShowUpdateNotice(info);
    }

    private void ShowUpdateNotice(UpdateInfo info)
    {
        UpdateChipVersion.Text = $"v{info.Version}";
        UpdateChip.Visibility = Visibility.Visible;
        if (_updateConfig.BannerShownVersion == info.Version.ToString()) return;
        UpdateBannerTitle.Text = $"Stagecast {info.Version} tersedia";
        UpdateBannerDetail.Text = $"Anda memakai {RunningVersion}. Pembaruan tidak dipasang sendiri.";
        UpdateBanner.Visibility = Visibility.Visible;
        _updateConfig = new UpdateConfig { SkippedVersion = _updateConfig.SkippedVersion, BannerShownVersion = info.Version.ToString() };
        Persist();
    }

    private void OnUpdateChip(object sender, RoutedEventArgs e) => OfferUpdate();

    private void OnUpdateBannerDetails(object sender, RoutedEventArgs e)
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
        OfferUpdate();
    }

    private void OnUpdateBannerLater(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    /// <summary>Details, then (if the operator agrees) save-session question, download, hash check and installer. Never runs by itself.</summary>
    private void OfferUpdate()
    {
        if (_update is not { } info || _updateService is null || _installingUpdate) return;
        UpdateBanner.Visibility = Visibility.Collapsed;
        switch (UpdateDialogs.ShowDetails(this, info, RunningVersion.ToString(), LiveOutputNames()))
        {
            case UpdateChoice.Skip:
                _updateConfig = new UpdateConfig { SkippedVersion = info.Version.ToString(), BannerShownVersion = _updateConfig.BannerShownVersion };
                UpdateChip.Visibility = Visibility.Collapsed;
                _update = null;
                Persist();
                return;
            case UpdateChoice.Install:
                break;
            default:
                return;
        }
        if (!ConfirmCloseSession()) return;
        string? installer = UpdateDialogs.ShowDownload(this, info, _updateService);
        if (installer is null) return;
        try
        {
            _installingUpdate = true;
            _updateService.LaunchInstaller(installer);
        }
        catch (Win32Exception ex)
        {
            // Most often the operator said no to the administrator prompt: stay in the application.
            _installingUpdate = false;
            _logger.LogWarning(ex, "Installer did not start");
            StatusText.Text = $"Pemasang tidak dijalankan: {ex.Message}";
            return;
        }
        Close();
    }
}
