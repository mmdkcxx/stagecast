using Stagecast.Core.Presets;
using Stagecast.Core.Rendering;

namespace Stagecast.Core.Tests;

public class PresetRulesTests
{
    private static readonly IReadOnlyList<Preset> Builtins = PresetRules.BuiltIns;

    [Fact]
    public void BuiltIns_AreTheFourFromTheMockup()
    {
        Assert.Equal(["LED 7×3", "Full HD", "4K UHD", "Vertikal"], Builtins.Select(p => p.Name));
        Assert.All(Builtins, p => Assert.True(p.BuiltIn));
    }

    [Fact]
    public void ValidateNew_NewNameAndResolution_IsAccepted()
    {
        var error = PresetRules.ValidateNew("Lobby 5x2", new Resolution(1800, 720), Builtins);

        Assert.Equal(PresetError.None, error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateNew_EmptyName_IsRejected(string? name)
    {
        Assert.Equal(PresetError.NameEmpty, PresetRules.ValidateNew(name, new Resolution(1800, 720), Builtins));
    }

    [Fact]
    public void ValidateNew_NameOfExactlyMaxLength_IsAccepted()
    {
        var name = new string('a', PresetRules.MaxNameLength);

        Assert.Equal(PresetError.None, PresetRules.ValidateNew(name, new Resolution(1800, 720), Builtins));
    }

    [Fact]
    public void ValidateNew_NameOneOverMax_IsRejected()
    {
        var name = new string('a', PresetRules.MaxNameLength + 1);

        Assert.Equal(PresetError.NameTooLong, PresetRules.ValidateNew(name, new Resolution(1800, 720), Builtins));
    }

    [Fact]
    public void ValidateNew_SpacesAroundTheNameDoNotCountTowardLength()
    {
        var name = "  " + new string('a', PresetRules.MaxNameLength) + "  ";

        Assert.Equal(PresetError.None, PresetRules.ValidateNew(name, new Resolution(1800, 720), Builtins));
    }

    [Theory]
    [InlineData("full hd")]
    [InlineData("FULL HD")]
    [InlineData("  Full HD  ")]
    public void ValidateNew_DuplicateNameIgnoringCaseAndSpaces_IsRejected(string name)
    {
        Assert.Equal(PresetError.NameInUse, PresetRules.ValidateNew(name, new Resolution(1800, 720), Builtins));
    }

    [Fact]
    public void ValidateNew_ResolutionAlreadyPresent_IsRejected()
    {
        var error = PresetRules.ValidateNew("Panggung utama", new Resolution(2520, 1080), Builtins);

        Assert.Equal(PresetError.ResolutionInUse, error);
    }

    [Fact]
    public void ValidateNew_NameProblemIsReportedBeforeResolutionProblem()
    {
        var error = PresetRules.ValidateNew("LED 7×3", new Resolution(2520, 1080), Builtins);

        Assert.Equal(PresetError.NameInUse, error);
    }

    [Fact]
    public void CanDelete_BuiltInNo_UserPresetYes()
    {
        Assert.False(PresetRules.CanDelete(Builtins[0]));
        Assert.True(PresetRules.CanDelete(new Preset("Lobby", new Resolution(1800, 720))));
    }
}
