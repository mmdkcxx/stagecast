using Stagecast.Core.Rendering;

namespace Stagecast.Core.Presets;

public enum PresetError
{
    None,
    NameEmpty,
    NameTooLong,
    NameInUse,
    ResolutionInUse,
}

public static class PresetRules
{
    public const int MaxNameLength = 24;

    public static readonly IReadOnlyList<Preset> BuiltIns =
    [
        new("LED 7×3", new Resolution(2520, 1080), BuiltIn: true),
        new("Full HD", new Resolution(1920, 1080), BuiltIn: true),
        new("4K UHD", new Resolution(3840, 2160), BuiltIn: true),
        new("Vertikal", new Resolution(1080, 1920), BuiltIn: true),
    ];

    /// <summary>Checks a new preset against the existing ones. Names are compared ignoring case and surrounding spaces.</summary>
    public static PresetError ValidateNew(string? name, Resolution resolution, IEnumerable<Preset> existing)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return PresetError.NameEmpty;
        if (trimmed.Length > MaxNameLength) return PresetError.NameTooLong;

        var list = existing as IReadOnlyCollection<Preset> ?? existing.ToList();
        if (list.Any(p => string.Equals(p.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
            return PresetError.NameInUse;
        if (list.Any(p => p.Resolution == resolution))
            return PresetError.ResolutionInUse;

        return PresetError.None;
    }

    /// <summary>Built-in presets cannot be deleted. Deleting a preset never changes the active resolution.</summary>
    public static bool CanDelete(Preset preset) => !preset.BuiltIn;
}
