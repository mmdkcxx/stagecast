namespace Stagecast.Core.Configuration;

/// <summary>
/// The settings file. Properties have defaults so a file that lacks them (an older version, or hand edited) still loads;
/// <see cref="ConfigStore"/> then repairs values that are present but unusable.
/// </summary>
public sealed class AppConfig
{
    public const int CurrentSchemaVersion = 1;

    public const int MaxRecentSessions = 8;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public RenderConfig Render { get; init; } = new();

    /// <summary>How the picture goes to the screen. Whether the output is on is never saved: the application always starts with it off.</summary>
    public OutputConfig Output { get; init; } = new();

    public AudioConfig Audio { get; init; } = new();

    /// <summary>NDI source name. Whether NDI is on is never saved.</summary>
    public NdiConfig Ndi { get; init; } = new();

    /// <summary>OMT source name. Whether OMT is on is never saved.</summary>
    public OmtConfig Omt { get; init; } = new();

    /// <summary>Presets made by the operator. The built-in presets are never stored.</summary>
    public List<PresetEntry> Presets { get; init; } = [];

    /// <summary>Session files opened or saved lately, newest first. Only paths.</summary>
    public List<string> RecentSessions { get; init; } = [];

    /// <summary>Not written any more and ignored at start: the application always starts without tabs. Old files may still hold a list; it is read only so such a file stays valid.</summary>
    public List<TabEntry> Tabs { get; init; } = [];

    /// <summary>Position in <see cref="Tabs"/>; see there.</summary>
    public int ActiveTab { get; init; }
}

public sealed class TabEntry
{
    public string Name { get; init; } = string.Empty;

    public string Url { get; init; } = "about:blank";

    /// <summary>Set when the tab has its own browser profile (a separate login); names a folder, never holds credentials.</summary>
    public string? ProfileId { get; init; }

    /// <summary>
    /// What the tab shows: "web" (also when missing, as in older files), "pdf", "powerpoint" or "video". A string, not an enum, so a
    /// name from a newer version does not make the whole file unreadable.
    /// </summary>
    public string Kind { get; init; } = "web";

    /// <summary>The path of the file of a pdf, powerpoint or video tab. Never the contents, never a password; null for web tabs.</summary>
    public string? Source { get; init; }
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

public sealed class OutputConfig
{
    /// <summary>Windows device name of the chosen monitor; null until the operator picks one.</summary>
    public string? MonitorId { get; init; }

    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public Stagecast.Core.Display.ScaleMode Scale { get; init; } = Stagecast.Core.Display.ScaleMode.Squeeze;

    /// <summary>The output window covers other windows, including the taskbar.</summary>
    public bool AlwaysOnTop { get; init; } = true;
}

public sealed class OmtConfig
{
    public string Name { get; init; } = Stagecast.Core.Output.SourceNameRules.DefaultOmtName;

    /// <summary>Send the picture with transparency: the bars around a document that does not fill the frame are see-through. Only the choice is saved; the output itself is started by hand.</summary>
    public bool Alpha { get; init; }
}

public sealed class NdiConfig
{
    public string Name { get; init; } = Stagecast.Core.Output.SourceNameRules.DefaultNdiName;

    /// <summary>Send the picture with transparency: the bars around a document that does not fill the frame are see-through. Only the choice is saved; the output itself is started by hand.</summary>
    public bool Alpha { get; init; }
}

public sealed class AudioConfig
{
    /// <summary>Windows id of the local output device; null follows the Windows default device.</summary>
    public string? DeviceId { get; init; }

    /// <summary>0 to 1.</summary>
    public double Volume { get; init; } = 1;

    public bool Muted { get; init; }
}

public sealed class PresetEntry
{
    public string Name { get; init; } = string.Empty;

    public int Width { get; init; }

    public int Height { get; init; }
}
