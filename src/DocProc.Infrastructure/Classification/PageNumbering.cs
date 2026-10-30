using System.Globalization;
using System.Text.RegularExpressions;

namespace DocProc.Infrastructure.Classification;

/// <summary>A printed page number such as "Seite 1/3", "Page 2 of 2", "Stranica 1 od 2", "Pagina 3 di 3", "Page 1 sur 2".</summary>
internal sealed record PageNumber(int Current, int? Total, string Text);

/// <summary>Finds printed page numbers; a reset to 1 on a later page is the strongest "document start" signal of docs/03 stage 3.</summary>
internal static partial class PageNumbering
{
    [GeneratedRegex(
        @"(?<!\p{L})(?:Seite|Blatt|Page|Pagina|Pag\.?|Stranica)\s*(?<current>\d{1,3})(?:\s*(?:/|von|of|od|di|sur|-)\s*(?<total>\d{1,3}))?(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static PageNumber? Find(string text)
    {
        foreach (Match match in Pattern().Matches(text))
        {
            var current = int.Parse(match.Groups["current"].Value, CultureInfo.InvariantCulture);
            int? total = match.Groups["total"].Success ? int.Parse(match.Groups["total"].Value, CultureInfo.InvariantCulture) : null;
            if (current >= 1 && (total is null || current <= total))
            {
                return new PageNumber(current, total, match.Value);
            }
        }

        return null;
    }
}
