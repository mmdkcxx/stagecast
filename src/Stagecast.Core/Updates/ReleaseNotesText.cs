namespace Stagecast.Core.Updates;

public enum NoteLine { Heading, Bullet, Paragraph }

/// <summary>Turns the markdown body of a release (### headings, "- " bullets with wrapped lines) into plain lines for display.</summary>
public static class ReleaseNotesText
{
    public static IReadOnlyList<(NoteLine Kind, string Text)> Lines(string markdown)
    {
        var result = new List<(NoteLine, string)>();
        foreach (string raw in (markdown ?? "").Replace("\r", "").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Trim().Length == 0) continue;
            if (line.StartsWith('#')) result.Add((NoteLine.Heading, line.TrimStart('#').Trim()));
            else if (line.StartsWith("- ") || line.StartsWith("* ")) result.Add((NoteLine.Bullet, Clean(line[2..])));
            else if (char.IsWhiteSpace(line[0]) && result.Count > 0 && result[^1].Item1 != NoteLine.Heading)
                result[^1] = (result[^1].Item1, result[^1].Item2 + " " + Clean(line.Trim()));
            else result.Add((NoteLine.Paragraph, Clean(line.Trim())));
        }
        return result;
    }

    private static string Clean(string s) => s.Replace("**", "").Replace("`", "");
}
