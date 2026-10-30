using System.Text;
using System.Text.RegularExpressions;

namespace DocProc.Domain.Common;

/// <summary>
/// A number that came from a document (invoice, PO, delivery note number): stored verbatim plus a normalised
/// form used for matching and duplicate detection (docs/02-data-model.md conventions).
/// </summary>
public sealed record DocumentIdentifier(string Verbatim, string Normalized)
{
    public static DocumentIdentifier Create(string verbatim)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verbatim);
        return new DocumentIdentifier(verbatim, Identifiers.Normalize(verbatim));
    }

    public bool Matches(DocumentIdentifier other) =>
        string.Equals(Normalized, other.Normalized, StringComparison.Ordinal);

    public override string ToString() => Verbatim;
}

public static partial class Identifiers
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[\s.\-/]")]
    private static partial Regex CompactSeparators();

    /// <summary>Trim, upper-case, collapse internal whitespace.</summary>
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Whitespace().Replace(value.Trim(), " ").ToUpperInvariant();
    }

    /// <summary>Upper-case and strip whitespace, dots, dashes and slashes. Used for VAT ids and IBANs.</summary>
    public static string? NormalizeCompact(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : CompactSeparators().Replace(value, string.Empty).ToUpperInvariant();

    // Latin-1 Supplement and Latin Extended-A letters with their ASCII base letters. Done by table rather than
    // Unicode decomposition so the result does not depend on ICU being available (InvariantGlobalization).
    private const string Accented =
        "ÀÁÂÃÄÅÆÇÈÉÊËÌÍÎÏÐÑÒÓÔÕÖØÙÚÛÜÝÞàáâãäåæçèéêëìíîïðñòóôõöøùúûüýþÿ" +
        "ĀāĂăĄąĆćĈĉĊċČčĎďĐđĒēĔĕĖėĘęĚěĜĝĞğĠġĢģĤĥĦħĨĩĪīĬĭĮįİıĲĳĴĵĶķĸĹĺĻļĽľĿŀŁłŃńŅņŇňŉŊŋŌōŎŏŐőŒœŔŕŖŗŘřŚśŜŝŞşŠšŢţŤťŦŧŨũŪūŬŭŮůŰűŲųŴŵŶŷŸŹźŻżŽžſ";

    private const string Plain =
        "AAAAAAACEEEEIIIIDNOOOOOOUUUUYTaaaaaaaceeeeiiiidnoooooouuuuyty" +
        "AaAaAaCcCcCcCcDdDdEeEeEeEeEeGgGgGgGgHhHhIiIiIiIiIiIiJjKkkLlLlLlLlLlNnNnNnnNnOoOoOoOoRrRrRrSsSsSsSsTtTtTtUuUuUuUuUuUuWwYyYZzZzZzs";

    /// <summary>Letters and digits only, upper-cased, diacritics folded to ASCII. Used for fuzzy name comparison.</summary>
    public static string NormalizeName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            var ch = c;
            var idx = Accented.IndexOf(ch, StringComparison.Ordinal);
            if (idx >= 0)
            {
                ch = Plain[idx];
            }

            if (ch == 'ß')
            {
                sb.Append("SS");
            }
            else if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToUpperInvariant(ch));
            }
            else if (char.IsWhiteSpace(ch) && sb.Length > 0 && sb[^1] != ' ')
            {
                sb.Append(' ');
            }
        }

        return sb.ToString().Trim();
    }
}
