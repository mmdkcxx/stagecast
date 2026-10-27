using Microsoft.Extensions.Logging.Abstractions;
using Stagecast.Core.Configuration;
using Stagecast.Core.Tabs;

namespace Stagecast.Core.Tests;

public sealed class ConfigTabsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stagecast-tests-" + Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_dir, "config.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private ConfigLoadResult LoadRaw(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, json);
        return new ConfigStore(ConfigPath, NullLogger.Instance).Load();
    }

    [Fact]
    public void FileWithoutTabs_LoadsWithNoTabs_AndNoRepair()
    {
        var result = LoadRaw("""{ "schemaVersion": 1, "render": { "width": 2520, "height": 1080, "fps": 60 } }""");
        Assert.Equal(ConfigLoadStatus.Loaded, result.Status);
        Assert.Empty(result.Config.Tabs);
    }

    [Fact]
    public void Tabs_RoundTripThroughSave()
    {
        Directory.CreateDirectory(_dir);
        var store = new ConfigStore(ConfigPath, NullLogger.Instance);
        store.Save(new AppConfig
        {
            Tabs = [new TabEntry { Name = "Kuis", Url = "https://kahoot.it" }, new TabEntry { Name = "Slide", Url = "https://www.canva.com" }],
            ActiveTab = 1,
        });
        var loaded = store.Load();
        Assert.Equal(ConfigLoadStatus.Loaded, loaded.Status);
        Assert.Equal(["Kuis", "Slide"], loaded.Config.Tabs.Select(t => t.Name));
        Assert.Equal(1, loaded.Config.ActiveTab);
    }

    [Fact]
    public void BadTabs_AreRepaired()
    {
        string longName = new('x', 60);
        var result = LoadRaw($$"""
            { "tabs": [ { "name": "", "url": "https://a.example" }, { "name": "{{longName}}", "url": "" }, { "name": "Ok" } ], "activeTab": 9 }
            """);
        var tabs = result.Config.Tabs;
        Assert.Equal(3, tabs.Count);
        Assert.Equal("Tab 1", tabs[0].Name);
        Assert.Equal(TabRules.MaxNameLength, tabs[1].Name.Length);
        Assert.Equal(TabRules.BlankUrl, tabs[1].Url);
        Assert.Equal(2, result.Config.ActiveTab);
    }

    [Fact]
    public void TooManyTabs_AreCut()
    {
        string many = string.Join(",", Enumerable.Range(1, 20).Select(i => $$"""{ "name": "T{{i}}", "url": "about:blank" }"""));
        var result = LoadRaw($$"""{ "tabs": [ {{many}} ] }""");
        Assert.Equal(ConfigLoadStatus.Repaired, result.Status);
        Assert.Equal(TabRules.MaxTabs, result.Config.Tabs.Count);
    }
}
