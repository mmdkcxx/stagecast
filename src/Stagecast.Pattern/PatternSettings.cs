using System.IO;
using System.Text.Json;

namespace Stagecast.Pattern;

/// <summary>What the user typed in the window; saved between runs.</summary>
public sealed class PatternSettings
{
    public const int MinSize = 64, MaxSize = 4096, MaxSeconds = 3600, MaxText = 40;

    public static readonly int[] FpsChoices = [24, 25, 30, 50, 60];

    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;

    public int Fps { get; set; } = 50;

    public int Seconds { get; set; } = 30;

    public string Title { get; set; } = "PANGGUNG UTAMA";

    public string Subtitle { get; set; } = "OUTPUT A - CEK SINYAL";

    public int ToneHz { get; set; } = 1000;

    public double LevelDb { get; set; } = -20;

    public long TotalFrames => (long)Fps * Seconds;

    public PatternSettings Clone() => (PatternSettings)MemberwiseClone();

    // Each check returns the message to show under the field, or null when the value is fine.
    public static string? CheckSize(int value, string name) =>
        value < MinSize || value > MaxSize ? $"{name} harus {MinSize}-{MaxSize}" : value % 2 != 0 ? $"{name} harus genap (syarat H.264)" : null;

    public static string? CheckSeconds(int value) => value < 1 || value > MaxSeconds ? $"Durasi harus 1-{MaxSeconds} detik" : null;

    public static string? CheckTone(int value) => value < 100 || value > 8000 ? "Nada harus 100-8000 Hz" : null;

    public static string? CheckLevel(double value) => value < -60 || value > 0 ? "Level harus -60 sampai 0 dBFS" : null;

    /// <summary>The most the finished file can take, in bytes (video bitrate plus 192 kbit/s of audio); a still pattern usually comes out far smaller.</summary>
    public long EstimatedBytes() => (long)((Mp4Writer.VideoBitrate(Width, Height, Fps) + 192_000.0) / 8 * Seconds);

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StagecastPattern", "settings.json");

    public static PatternSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<PatternSettings>(File.ReadAllText(FilePath)) is { } s) return s;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { /* start from the defaults */ }

        return new PatternSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* settings are a convenience only */ }
    }
}
