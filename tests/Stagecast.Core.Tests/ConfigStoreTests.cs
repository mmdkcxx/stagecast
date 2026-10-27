using Microsoft.Extensions.Logging.Abstractions;
using Stagecast.Core.Configuration;

namespace Stagecast.Core.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stagecast-tests-" + Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_dir, "config.json");

    private ConfigStore Store() => new(ConfigPath, NullLogger.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void WriteRaw(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, json);
    }

    [Fact]
    public void Load_WithoutFile_CreatesDefaults()
    {
        var result = Store().Load();

        Assert.Equal(ConfigLoadStatus.CreatedDefault, result.Status);
        Assert.Equal(2520, result.Config.Render.Width);
        Assert.Equal(1080, result.Config.Render.Height);
        Assert.Equal(60, result.Config.Render.Fps);
        Assert.True(File.Exists(ConfigPath));
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = Store();
        store.Save(new AppConfig
        {
            Render = new RenderConfig { Width = 1920, Height = 1080, Fps = 30 },
            Presets = [new PresetEntry { Name = "Panggung", Width = 3000, Height = 1000 }],
        });

        var result = store.Load();

        Assert.Equal(ConfigLoadStatus.Loaded, result.Status);
        Assert.Equal(30, result.Config.Render.Fps);
        Assert.Equal(1920, result.Config.Render.Width);
        var preset = Assert.Single(result.Config.Presets);
        Assert.Equal("Panggung", preset.Name);
        Assert.Equal(3000, preset.Width);
    }

    [Fact]
    public void Load_CorruptFile_KeepsACopyAndUsesDefaults()
    {
        WriteRaw("{ this is not json");

        var result = Store().Load();

        Assert.Equal(ConfigLoadStatus.RecoveredFromCorrupt, result.Status);
        Assert.Equal(2520, result.Config.Render.Width);
        Assert.Single(Directory.GetFiles(_dir, "config.json.corrupt-*"));
        Assert.Equal(ConfigLoadStatus.Loaded, Store().Load().Status); // the rewritten file is fine
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    public void Load_EmptyOrNullFile_IsTreatedAsCorrupt(string content)
    {
        WriteRaw(content);
        Assert.Equal(ConfigLoadStatus.RecoveredFromCorrupt, Store().Load().Status);
    }

    [Fact]
    public void Load_UnusableValues_AreRepairedIndependently()
    {
        WriteRaw("""{ "render": { "width": 50, "height": 1000, "fps": 45 } }""");

        var result = Store().Load();

        Assert.Equal(ConfigLoadStatus.Repaired, result.Status);
        Assert.Equal(2520, result.Config.Render.Width);  // width and height are replaced together
        Assert.Equal(1080, result.Config.Render.Height);
        Assert.Equal(60, result.Config.Render.Fps);
        Assert.Equal(2, result.Notes.Count);
    }

    [Fact]
    public void Load_MissingAndUnknownFields_AreTolerated()
    {
        WriteRaw("""{ "render": { "fps": 30 }, "somethingNew": { "x": 1 } }""");

        var result = Store().Load();

        Assert.Equal(ConfigLoadStatus.Loaded, result.Status);
        Assert.Equal(30, result.Config.Render.Fps);
        Assert.Equal(2520, result.Config.Render.Width);
    }

    [Fact]
    public void Load_CommentsAndTrailingCommas_AreAllowed()
    {
        WriteRaw("""
            { // hand edited
              "render": { "fps": 30, },
            }
            """);

        Assert.Equal(30, Store().Load().Config.Render.Fps);
    }

    [Fact]
    public void Load_DropsInvalidAndDuplicatePresets()
    {
        WriteRaw("""
            { "presets": [
              { "name": "Bagus", "width": 3000, "height": 1000 },
              { "name": "bagus", "width": 3100, "height": 1000 },
              { "name": "Kembar resolusi", "width": 3000, "height": 1000 },
              { "name": "Full HD", "width": 1234, "height": 567 },
              { "name": "", "width": 1234, "height": 567 },
              { "name": "Terlalu kecil", "width": 10, "height": 10 },
              { "name": "  Spasi  ", "width": 1300, "height": 700 }
            ] }
            """);

        var result = Store().Load();

        Assert.Equal(ConfigLoadStatus.Repaired, result.Status);
        Assert.Equal(["Bagus", "Spasi"], result.Config.Presets.Select(p => p.Name).ToArray());
        Assert.Equal(5, result.Notes.Count);
    }

    [Fact]
    public void Load_NewerSchema_IsNotedButKept()
    {
        WriteRaw("""{ "schemaVersion": 99, "render": { "fps": 30 } }""");

        var result = Store().Load();

        Assert.Equal(ConfigLoadStatus.Repaired, result.Status);
        Assert.Equal(30, result.Config.Render.Fps);
        Assert.Contains(result.Notes, n => n.Contains("newer version"));
    }

    [Fact]
    public void Save_LeavesNoTemporaryFile()
    {
        var store = Store();
        store.Save(new AppConfig());
        store.Save(new AppConfig { Render = new RenderConfig { Fps = 30 } });

        Assert.Equal([ConfigPath], Directory.GetFiles(_dir));
    }
}
