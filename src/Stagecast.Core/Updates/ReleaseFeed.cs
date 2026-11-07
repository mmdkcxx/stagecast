using System.Text.Json;

namespace Stagecast.Core.Updates;

/// <summary>A stable release that is newer than the running version and can be installed.</summary>
/// <param name="Sha256">Lower case hex of the installer, from the digest GitHub publishes for the asset.</param>
public sealed record UpdateInfo(AppVersion Version, string Notes, string PageUrl, string InstallerName, string InstallerUrl, long InstallerSize, string Sha256, DateTimeOffset? PublishedAt);

/// <summary>Reads the list of releases of the repository (GitHub API) and picks the update, if any.</summary>
public static class ReleaseFeed
{
    public const string ListUrl = "https://api.github.com/repos/mmdkcxx/stagecast/releases?per_page=30";

    private const string Sha256Prefix = "sha256:";

    /// <summary>
    /// The newest release that is stable, not a draft, tagged vX.Y.Z (so Pattern releases are ignored), has a Stagecast-Setup-X.Y.Z.exe
    /// asset with a digest, and is newer than <paramref name="current"/>. Null when there is none. A feed that cannot be read gives null.
    /// </summary>
    public static UpdateInfo? FindUpdate(string json, AppVersion current)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            UpdateInfo? best = null;
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (Candidate(release) is { } info && info.Version > current && (best is null || info.Version > best.Version)) best = info;
            }
            return best;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static UpdateInfo? Candidate(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object) return null;
        if (Bool(release, "draft") || Bool(release, "prerelease")) return null;
        string tag = Str(release, "tag_name");
        if (!tag.StartsWith('v') || !AppVersion.TryParse(tag, out var version) || version.IsPreRelease) return null;
        string name = $"Stagecast-Setup-{version}.exe";
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (Str(asset, "name") != name) continue;
            string digest = Str(asset, "digest");
            string url = Str(asset, "browser_download_url");
            if (!digest.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase) || digest.Length != Sha256Prefix.Length + 64) return null;
            if (!url.StartsWith("https://", StringComparison.Ordinal)) return null;
            DateTimeOffset? published = DateTimeOffset.TryParse(Str(release, "published_at"), out var p) ? p : null;
            long size = asset.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : 0;
            return new UpdateInfo(version, Str(release, "body"), Str(release, "html_url"), name, url, size, digest[Sha256Prefix.Length..].ToLowerInvariant(), published);
        }
        return null;
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
