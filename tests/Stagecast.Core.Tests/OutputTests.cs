using Microsoft.Extensions.Logging.Abstractions;
using Stagecast.Core.Configuration;
using Stagecast.Core.Display;
using Stagecast.Core.Output;

namespace Stagecast.Core.Tests;

public sealed class OutputGuardTests
{
    private static readonly MonitorInfo Main = new(@"\\.\DISPLAY1", 1, 0, 0, 1920, 1080, true);
    private static readonly MonitorInfo Side = new(@"\\.\DISPLAY2", 2, 1920, 0, 1920, 1080, false);

    [Fact]
    public void OneMonitor_Warns() => Assert.Equal(OutputWarning.OnlyOneMonitor, OutputGuard.Check([Main], Main, Main));

    [Fact]
    public void SameAsControl_Warns() => Assert.Equal(OutputWarning.SameAsControl, OutputGuard.Check([Main, Side], Main, Main));

    [Fact]
    public void OtherMonitor_NoWarning() => Assert.Equal(OutputWarning.None, OutputGuard.Check([Main, Side], Side, Main));

    [Fact]
    public void Find_IgnoresCase_AndMissingIsNull()
    {
        Assert.Same(Side, OutputGuard.Find([Main, Side], @"\\.\display2"));
        Assert.Null(OutputGuard.Find([Main], @"\\.\DISPLAY2"));
        Assert.Null(OutputGuard.Find([Main], null));
    }

    [Fact]
    public void Default_PrefersAMonitorOtherThanControl()
    {
        Assert.Same(Side, OutputGuard.Default([Main, Side], Main));
        Assert.Same(Main, OutputGuard.Default([Main], Main));
        Assert.Null(OutputGuard.Default([], Main));
    }

    [Fact]
    public void Contains_UsesVirtualDesktopPixels()
    {
        Assert.True(Side.Contains(1920, 0));
        Assert.False(Side.Contains(1919, 0));
        Assert.False(Side.Contains(3840, 0));
    }

    [Fact]
    public void Messages_ExistForEveryWarning()
    {
        Assert.NotEmpty(OutputGuard.Message(OutputWarning.OnlyOneMonitor));
        Assert.NotEmpty(OutputGuard.Message(OutputWarning.SameAsControl));
        Assert.Empty(OutputGuard.Message(OutputWarning.None));
    }
}

public sealed class ConfigOutputTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stagecast-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private ConfigStore Store() => new(Path.Combine(_dir, "config.json"), NullLogger.Instance);

    [Fact]
    public void Defaults_AreSqueezeAndAlwaysOnTop()
    {
        var output = Store().Load().Config.Output;
        Assert.Null(output.MonitorId);
        Assert.Equal(ScaleMode.Squeeze, output.Scale);
        Assert.True(output.AlwaysOnTop);
    }

    [Fact]
    public void RoundTrip_KeepsOutputSettings()
    {
        var store = Store();
        store.Load();
        store.Save(new AppConfig { Output = new OutputConfig { MonitorId = @"\\.\DISPLAY2", Scale = ScaleMode.Fit, AlwaysOnTop = false } });
        var output = Store().Load().Config.Output;
        Assert.Equal(@"\\.\DISPLAY2", output.MonitorId);
        Assert.Equal(ScaleMode.Fit, output.Scale);
        Assert.False(output.AlwaysOnTop);
    }

    [Fact]
    public void UnknownScale_IsRepairedToSqueeze()
    {
        var store = Store();
        store.Load();
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{\"output\":{\"scale\":42,\"monitorId\":\"  \"}}");
        var result = Store().Load();
        Assert.Equal(ConfigLoadStatus.Repaired, result.Status);
        Assert.Equal(ScaleMode.Squeeze, result.Config.Output.Scale);
        Assert.Null(result.Config.Output.MonitorId);
    }
}

public sealed class ConfigAudioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stagecast-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private ConfigStore Store() => new(Path.Combine(_dir, "config.json"), NullLogger.Instance);

    [Fact]
    public void Defaults_AreDefaultDeviceFullVolume()
    {
        var audio = Store().Load().Config.Audio;
        Assert.Null(audio.DeviceId);
        Assert.Equal(1, audio.Volume);
        Assert.False(audio.Muted);
    }

    [Fact]
    public void RoundTrip_KeepsAudioSettings()
    {
        var store = Store();
        store.Load();
        store.Save(new AppConfig { Audio = new AudioConfig { DeviceId = "{0.0.0.00000000}.{abc}", Volume = 0.4, Muted = true } });
        var audio = Store().Load().Config.Audio;
        Assert.Equal("{0.0.0.00000000}.{abc}", audio.DeviceId);
        Assert.Equal(0.4, audio.Volume);
        Assert.True(audio.Muted);
    }

    [Fact]
    public void VolumeOutOfRange_IsClamped()
    {
        Store().Load();
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{\"audio\":{\"volume\":7}}");
        var result = Store().Load();
        Assert.Equal(ConfigLoadStatus.Repaired, result.Status);
        Assert.Equal(1, result.Config.Audio.Volume);
    }
}

public sealed class SourceNameTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stagecast-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData("Stagecast", SourceNameError.None)]
    [InlineData("  LED Utama  ", SourceNameError.None)]
    [InlineData("", SourceNameError.Empty)]
    [InlineData("   ", SourceNameError.Empty)]
    [InlineData("tab\tname", SourceNameError.InvalidCharacter)]
    public void Validate(string name, SourceNameError expected) => Assert.Equal(expected, SourceNameRules.Validate(name));

    [Fact]
    public void Validate_TooLong() => Assert.Equal(SourceNameError.TooLong, SourceNameRules.Validate(new string('a', SourceNameRules.MaxLength + 1)));

    [Fact]
    public void Validate_ExactlyMaxIsFine() => Assert.Equal(SourceNameError.None, SourceNameRules.Validate(new string('a', SourceNameRules.MaxLength)));

    [Fact]
    public void Repair_TrimsCutsOrFallsBack()
    {
        Assert.Equal("LED", SourceNameRules.Repair("  LED ", "x"));
        Assert.Equal(SourceNameRules.MaxLength, SourceNameRules.Repair(new string('a', 100), "x").Length);
        Assert.Equal("x", SourceNameRules.Repair("", "x"));
        Assert.Equal("x", SourceNameRules.Repair("a\u0001b", "x"));
    }

    [Fact]
    public void Config_KeepsNdiName_AndRepairsABadOne()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"), NullLogger.Instance);
        store.Load();
        store.Save(new AppConfig { Ndi = new NdiConfig { Name = "Panggung 1" } });
        Assert.Equal("Panggung 1", new ConfigStore(Path.Combine(_dir, "config.json"), NullLogger.Instance).Load().Config.Ndi.Name);

        File.WriteAllText(Path.Combine(_dir, "config.json"), "{\"ndi\":{\"name\":\"   \"}}");
        var repaired = new ConfigStore(Path.Combine(_dir, "config.json"), NullLogger.Instance).Load();
        Assert.Equal(ConfigLoadStatus.Repaired, repaired.Status);
        Assert.Equal(SourceNameRules.DefaultNdiName, repaired.Config.Ndi.Name);
    }
}
