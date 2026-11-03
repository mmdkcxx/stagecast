using System.IO;
using Microsoft.Extensions.Logging;

namespace Stagecast.App;

// --selftest shot: saves pictures of the window's controls with each settings window open (STAGECAST_SHOT_DIR, default the temp folder). For looking at the layout; not in check.sh.
internal sealed partial class SelfTest
{
    private async Task RunShotAsync(MainWindow window)
    {
        string dir = Environment.GetEnvironmentVariable("STAGECAST_SHOT_DIR") ?? Path.GetTempPath();
        Directory.CreateDirectory(dir);
        await Task.Delay(1500);
        window.SaveWindowShot(Path.Combine(dir, "shot-main.png"));
        foreach (string name in new[] { "render", "screen", "ndi", "omt", "audio", "stats" })
        {
            window.OpenModal(name);
            await Task.Delay(700);
            window.SaveWindowShot(Path.Combine(dir, $"shot-{name}.png"));
            window.CloseModal();
        }

        _logger.LogInformation("Shot selftest ok");
    }
}
