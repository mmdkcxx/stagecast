using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Stagecast.Core.Resilience;

namespace Stagecast.Rendering;

/// <summary>
/// Keeps pages alive through things that go wrong during an event: a network that drops (the page is loaded again until it works),
/// a page process that crashes or hangs (reloaded), and a browser process that dies (the tab gets a new browser).
/// </summary>
public sealed partial class RenderEngine
{
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private bool _networkHooked;

    /// <summary>A tab has a problem the operator should know about (also raised when recovery is not possible). Message is in Indonesian, for the status line.</summary>
    public event Action<PageTab, string>? TabProblem;

    /// <summary>A tab that had a problem works again.</summary>
    public event Action<PageTab>? TabRecovered;

    private void HookRecovery(PageTab tab)
    {
        if (!_networkHooked)
        {
            _networkHooked = true;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        }
        tab.Browser.NavigationStarting += (_, e) => tab.NavigatingTo = e.Uri;
        tab.Browser.NavigationCompleted += (_, e) => OnNavigationDone(tab, e);
        tab.Browser.ProcessFailed += (_, e) => OnProcessFailed(tab, e);
    }

    private void DisposeRecovery()
    {
        if (!_networkHooked) return;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _networkHooked = false;
    }

    internal static bool IsNetworkError(CoreWebView2WebErrorStatus status) => status is
        CoreWebView2WebErrorStatus.Disconnected or CoreWebView2WebErrorStatus.CannotConnect or CoreWebView2WebErrorStatus.ConnectionAborted
        or CoreWebView2WebErrorStatus.ConnectionReset or CoreWebView2WebErrorStatus.HostNameNotResolved or CoreWebView2WebErrorStatus.ServerUnreachable
        or CoreWebView2WebErrorStatus.Timeout or CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse
        or CoreWebView2WebErrorStatus.Unknown; // a refused connection to a local site is reported as Unknown the first time; retrying a web page is harmless

    private static bool IsWebAddress(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private void OnNavigationDone(PageTab tab, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            tab.RetryAttempt = 0;
            tab.FailedUrl = null;
            if (tab.Problem is not null)
            {
                tab.Problem = null;
                _logger.LogInformation("Tab {Id} works again", tab.Id);
                TabRecovered?.Invoke(tab);
            }
            return;
        }
        if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return; // replaced by a newer navigation
        string? url = tab.NavigatingTo;
        if (!IsWebAddress(url)) return;
        if (!IsNetworkError(e.WebErrorStatus))
        {
            tab.Problem = $"Halaman tidak bisa dimuat ({e.WebErrorStatus}).";
            TabProblem?.Invoke(tab, tab.Problem);
            return;
        }
        tab.FailedUrl = url;
        var delay = RetryPolicy.Delay(tab.RetryAttempt);
        tab.Problem = $"Jaringan atau situs tidak terjangkau ({e.WebErrorStatus}). Mencoba lagi dalam {delay.TotalSeconds:0} dtk.";
        _logger.LogWarning("Tab {Id} could not load {Url}: {Status}; retry {Attempt} in {Delay}s", tab.Id, url, e.WebErrorStatus, tab.RetryAttempt + 1, delay.TotalSeconds);
        TabProblem?.Invoke(tab, tab.Problem);
        ScheduleRetry(tab, delay);
    }

    private void ScheduleRetry(PageTab tab, TimeSpan delay)
    {
        if (tab.RetryPending) return;
        tab.RetryPending = true;
        _ = RetryAfterAsync(tab, delay);
    }

    private async Task RetryAfterAsync(PageTab tab, TimeSpan delay)
    {
        await Task.Delay(delay);
        tab.RetryPending = false;
        if (!_tabs.Contains(tab) || tab.FailedUrl is not { } url) return; // closed, loaded, or sent somewhere else meanwhile
        tab.RetryAttempt++;
        try
        {
            tab.Browser.Navigate(url);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _logger.LogWarning(ex, "Retrying {Url} failed", url);
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable) return;
        _logger.LogInformation("The network is back");
        Post(async () =>
        {
            await Task.Delay(1000); // the adapter needs a moment before the first request works
            foreach (var tab in _tabs.ToList())
            {
                if (tab.FailedUrl is not { } url) continue;
                tab.RetryAttempt = 0;
                try
                {
                    tab.Browser.Navigate(url);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    _logger.LogWarning(ex, "Loading {Url} after the network came back failed", url);
                }
            }
        });
    }

    private void Post(Func<Task> work)
    {
        if (_ui is null) { _ = work(); return; }
        _ui.Post(async _ => await work(), null);
    }

    private void OnProcessFailed(PageTab tab, CoreWebView2ProcessFailedEventArgs e)
    {
        _logger.LogWarning("Tab {Id}: browser process failure {Kind} ({Reason})", tab.Id, e.ProcessFailedKind, e.Reason);
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                _ = RecoverBrowserAsync(tab);
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
            case CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                _ = ReloadAfterCrashAsync(tab, e.ProcessFailedKind);
                break;
        }
    }

    private async Task ReloadAfterCrashAsync(PageTab tab, CoreWebView2ProcessFailedKind kind)
    {
        var now = DateTimeOffset.UtcNow;
        if (!RetryPolicy.AllowReload(tab.Crashes, now))
        {
            tab.Problem = "Halaman berulang kali berhenti. Muat ulang manual atau ganti halaman.";
            TabProblem?.Invoke(tab, tab.Problem);
            return;
        }
        tab.Crashes.Add(now);
        tab.Problem = kind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive ? "Halaman tidak merespons; dimuat ulang." : "Halaman berhenti mendadak; dimuat ulang.";
        TabProblem?.Invoke(tab, tab.Problem);
        await Task.Delay(500);
        try
        {
            if (_tabs.Contains(tab)) tab.Browser.Reload();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _logger.LogWarning(ex, "Reload after a crash failed");
        }
    }

    /// <summary>
    /// The whole browser process of a profile died. Every tab of that profile gets a new browser and loads its last address again.
    /// In Preview + Take the stage layout is in the way, so the operator is told instead (leave the mode, then close and reopen the tab).
    /// </summary>
    private async Task RecoverBrowserAsync(PageTab tab)
    {
        if (tab.Recovering || !_tabs.Contains(tab)) return;
        if (PreviewMode)
        {
            tab.Problem = "Browser tab ini berhenti. Keluar dari Preview + Take, lalu tab akan dipulihkan.";
            TabProblem?.Invoke(tab, tab.Problem);
            _recoverAfterPreviewMode.Add(tab);
            return;
        }
        tab.Recovering = true;
        tab.Problem = "Browser tab ini berhenti; membuat browser baru.";
        TabProblem?.Invoke(tab, tab.Problem);
        try
        {
            string key = tab.ProfileId ?? SharedProfileKey;
            // Only drop the environment if it is still the dead one; another tab of the same profile may have made a new one already.
            if (_environments.TryGetValue(key, out var current) && ReferenceEquals(current, tab.Environment)) _environments.Remove(key);
            await Task.Delay(500);
            var fresh = await ReplaceProfileAsync(tab, tab.ProfileId);
            fresh.Problem = null;
            _logger.LogInformation("Tab {Id} got a new browser", fresh.Id);
            TabRecovered?.Invoke(fresh);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            _logger.LogError(ex, "Recovering a tab whose browser died failed");
            tab.Problem = "Browser tab ini berhenti dan tidak bisa dipulihkan otomatis.";
            TabProblem?.Invoke(tab, tab.Problem);
        }
        finally
        {
            tab.Recovering = false;
        }
    }

    private readonly List<PageTab> _recoverAfterPreviewMode = [];

    /// <summary>Tabs that lost their browser while Preview + Take was on are recovered once the mode is left.</summary>
    private void RecoverDeferredTabs()
    {
        var tabs = _recoverAfterPreviewMode.ToList();
        _recoverAfterPreviewMode.Clear();
        foreach (var tab in tabs) _ = RecoverBrowserAsync(tab);
    }

    private void SetMuted(PageTab tab, bool muted)
    {
        try
        {
            tab.Browser.IsMuted = muted;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // The browser is gone; recovery gives the tab a new one.
        }
    }
}
