using System.IO;

namespace Stagecast.Spike;

/// <summary>Appends timestamped lines to logs/spike.log next to the exe, so the build script can read the result.</summary>
internal static class SpikeLog
{
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "logs", "spike.log");

    public static void Write(string message)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
    }
}
