namespace Stagecast.Core.Configuration;

/// <summary>
/// The settings file. Properties have defaults so a file that lacks them (an older version, or hand edited) still loads;
/// <see cref="ConfigStore"/> then repairs values that are present but unusable.
/// </summary>
public sealed class AppConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public RenderConfig Render { get; init; } = new();

    /// <summary>Presets made by the operator. The built-in presets are never stored.</summary>
    public List<PresetEntry> Presets { get; init; } = [];
}

public sealed class RenderConfig
{
    public const int DefaultWidth = 2520;
    public const int DefaultHeight = 1080;
    public const int DefaultFps = 60;

    public int Width { get; init; } = DefaultWidth;

    public int Height { get; init; } = DefaultHeight;

    /// <summary>Only 30 and 60 are offered by the application.</summary>
    public int Fps { get; init; } = DefaultFps;
}

public sealed class PresetEntry
{
    public string Name { get; init; } = string.Empty;

    public int Width { get; init; }

    public int Height { get; init; }
}
