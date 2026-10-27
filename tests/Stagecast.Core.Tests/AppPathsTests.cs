using Stagecast.Core.Configuration;

namespace Stagecast.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void Resolve_WithOverride_PlacesEverythingUnderIt()
    {
        string root = Path.Combine(Path.GetTempPath(), "stagecast-paths");
        var paths = AppPaths.Resolve(root);

        Assert.Equal(Path.Combine(root, "config.json"), paths.ConfigFile);
        Assert.Equal(Path.Combine(root, "logs"), paths.LogDirectory);
    }

    [Fact]
    public void Resolve_WithoutOverride_EndsWithStagecast()
    {
        // Does not touch STAGECAST_HOME: whatever it is, the result must be an absolute path.
        var paths = AppPaths.Resolve();
        Assert.True(Path.IsPathRooted(paths.Root));
    }
}

public class AppPathsProfileTests
{
    [Fact]
    public void ExplicitRoot_KeepsProfilesInsideIt()
    {
        var paths = AppPaths.Resolve(Path.Combine(Path.GetTempPath(), "stagecast-x"));
        Assert.Equal(Path.Combine(paths.Root, "profiles"), paths.ProfilesDirectory);
    }

    [Fact]
    public void DefaultRoot_PutsProfilesUnderLocalAppData()
    {
        string? previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeEnvironmentVariable, null);
            var paths = AppPaths.Resolve();
            Assert.EndsWith(Path.Combine("Stagecast", "Profiles"), paths.ProfilesDirectory);
            Assert.DoesNotContain(paths.Root + Path.DirectorySeparatorChar, paths.ProfilesDirectory + Path.DirectorySeparatorChar);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeEnvironmentVariable, previous);
        }
    }
}
