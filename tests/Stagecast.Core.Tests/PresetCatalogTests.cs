using Stagecast.Core.Configuration;
using Stagecast.Core.Presets;
using Stagecast.Core.Rendering;

namespace Stagecast.Core.Tests;

public class PresetCatalogTests
{
    private static PresetCatalog Empty() => PresetCatalog.FromConfig(new AppConfig());

    [Fact]
    public void StartsWithTheBuiltIns()
    {
        var catalog = Empty();
        Assert.Equal(PresetRules.BuiltIns, catalog.All);
        Assert.Empty(catalog.ToEntries());
    }

    [Fact]
    public void Add_KeepsTheNameTrimmedAndAppendsLast()
    {
        Assert.Equal(PresetError.None, Empty().TryAdd("  Panggung  ", new Resolution(1600, 900), out var catalog));
        Assert.Equal(new Preset("Panggung", new Resolution(1600, 900)), catalog.All[^1]);
        Assert.Single(catalog.ToEntries());
    }

    [Theory]
    [InlineData("", 1600, 900, PresetError.NameEmpty)]
    [InlineData("   ", 1600, 900, PresetError.NameEmpty)]
    [InlineData("Nama yang jauh lebih panjang dari dua puluh empat", 1600, 900, PresetError.NameTooLong)]
    [InlineData("full hd", 1600, 900, PresetError.NameInUse)]
    [InlineData("Baru", 1920, 1080, PresetError.ResolutionInUse)]
    public void Add_RejectsInvalid_AndLeavesTheCatalogAlone(string name, int w, int h, PresetError expected)
    {
        var start = Empty();
        Assert.Equal(expected, start.TryAdd(name, new Resolution(w, h), out var result));
        Assert.Same(start, result);
    }

    [Fact]
    public void Remove_OnlyCustomPresets()
    {
        Empty().TryAdd("Panggung", new Resolution(1600, 900), out var catalog);
        var custom = catalog.All[^1];
        Assert.False(catalog.TryRemove(PresetRules.BuiltIns[0], out var same));
        Assert.Same(catalog, same);
        Assert.True(catalog.TryRemove(custom, out var removed));
        Assert.DoesNotContain(custom, removed.All);
        Assert.Equal(PresetRules.BuiltIns.Count, removed.All.Count);
    }

    [Fact]
    public void FromConfig_LoadsCustomAndDropsBadOrDuplicateEntries()
    {
        var config = new AppConfig
        {
            Presets =
            [
                new PresetEntry { Name = "Panggung", Width = 1600, Height = 900 },
                new PresetEntry { Name = "panggung", Width = 1700, Height = 900 }, // same name
                new PresetEntry { Name = "Lain", Width = 1920, Height = 1080 },    // same resolution as Full HD
                new PresetEntry { Name = "Kecil", Width = 10, Height = 10 },       // below the minimum
            ],
        };
        var catalog = PresetCatalog.FromConfig(config);
        Assert.Equal(PresetRules.BuiltIns.Count + 1, catalog.All.Count);
        Assert.Equal("Panggung", catalog.All[^1].Name);
    }

    [Fact]
    public void RoundTripsThroughTheSettingsEntries()
    {
        Empty().TryAdd("Panggung", new Resolution(1600, 900), out var catalog);
        var again = PresetCatalog.FromConfig(new AppConfig { Presets = catalog.ToEntries() });
        Assert.Equal(catalog.All, again.All);
    }

    [Fact]
    public void Match_FindsThePresetOfTheActiveResolution()
    {
        var catalog = Empty();
        Assert.Equal("LED 7×3", catalog.Match(new Resolution(2520, 1080))?.Name);
        Assert.Null(catalog.Match(new Resolution(1234, 567)));
    }

    [Fact]
    public void DeletingThePresetInUse_DoesNotTouchTheResolution()
    {
        // The catalog knows nothing about the active resolution, so removing the matching preset leaves nothing to change.
        Empty().TryAdd("Panggung", new Resolution(1600, 900), out var catalog);
        var active = new Resolution(1600, 900);
        catalog.TryRemove(catalog.Match(active)!, out var removed);
        Assert.Null(removed.Match(active));
        Assert.Equal(new Resolution(1600, 900), active);
    }
}
