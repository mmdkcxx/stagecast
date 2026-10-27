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
            string? profileId = TabRules.IsValidProfileId(entry.ProfileId) && !tabs.Any(t => t.ProfileId == entry.ProfileId) ? entry.ProfileId : null;
            if (entry.ProfileId is not null && profileId is null) notes.Add($"tab '{name}' had an unusable profile id; it uses the shared profile now");
            tabs.Add(new TabEntry { Name = name, Url = string.IsNullOrWhiteSpace(entry.Url) ? TabRules.BlankUrl : entry.Url.Trim(), ProfileId = profileId });
        }
        int activeTab = tabs.Count == 0 ? 0 : Math.Clamp(input.ActiveTab, 0, tabs.Count - 1);

        if (input.SchemaVersion > AppConfig.CurrentSchemaVersion)
            notes.Add($"the file is from a newer version (schema {input.SchemaVersion}); unknown settings may be lost on the next save");

        return new AppConfig
        {
            SchemaVersion = AppConfig.CurrentSchemaVersion,
            Render = new RenderConfig { Width = width, Height = height, Fps = fps },
            Presets = presets,
            Tabs = tabs,
            ActiveTab = activeTab,
        };
    }
}
