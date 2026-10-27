using Stagecast.Core.Configuration;
using Stagecast.Core.Rendering;

namespace Stagecast.Core.Presets;

/// <summary>The built-in presets followed by the operator's own, in the order they are shown. Changes return a new catalog.</summary>
public sealed class PresetCatalog
{
    private PresetCatalog(IReadOnlyList<Preset> all) => All = all;

    public IReadOnlyList<Preset> All { get; }

    public static PresetCatalog FromConfig(AppConfig config)
    {
        var list = new List<Preset>(PresetRules.BuiltIns);
        foreach (var entry in config.Presets)
        {
            if (!Resolution.TryCreate(entry.Width, entry.Height, out var resolution)) continue;
            if (PresetRules.ValidateNew(entry.Name, resolution, list) != PresetError.None) continue;
            list.Add(new Preset(entry.Name.Trim(), resolution));
        }
        return new PresetCatalog(list);
    }

    /// <summary>The operator's own presets, as they are stored in the settings file.</summary>
    public List<PresetEntry> ToEntries()
        => All.Where(p => !p.BuiltIn).Select(p => new PresetEntry { Name = p.Name, Width = p.Resolution.Width, Height = p.Resolution.Height }).ToList();

    public PresetError TryAdd(string? name, Resolution resolution, out PresetCatalog result)
    {
        var error = PresetRules.ValidateNew(name, resolution, All);
        result = error == PresetError.None ? new PresetCatalog([.. All, new Preset(name!.Trim(), resolution)]) : this;
        return error;
    }

    /// <summary>Removes a preset of the operator's. Built-in presets and unknown ones are left alone.</summary>
    public bool TryRemove(Preset preset, out PresetCatalog result)
    {
        bool ok = PresetRules.CanDelete(preset) && All.Contains(preset);
        result = ok ? new PresetCatalog(All.Where(p => p != preset).ToList()) : this;
        return ok;
    }

    /// <summary>The preset with exactly this resolution, if there is one.</summary>
    public Preset? Match(Resolution resolution) => All.FirstOrDefault(p => p.Resolution == resolution);
}
