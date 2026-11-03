namespace Stagecast.Core.Sessions;

/// <summary>What the search for a document needs from the disk; tests supply a fake.</summary>
public interface ISessionFiles
{
    bool Exists(string path);

    /// <summary>A file called <paramref name="fileName"/> in <paramref name="folder"/> or a folder below it, or null. The folder itself comes first.</summary>
    string? FindByName(string folder, string fileName);
}

/// <summary>
/// Keeps the search for documents off the network unless the operator agreed. Looking for a file on a share makes Windows connect to
/// that server and offer the user's credentials, and a session file may come from anyone, so a path it names is not probed until asked.
/// </summary>
public sealed class NetworkGuard(ISessionFiles inner, bool allowNetwork) : ISessionFiles
{
    /// <summary>A UNC path (\\server\share, also \\?\UNC\...). A device path to a local drive (\\?\C:\...) is not.</summary>
    public static bool IsNetworkPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string p = path.Replace('/', '\\');
        if (!p.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
        return !(p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal));
    }

    public bool Exists(string path) => (allowNetwork || !IsNetworkPath(path)) && inner.Exists(path);

    public string? FindByName(string folder, string fileName) => allowNetwork || !IsNetworkPath(folder) ? inner.FindByName(folder, fileName) : null;
}

public sealed class DiskFiles : ISessionFiles
{
    /// <summary>How many folders below the chosen one are looked into; a deck folder is shallow, and a whole drive must not be walked.</summary>
    public const int MaxDepth = 2;

    public bool Exists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public string? FindByName(string folder, string fileName)
    {
        try
        {
            return Search(folder, fileName, MaxDepth);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? Search(string folder, string fileName, int depth)
    {
        string direct = Path.Combine(folder, fileName);
        if (File.Exists(direct)) return direct;
        if (depth <= 0) return null;
        foreach (string child in Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (Search(child, fileName, depth - 1) is { } found) return found;
        }
        return null;
    }
}
