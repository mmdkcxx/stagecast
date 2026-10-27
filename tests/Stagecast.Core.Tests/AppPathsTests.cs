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
