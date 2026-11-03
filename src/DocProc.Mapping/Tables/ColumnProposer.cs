using System.Text.RegularExpressions;
using DocProc.Definitions;
using DocProc.Domain.Fields;
using DocProc.Mapping.Text;
using DocProc.Mapping.Values;

namespace DocProc.Mapping.Tables;

/// <summary>
/// A row field the proposer may assign a column to, with the labels it is printed under (definition labels and
/// extraction hints) and its kind, which the column's printed values must read as.
/// </summary>
public sealed record ProposableField(string Key, IReadOnlyList<string> Labels, string Kind = FieldKinds.Text, IReadOnlyList<string>? Options = null);

/// <summary>A proposed column: its dividers, the header printed above it, the field its header names and, for a tier column, its minimum quantity.</summary>
public sealed record ProposedColumn(double X0, double X1, string? Header, string? Field, decimal? TierMinQuantity);

/// <summary>What the proposer read from the box the reviewer drew: the header row's band (if any) and the columns.</summary>
public sealed record TableProposal((double Y0, double Y1)? HeaderRow, IReadOnlyList<ProposedColumn> Columns);

/// <summary>
/// Proposes column dividers for a table the reviewer drew a box around (docs/11 §12.4 step 2, <c>regions:table</c>
/// without columns): from the x gaps that run through every body line, and the header words above them. A column's
/// header is matched against the row fields' labels, so a column printed "Art.-Nr." is proposed for <c>supplierSku</c>,
/// and a header such as <c>ab 10</c> is proposed as a tier column. A field is only proposed for a column whose printed
/// values read as its kind (at least <see cref="ReadableShare"/> of them): an "Inhalt" column printing <c>6 x 0,75 l</c>
/// is not proposed for a numeric pack size, it is left for the reviewer to assign.
/// </summary>
public static class ColumnProposer
{
    /// <summary>The narrowest gap between two columns, in page units.</summary>
    public const int MinGap = 8;

    /// <summary>The share of body lines that may cross a gap (a heading or a long description line) without closing it.</summary>
    public const double CrossingShare = 0.1;

    /// <summary>The share of a column's printed values that must read as a field's kind for the field to be proposed.</summary>
    public const double ReadableShare = 0.8;

    public static TableProposal Propose(WordPage page, BoundingBox region, (double Y0, double Y1)? headerRow, IReadOnlyList<ProposableField> fields, Regex? tierPattern, ValueLocale? locale = null)
    {
        locale ??= ValueLocale.German;
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(fields);
        var words = PageGeometry.Hit(page.Words, region);
        var lines = PageGeometry.Lines(words);
        if (lines.Count == 0)
        {
            return new TableProposal(headerRow, []);
        }

        // The header: the band given, or the first line in the box.
        IReadOnlyList<IndexedWord> header;
        (double Y0, double Y1) band;
        if (headerRow is { } given)
        {
            header = [.. words.Where(w => w.Box.CenterY >= given.Y0 && w.Box.CenterY <= given.Y1)];
            band = given;
        }
        else
        {
            header = lines[0].Words;
            band = (lines[0].Box.Y0, lines[0].Box.Y1);
        }

        var body = lines.Where(l => l.Box.CenterY > band.Y1).ToList();
        var sample = body.Count > 0 ? body : lines;

        // Coverage per page unit across the box: how many body lines print something there. The header words always
        // cover, so a gap never runs through a header.
        var x0 = (int)Math.Floor(region.X0);
        var x1 = (int)Math.Ceiling(region.X1);
        var width = Math.Max(1, x1 - x0);
        var coverage = new int[width];
        var headerCover = new bool[width];
        foreach (var line in sample)
        {
            var covered = new bool[width];
            foreach (var word in line.Words)
            {
                Mark(covered, word.Box, x0);
            }

            for (var i = 0; i < width; i++)
            {
                coverage[i] += covered[i] ? 1 : 0;
            }
        }

        foreach (var word in header)
        {
            Mark(headerCover, word.Box, x0);
        }

        var allowed = (int)Math.Floor(sample.Count * CrossingShare);
        var first = Array.FindIndex(coverage, c => c > 0);
        var last = Array.FindLastIndex(coverage, c => c > 0);
        var dividers = new List<double>();
        if (first >= 0)
        {
            var start = -1;
            for (var i = first; i <= last + 1; i++)
            {
                var open = i <= last && coverage[i] <= allowed && !headerCover[i];
                if (open && start < 0)
                {
                    start = i;
                }
                else if (!open && start >= 0)
                {
                    if (i - start >= MinGap)
                    {
                        dividers.Add(x0 + ((start + i) / 2.0));
                    }

                    start = -1;
                }
            }
        }

        var bounds = new List<(double X0, double X1)>();
        var left = region.X0;
        foreach (var divider in dividers)
        {
            bounds.Add((left, divider));
            left = divider;
        }

        bounds.Add((left, region.X1));

        var used = new HashSet<string>(StringComparer.Ordinal);
        var columns = new List<ProposedColumn>();
        foreach (var (c0, c1) in bounds)
        {
            if (!words.Any(w => w.Box.CenterX >= c0 && w.Box.CenterX < c1))
            {
                continue;
            }

            var headerText = PageGeometry.ReadingText(header.Where(w => w.Box.CenterX >= c0 && w.Box.CenterX < c1));
            var tier = ColumnTableEngine.TierQuantity(tierPattern, headerText);
            var cells = body.Select(l => PrintedValues.Collapse(string.Join(' ', l.Words.Where(w => w.Box.CenterX >= c0 && w.Box.CenterX < c1).Select(w => w.Text)))).Where(t => t.Length > 0).ToList();
            var field = tier is null ? Suggest(headerText, fields, used, f => Reads(f, cells, locale)) : null;
            if (field is not null)
            {
                used.Add(field);
            }

            columns.Add(new ProposedColumn(Math.Round(c0, 2), Math.Round(c1, 2), headerText.Length == 0 ? null : headerText, field, tier));
        }

        return new TableProposal(headerRow ?? band, columns);
    }

    private static void Mark(bool[] covered, BoundingBox box, int x0)
    {
        var from = Math.Max(0, (int)Math.Floor(box.X0) - x0);
        var to = Math.Min(covered.Length - 1, (int)Math.Ceiling(box.X1) - x0);
        for (var i = from; i <= to; i++)
        {
            covered[i] = true;
        }
    }

    /// <summary>
    /// The field whose label or key is the header (normalised), else one whose label the header starts with or that
    /// starts with the header, among those whose kind the column's values read as.
    /// </summary>
    private static string? Suggest(string header, IReadOnlyList<ProposableField> fields, HashSet<string> used, Func<ProposableField, bool> reads)
    {
        var normalized = ColumnTableEngine.NormalizeHeader(header);
        if (normalized.Length == 0)
        {
            return null;
        }

        var free = fields.Where(f => !used.Contains(f.Key)).ToList();
        var exact = free.Where(f => f.Labels.Append(f.Key).Any(l => ColumnTableEngine.NormalizeHeader(l) == normalized));
        var partial = free.Where(f => f.Labels.Append(f.Key).Select(ColumnTableEngine.NormalizeHeader).Any(l => l.Length >= 3 && (normalized.StartsWith(l, StringComparison.Ordinal) || l.StartsWith(normalized, StringComparison.Ordinal))));
        return exact.Concat(partial).FirstOrDefault(reads)?.Key;
    }

    /// <summary>Whether at least <see cref="ReadableShare"/> of a column's printed values read as the field's kind (a column without values reads as anything).</summary>
    private static bool Reads(ProposableField field, List<string> cells, ValueLocale locale) =>
        cells.Count == 0 || cells.Count(c => PrintedValues.Parse(field.Kind, c, locale, field.Options).Ok) >= cells.Count * ReadableShare;
}
