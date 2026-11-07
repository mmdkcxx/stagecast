using Stagecast.Core.Updates;

namespace Stagecast.Core.Tests;

public class UpdateTests
{
    private static AppVersion V(string s) => AppVersion.TryParse(s, out var v) ? v : throw new ArgumentException(s);

    [Theory]
    [InlineData("1.2.1", "1.2.2", -1)]
    [InlineData("1.2.2-beta.1", "1.2.2", -1)]
    [InlineData("1.2.2-beta.1", "1.2.1", 1)]
    [InlineData("1.2.2-beta.2", "1.2.2-beta.10", -1)]
    [InlineData("v1.10.0", "1.9.9", 1)]
    [InlineData("1.2.1", "1.2.1+build5", 0)]
    public void Compare(string a, string b, int expected) => Assert.Equal(expected, Math.Sign(V(a).CompareTo(V(b))));

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.x")]
    [InlineData("pattern-v1.0.0")]
    [InlineData("1.2.3-")]
    public void Parse_RejectsNonVersions(string text) => Assert.False(AppVersion.TryParse(text, out _));

    private static string Digest => "sha256:" + new string('a', 64);

    private static string Release(string tag, bool pre = false, bool draft = false, string? digest = null, string? asset = null)
    {
        string version = tag.TrimStart('v');
        string name = asset ?? $"Stagecast-Setup-{version}.exe";
        string d = digest is null ? $"\"digest\":\"{Digest}\"," : digest.Length == 0 ? "" : $"\"digest\":\"{digest}\",";
        return $"{{\"tag_name\":\"{tag}\",\"draft\":{draft.ToString().ToLowerInvariant()},\"prerelease\":{pre.ToString().ToLowerInvariant()},\"body\":\"catatan {tag}\",\"html_url\":\"https://github.com/x/y/releases/tag/{tag}\",\"published_at\":\"2026-11-10T01:00:00Z\",\"assets\":[{{\"name\":\"{name}\",{d}\"size\":57306338,\"browser_download_url\":\"https://github.com/x/y/releases/download/{tag}/{name}\"}}]}}";
    }

    private static string Feed(params string[] releases) => "[" + string.Join(",", releases) + "]";

    [Fact]
    public void FindUpdate_PicksNewestStable()
    {
        var info = ReleaseFeed.FindUpdate(Feed(Release("v1.2.3"), Release("v1.3.0"), Release("v1.2.1")), V("1.2.1"));
        Assert.NotNull(info);
        Assert.Equal("1.3.0", info.Version.ToString());
        Assert.Equal("Stagecast-Setup-1.3.0.exe", info.InstallerName);
        Assert.Equal(new string('a', 64), info.Sha256);
        Assert.Equal(57306338, info.InstallerSize);
    }

    [Fact]
    public void FindUpdate_IgnoresPatternPreReleaseAndDraft()
    {
        string feed = Feed(Release("pattern-v9.0.0", asset: "Stagecast-Pattern-9.0.0.exe"), Release("v1.4.0", pre: true), Release("v1.5.0", draft: true));
        Assert.Null(ReleaseFeed.FindUpdate(feed, V("1.2.1")));
    }

    [Fact]
    public void FindUpdate_NullWhenCurrentIsNewerOrEqual_IncludingBeta()
    {
        string feed = Feed(Release("v1.2.1"));
        Assert.Null(ReleaseFeed.FindUpdate(feed, V("1.2.1")));
        Assert.Null(ReleaseFeed.FindUpdate(feed, V("1.2.2-beta.1")));
        Assert.NotNull(ReleaseFeed.FindUpdate(feed, V("1.2.0")));
    }

    [Fact]
    public void FindUpdate_RequiresInstallerWithDigest()
    {
        Assert.Null(ReleaseFeed.FindUpdate(Feed(Release("v1.3.0", digest: "")), V("1.2.1")));
        Assert.Null(ReleaseFeed.FindUpdate(Feed(Release("v1.3.0", digest: "sha256:abc")), V("1.2.1")));
        Assert.Null(ReleaseFeed.FindUpdate(Feed(Release("v1.3.0", asset: "Stagecast-1.3.0-win-x64.zip")), V("1.2.1")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"message\":\"API rate limit exceeded\"}")]
    public void FindUpdate_UnreadableFeedGivesNull(string json) => Assert.Null(ReleaseFeed.FindUpdate(json, V("1.2.1")));
}

public class ReleaseNotesTextTests
{
    [Fact]
    public void Lines_HeadingsBulletsAndWrappedLines()
    {
        var lines = ReleaseNotesText.Lines("\n### Ditambahkan\n- Bar progres **baru**\n  dengan langkah.\n\n### Diperbaiki\n- Satu `font`\n");
        Assert.Equal(
            new (NoteLine, string)[] { (NoteLine.Heading, "Ditambahkan"), (NoteLine.Bullet, "Bar progres baru dengan langkah."), (NoteLine.Heading, "Diperbaiki"), (NoteLine.Bullet, "Satu font") },
            lines);
    }
}
