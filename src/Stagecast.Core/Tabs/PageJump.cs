namespace Stagecast.Core.Tabs;

/// <summary>Reads the page number the operator typed into the page box of a document tab.</summary>
public static class PageJump
{
    /// <summary>
    /// True when <paramref name="text"/> is a whole number from 1 to <paramref name="pageCount"/> (spaces around it are fine);
    /// <paramref name="pageIndex"/> is then the zero-based index of that page. Anything else (empty, letters, signs, decimals,
    /// zero, past the end, too large for a number) is not a page and changes nothing.
    /// </summary>
    public static bool TryParse(string? text, int pageCount, out int pageIndex)
    {
        pageIndex = -1;
        if (pageCount <= 0 || text is null) return false;
        string trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 9 || !trimmed.All(char.IsAsciiDigit)) return false;
        int number = int.Parse(trimmed, System.Globalization.CultureInfo.InvariantCulture);
        if (number < 1 || number > pageCount) return false;
        pageIndex = number - 1;
        return true;
    }

    /// <summary>What to tell the operator when <see cref="TryParse"/> said no.</summary>
    public static string Hint(int pageCount) => pageCount == 1 ? "Dokumen ini hanya punya 1 halaman." : $"Ketik nomor halaman 1 sampai {pageCount}.";
}
