namespace Stagecast.Core.Configuration;

/// <summary>Where the application keeps its files. <c>STAGECAST_HOME</c> or an explicit root moves everything (used by tests).</summary>
public sealed record AppPaths(string Root, string? ProfilesRoot = null)
{
    public const string HomeEnvironmentVariable = "STAGECAST_HOME";

    public string ConfigFile => Path.Combine(Root, "config.json");

    public string LogDirectory => Path.Combine(Root, "logs");

    /// <summary>
    /// Browser profiles (cookies, logins). Sensitive and not meant to roam, so by default under %LocalAppData%; with an explicit
    /// root (tests, <c>--data-dir</c>) they live inside that root so a test never touches the real logins.
    /// </summary>
    public string ProfilesDirectory => ProfilesRoot ?? Path.Combine(Root, "profiles");

    public static AppPaths Resolve(string? rootOverride = null)
    {
        string? root = !string.IsNullOrWhiteSpace(rootOverride) ? rootOverride : Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(root)) return new AppPaths(Path.GetFullPath(root));
        return new AppPaths(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stagecast"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stagecast", "Profiles"));
    }
}
