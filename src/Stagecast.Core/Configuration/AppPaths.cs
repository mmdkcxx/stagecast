namespace Stagecast.Core.Configuration;

/// <summary>Where the application keeps its files. <c>STAGECAST_HOME</c> or an explicit root moves everything (used by tests).</summary>
public sealed record AppPaths(string Root)
{
    public const string HomeEnvironmentVariable = "STAGECAST_HOME";

    public string ConfigFile => Path.Combine(Root, "config.json");

    public string LogDirectory => Path.Combine(Root, "logs");

    public static AppPaths Resolve(string? rootOverride = null)
    {
        string? root = !string.IsNullOrWhiteSpace(rootOverride) ? rootOverride : Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stagecast");
        return new AppPaths(Path.GetFullPath(root));
    }
}
