using System.Globalization;

namespace Stagecast.Core.Updates;

/// <summary>A semantic version such as 1.2.1 or 1.2.2-beta.1. A leading "v" is accepted. A pre-release sorts before its release.</summary>
public sealed class AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
    private readonly string[] _pre;

    private AppVersion(int major, int minor, int patch, string[] pre)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _pre = pre;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public bool IsPreRelease => _pre.Length > 0;

    public static bool TryParse(string? text, out AppVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        string[] pre = [];
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..].Split('.');
            if (pre.Any(p => p.Length == 0)) return false;
            s = s[..dash];
        }
        string[] parts = s.Split('.');
        if (parts.Length != 3) return false;
        if (!TryNumber(parts[0], out int major) || !TryNumber(parts[1], out int minor) || !TryNumber(parts[2], out int patch)) return false;
        version = new AppVersion(major, minor, patch, pre);
        return true;
    }

    private static bool TryNumber(string s, out int n) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (_pre.Length == 0 && other._pre.Length == 0) return 0;
        if (_pre.Length == 0) return 1;
        if (other._pre.Length == 0) return -1;
        for (int i = 0; i < Math.Min(_pre.Length, other._pre.Length); i++)
        {
            bool a = TryNumber(_pre[i], out int na), b = TryNumber(other._pre[i], out int nb);
            c = a && b ? na.CompareTo(nb) : a ? -1 : b ? 1 : string.CompareOrdinal(_pre[i], other._pre[i]);
            if (c != 0) return c;
        }
        return _pre.Length.CompareTo(other._pre.Length);
    }

    public bool Equals(AppVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => Equals(obj as AppVersion);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', _pre));

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (_pre.Length > 0 ? "-" + string.Join('.', _pre) : "");

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;

    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
}
