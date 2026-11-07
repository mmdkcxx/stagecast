using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Updates;

namespace Stagecast.App;

/// <summary>Asks GitHub for a newer release, downloads its installer and checks the hash. It never starts anything by itself.</summary>
internal sealed class UpdateService
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _folder;

    public UpdateService(string version, ILogger logger, HttpClient? http = null, string? folder = null)
    {
        _logger = logger;
        _folder = folder ?? Path.Combine(Path.GetTempPath(), "Stagecast-Update");
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0) _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Stagecast/{version}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>The update, or null when there is none or the check failed (offline, rate limit): a failed check is silent.</summary>
    public async Task<UpdateInfo?> CheckAsync(AppVersion current, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CheckTimeout);
            string json = await _http.GetStringAsync(ReleaseFeed.ListUrl, timeout.Token);
            var info = ReleaseFeed.FindUpdate(json, current);
            _logger.LogInformation("Update check: {Result}", info is null ? $"up to date ({current})" : $"{info.Version} available");
            return info;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger.LogInformation("Update check failed: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Downloads the installer into a temporary folder and returns its path once the SHA-256 matches the published digest.
    /// A file with a wrong hash is deleted and an <see cref="InvalidDataException"/> is thrown.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<(long Received, long Total)> progress, CancellationToken ct)
    {
        if (!Uri.TryCreate(info.InstallerUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com")
            throw new InvalidDataException("Alamat unduhan bukan dari github.com.");
        Directory.CreateDirectory(_folder);
        foreach (string old in Directory.GetFiles(_folder, "Stagecast-Setup-*.exe*")) TryDelete(old);
        string path = Path.Combine(_folder, info.InstallerName);
        string part = path + ".part";
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? info.InstallerSize;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    progress.Report((received, total));
                }
            }
            string actual = await HashAsync(part, ct);
            if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Update {Version}: hash mismatch (expected {Expected}, got {Actual})", info.Version, info.Sha256, actual);
                throw new InvalidDataException("Hash berkas tidak cocok.");
            }
            File.Move(part, path, overwrite: true);
            return path;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    internal static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    /// <summary>Starts the installer (Windows asks for administrator rights). It closes this application, installs and starts it again.</summary>
    public void LaunchInstaller(string path)
    {
        _logger.LogInformation("Starting installer {Path}", path);
        Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1") { UseShellExecute = true });
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
