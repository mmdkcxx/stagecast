namespace Stagecast.Core.Output;

public enum SourceNameError
{
    None,
    Empty,
    TooLong,
    InvalidCharacter,
}

/// <summary>The name a receiver (vMix, NDI Monitor) shows for this application's NDI or OMT source.</summary>
public static class SourceNameRules
{
    public const int MaxLength = 40;
    public const string DefaultNdiName = "Stagecast";
    public const string DefaultOmtName = "Stagecast";

    public static SourceNameError Validate(string? name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return SourceNameError.Empty;
        if (trimmed.Length > MaxLength) return SourceNameError.TooLong;
        return trimmed.Any(char.IsControl) ? SourceNameError.InvalidCharacter : SourceNameError.None;
    }

    public static string Message(SourceNameError error) => error switch
    {
        SourceNameError.Empty => "Nama sumber tidak boleh kosong.",
        SourceNameError.TooLong => $"Nama sumber maksimal {MaxLength} karakter.",
        SourceNameError.InvalidCharacter => "Nama sumber tidak boleh berisi karakter kontrol.",
        _ => string.Empty,
    };

    /// <summary>The name to keep in a settings file: trimmed, or the default when it cannot be used.</summary>
    public static string Repair(string? name, string fallback)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length > MaxLength) trimmed = trimmed[..MaxLength].TrimEnd();
        return Validate(trimmed) == SourceNameError.None ? trimmed : fallback;
    }
}
