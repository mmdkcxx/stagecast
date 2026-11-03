using System.Text.Json;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Presets;
using Stagecast.Core.Rendering;
using Stagecast.Core.Tabs;

namespace Stagecast.Core.Configuration;

public enum ConfigLoadStatus
{
    /// <summary>The file was read as it was.</summary>
    Loaded,

    /// <summary>There was no file; defaults were used and written.</summary>
    CreatedDefault,

    /// <summary>The file was readable but some values were unusable and were replaced by defaults.</summary>
    Repaired,

    /// <summary>The file could not be parsed. It was moved aside and defaults were used.</summary>
    RecoveredFromCorrupt,
}

public sealed record ConfigLoadResult(AppConfig Config, ConfigLoadStatus Status, IReadOnlyList<string> Notes);

/// <summary>Reads and writes the settings file. Loading never throws for a bad file: the operator always gets a working configuration.</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly ILogger _logger;

    public ConfigStore(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
    }

    public ConfigLoadResult Load()
    {
        if (!File.Exists(_path))
        {
            var created = new AppConfig();
            Save(created);
            _logger.LogInformation("No config at {Path}; wrote defaults", _path);
            return new ConfigLoadResult(created, ConfigLoadStatus.CreatedDefault, []);
        }

        AppConfig? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return Recover(ex);
        }
        if (parsed is null) return Recover(new JsonException("the file is empty or 'null'"));

        var notes = new List<string>();
        var config = Normalize(parsed, notes);
        if (notes.Count == 0) return new ConfigLoadResult(config, ConfigLoadStatus.Loaded, notes);

        foreach (string note in notes) _logger.LogWarning("Config repaired: {Note}", note);
        Save(config);
        return new ConfigLoadResult(config, ConfigLoadStatus.Repaired, notes);
    }

    /// <summary>Writes through a temporary file so a crash while saving cannot leave a half-written settings file.</summary>
    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, Json));
        if (File.Exists(_path)) File.Replace(temp, _path, null);
        else File.Move(temp, _path);
    }

    private ConfigLoadResult Recover(Exception cause)
    {
        string moved = $"{_path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
        string note = $"could not read the file ({cause.Message})";
        try
        {
            File.Move(_path, moved);
            note += $"; kept a copy as {Path.GetFileName(moved)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            note += "; could not move it aside";
        }
        _logger.LogError("Config unusable: {Note}", note);

        var config = new AppConfig();
        Save(config);
        return new ConfigLoadResult(config, ConfigLoadStatus.RecoveredFromCorrupt, [note]);
    }

    private static AppConfig Normalize(AppConfig input, List<string> notes)
    {
        int width = input.Render.Width, height = input.Render.Height, fps = input.Render.Fps;
        if (!Resolution.TryCreate(width, height, out _))
        {
            notes.Add($"resolution {width}x{height} is outside {Resolution.MinSide}-{Resolution.MaxSide}; using {RenderConfig.DefaultWidth}x{RenderConfig.DefaultHeight}");
            width = RenderConfig.DefaultWidth;
            height = RenderConfig.DefaultHeight;
        }
        if (fps is not (30 or 60))
        {
            notes.Add($"fps {fps} is not 30 or 60; using {RenderConfig.DefaultFps}");
            fps = RenderConfig.DefaultFps;
        }

        // Keep presets in order, dropping the ones that would not have been accepted when created.
        var accepted = new List<Preset>(PresetRules.BuiltIns);
        var presets = new List<PresetEntry>();
        foreach (var entry in input.Presets ?? [])
        {
            if (!Resolution.TryCreate(entry.Width, entry.Height, out var resolution))
            {
                notes.Add($"preset '{entry.Name}' has an invalid resolution and was dropped");
                continue;
            }
            var error = PresetRules.ValidateNew(entry.Name, resolution, accepted);
            if (error != PresetError.None)
            {
                notes.Add($"preset '{entry.Name}' was dropped ({error})");
                continue;
            }
            accepted.Add(new Preset(entry.Name.Trim(), resolution));
            presets.Add(new PresetEntry { Name = entry.Name.Trim(), Width = entry.Width, Height = entry.Height });
        }

        var tabs = new List<TabEntry>();
        foreach (var entry in input.Tabs ?? [])
        {
            if (tabs.Count >= TabRules.MaxTabs)
            {
                notes.Add($"more than {TabRules.MaxTabs} tabs; the rest were dropped");
                break;
            }
            string name = (entry.Name ?? string.Empty).Trim();
            if (TabRules.ValidateName(name) == TabNameError.Empty) name = TabRules.NextDefaultName(tabs.Select(t => new TabInfo(Guid.Empty, t.Name, t.Url)));
            else if (name.Length > TabRules.MaxNameLength) name = name[..TabRules.MaxNameLength].TrimEnd();
            if (!TabKinds.TryParse(entry.Kind, out var kind)) notes.Add($"tab '{name}' has an unknown kind '{entry.Kind}'; it is a web tab now");
            if (TabKinds.IsDocument(kind))
            {
                // A file tab has no address and no browser profile; only the path of its file.
                string? source = TabRules.StorableSource(entry.Source);
                if (entry.Source is not null && source is null) notes.Add($"tab '{name}' had an unusable file path; the file has to be chosen again");
                tabs.Add(new TabEntry { Name = name, Url = TabRules.BlankUrl, Kind = TabKinds.ConfigName(kind), Source = source });
                continue;
            }
            string? profileId = TabRules.IsValidProfileId(entry.ProfileId) && !tabs.Any(t => t.ProfileId == entry.ProfileId) ? entry.ProfileId : null;
            if (entry.ProfileId is not null && profileId is null) notes.Add($"tab '{name}' had an unusable profile id; it uses the shared profile now");
            tabs.Add(new TabEntry { Name = name, Url = string.IsNullOrWhiteSpace(entry.Url) ? TabRules.BlankUrl : entry.Url.Trim(), ProfileId = profileId });
        }
        int activeTab = tabs.Count == 0 ? 0 : Math.Clamp(input.ActiveTab, 0, tabs.Count - 1);

        var recent = new List<string>();
        foreach (string path in input.RecentSessions ?? [])
        {
            string? stored = TabRules.StorableSource(path);
            if (stored is null || stored.Contains('\0') || recent.Contains(stored, StringComparer.OrdinalIgnoreCase)) continue;
            if (recent.Count >= AppConfig.MaxRecentSessions) break;
            recent.Add(stored);
        }

        if (input.SchemaVersion > AppConfig.CurrentSchemaVersion)
            notes.Add($"the file is from a newer version (schema {input.SchemaVersion}); unknown settings may be lost on the next save");

        var output = input.Output ?? new OutputConfig();
        var scale = output.Scale;
        if (!Enum.IsDefined(scale))
        {
            notes.Add($"output scale mode {(int)scale} is unknown; using squeeze");
            scale = Stagecast.Core.Display.ScaleMode.Squeeze;
        }
        string? monitorId = string.IsNullOrWhiteSpace(output.MonitorId) || output.MonitorId.Length > 64 ? null : output.MonitorId.Trim();

        var audio = input.Audio ?? new AudioConfig();
        double volume = double.IsNaN(audio.Volume) ? 1 : Math.Clamp(audio.Volume, 0, 1);
        if (volume != audio.Volume) notes.Add($"audio volume {audio.Volume} is outside 0-1; using {volume}");
        string? deviceId = string.IsNullOrWhiteSpace(audio.DeviceId) || audio.DeviceId.Length > 200 ? null : audio.DeviceId.Trim();

        var ndi = input.Ndi ?? new NdiConfig();
        string ndiName = Stagecast.Core.Output.SourceNameRules.Repair(ndi.Name, Stagecast.Core.Output.SourceNameRules.DefaultNdiName);
        if (ndiName != ndi.Name) notes.Add($"NDI source name '{ndi.Name}' is not usable; using '{ndiName}'");

        var omt = input.Omt ?? new OmtConfig();
        string omtName = Stagecast.Core.Output.SourceNameRules.Repair(omt.Name, Stagecast.Core.Output.SourceNameRules.DefaultOmtName);
        if (omtName != omt.Name) notes.Add($"OMT source name '{omt.Name}' is not usable; using '{omtName}'");

        return new AppConfig
        {
            SchemaVersion = AppConfig.CurrentSchemaVersion,
            Omt = new OmtConfig { Name = omtName, Alpha = omt.Alpha },
            Ndi = new NdiConfig { Name = ndiName, Alpha = ndi.Alpha },
            Audio = new AudioConfig { DeviceId = deviceId, Volume = volume, Muted = audio.Muted },
            Render = new RenderConfig { Width = width, Height = height, Fps = fps },
            Output = new OutputConfig { MonitorId = monitorId, Scale = scale, AlwaysOnTop = output.AlwaysOnTop },
            Presets = presets,
            RecentSessions = recent,
            Tabs = tabs,
            ActiveTab = activeTab,
        };
    }
}
