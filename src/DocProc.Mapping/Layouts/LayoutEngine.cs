using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocProc.Domain.Fields;
using DocProc.Mapping.Identification;
using DocProc.Mapping.Tables;
using DocProc.Mapping.Text;
using DocProc.Mapping.Values;

namespace DocProc.Mapping.Layouts;

/// <summary>The kind of a mapped field (and an enum's codes), as the type version's descriptors say.</summary>
public sealed record LayoutFieldKind(string Kind, IReadOnlyList<string>? Options = null);

/// <summary>
/// A value a field rule found (docs/11 §8.2): the field, the printed text, the value read from it by the field's kind
/// (in the shape a correction takes, <see cref="PrintedValues"/>), where it is, the rule that found it
/// (<c>nordwind-invoice-erp@1#invoiceNumber[0]</c>) and its confidence.
/// </summary>
public sealed record LayoutHit(string Path, string Kind, string Text, JsonNode? Value, int? Page, BoundingBox? Box, string RuleRef, string Strategy, double Confidence, bool Inferred = false);

/// <summary>Whether the field rules found the layout's usual coverage (docs/11 §8.2 "Drift check").</summary>
public sealed record DriftReport(int Covered, int Found, IReadOnlyList<string> Missing, IReadOnlyList<string> RequiredMissing, bool IsDrift)
{
    /// <summary>The share of covered fields found below which the layout counts as drifting.</summary>
    public const double MinCoverage = 0.7;
}

/// <summary>What the field mapping of one layout version found on a document.</summary>
public sealed record LayoutExtraction(IReadOnlyList<LayoutHit> Hits, IReadOnlyDictionary<string, TableResult> Tables, DriftReport Drift);

/// <summary>
/// Runs a layout version's field mapping over a document's words (docs/11 §8.2, the <c>supplier-mapping</c> step): for
/// each field the rules in order until one reads a value of the field's kind, then the <c>column-table</c> rules through
/// the review table tool's engine (<see cref="ColumnTableEngine"/>), with the header row found again by its column
/// headers. Pure and deterministic; it runs only for the version the identification rule selected.
/// </summary>
public static class LayoutEngine
{
    /// <summary>Confidence of a label with a matching value pattern.</summary>
    public const double LabelAndPattern = 0.97;

    /// <summary>Confidence of a label without a pattern.</summary>
    public const double LabelOnly = 0.92;

    /// <summary>Confidence of a pattern found anywhere.</summary>
    public const double Anywhere = 0.9;

    /// <summary>Confidence of a region-only hit.</summary>
    public const double RegionOnly = 0.85;

    /// <summary>The widest gap between two words of one value to the right of a label, in page units.</summary>
    public const double ValueGap = 30;

    public static LayoutExtraction Run(
        LayoutMapping mapping,
        DocumentWords words,
        string layoutRef,
        Func<string, LayoutFieldKind?> kindOf,
        Func<string, IReadOnlyDictionary<string, TableField>?> rowFieldsOf,
        ValueLocale locale,
        IReadOnlyCollection<string>? requiredPaths = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(kindOf);
        ArgumentNullException.ThrowIfNull(rowFieldsOf);
        ArgumentNullException.ThrowIfNull(locale);
        var hits = new List<LayoutHit>();
        foreach (var field in mapping.Fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (kindOf(field.Path) is not { } kind)
            {
                continue;
            }

            if (Field(field, kind, words, layoutRef, locale) is { } hit)
            {
                hits.Add(hit);
            }
        }

        var tables = new Dictionary<string, TableResult>(StringComparer.Ordinal);
        foreach (var table in mapping.Tables)
        {
            if (rowFieldsOf(table.Path) is not { } rowFields || Table(table, rowFields, words, locale, cancellationToken) is not { } result)
            {
                continue;
            }

            tables[table.Path] = result;
        }

        var coverage = mapping.Coverage;
        var found = coverage.Where(p => hits.Any(h => h.Path == p) || (tables.TryGetValue(p, out var t) && t.Rows.Count > 0)).ToHashSet(StringComparer.Ordinal);
        var missing = coverage.Where(p => !found.Contains(p)).ToList();
        var required = (requiredPaths ?? []).Concat(mapping.RequiredFields).Where(p => coverage.Contains(p, StringComparer.Ordinal) && !found.Contains(p)).Distinct(StringComparer.Ordinal).ToList();
        var drift = coverage.Count > 0 && (required.Count > 0 || (double)found.Count / coverage.Count < DriftReport.MinCoverage);
        return new LayoutExtraction(hits, tables, new DriftReport(coverage.Count, found.Count, missing, required, drift));
    }

    private static LayoutHit? Field(LayoutFieldMapping field, LayoutFieldKind kind, DocumentWords words, string layoutRef, ValueLocale locale)
    {
        for (var i = 0; i < field.Rules.Count; i++)
        {
            var rule = field.Rules[i];
            var ruleRef = $"{layoutRef}#{field.Path}[{i.ToString(CultureInfo.InvariantCulture)}]";
            if (rule.Strategy == LayoutStrategies.Constant)
            {
                var constant = PrintedValues.Parse(kind.Kind, Transform(rule.Value, field.Transforms), locale, kind.Options);
                if (constant.Value is not null)
                {
                    return new LayoutHit(field.Path, kind.Kind, rule.Value!, constant.Value, null, null, ruleRef, rule.Strategy, 0.99, Inferred: true);
                }

                continue;
            }

            foreach (var (page, text, box, confidence) in Candidates(rule, words))
            {
                var transformed = Transform(text, field.Transforms);
                var parsed = PrintedValues.Parse(kind.Kind, transformed, locale, kind.Options);
                if (parsed.Value is not null)
                {
                    return new LayoutHit(field.Path, kind.Kind, text, parsed.Value, page, box, ruleRef, rule.Strategy, confidence);
                }
            }
        }

        return null;
    }

    /// <summary>The texts a rule reads, best first: one per label occurrence, region or pattern match.</summary>
    private static IEnumerable<(int Page, string Text, BoundingBox Box, double Confidence)> Candidates(LayoutFieldRule rule, DocumentWords words)
    {
        var selector = rule.Page ?? PageSelector.Any;
        var regex = rule.Pattern is { } p ? IdentificationRuleEvaluator.Compile(p) : null;
        foreach (var page in words.Pages.Where(pg => selector.Selects(pg.Number, words.LastPage)))
        {
            switch (rule.Strategy)
            {
                case LayoutStrategies.Region:
                    var area = new BoundingBox(rule.BBox!.Value.X0 - rule.Tolerance, rule.BBox.Value.Y0 - rule.Tolerance, rule.BBox.Value.X1 + rule.Tolerance, rule.BBox.Value.Y1 + rule.Tolerance);
                    var inside = PageGeometry.Hit(page.Words, area);
                    if (inside.Count > 0 && Pick(PageGeometry.ReadingText(inside), regex, rule.Group) is { } regionText)
                    {
                        yield return (page.Number, regionText, PageGeometry.Union(inside)!.Value, regex is null ? RegionOnly : RegionOnly + 0.05);
                    }

                    break;

                case LayoutStrategies.RegexAnywhere:
                    foreach (var line in PageGeometry.Lines(page.Words))
                    {
                        if (Pick(line.Text, regex, rule.Group) is { } found)
                        {
                            yield return (page.Number, found, line.Box, Anywhere);
                        }
                    }

                    break;

                default:
                    var lines = PageGeometry.Lines(page.Words);
                    foreach (var (line, index, label) in LabelOccurrences(lines, rule.Labels!))
                    {
                        var value = rule.Strategy == LayoutStrategies.LabelRight
                            ? RightOf(line, label, rule.MaxDistance, regex is not null)
                            : Below(lines, index, label, rule.MaxDistance);
                        if (value.Count == 0)
                        {
                            continue;
                        }

                        var text = PageGeometry.ReadingText(value);
                        if (Pick(text, regex, rule.Group) is { } picked)
                        {
                            yield return (page.Number, picked, PageGeometry.Union(value)!.Value, regex is null ? LabelOnly : LabelAndPattern);
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>The text itself, or the pattern's match (its group) in it; null when the pattern does not match.</summary>
    private static string? Pick(string text, Regex? regex, int group)
    {
        if (regex is null)
        {
            return text;
        }

        try
        {
            var match = regex.Match(text);
            if (!match.Success)
            {
                return null;
            }

            return group > 0 && group < match.Groups.Count && match.Groups[group].Success ? match.Groups[group].Value : match.Value;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>Where a label is printed: its line, the line's index and the label's words (consecutive words, compared as identification compares them).</summary>
    private static IEnumerable<(TextLine Line, int Index, IReadOnlyList<IndexedWord> Label)> LabelOccurrences(IReadOnlyList<TextLine> lines, IReadOnlyList<string> labels)
    {
        var tokenized = labels.Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => IdentificationRuleEvaluator.Token(t)).ToArray()).Where(t => t.Length > 0).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var tokens = lines[i].Words.Select(w => IdentificationRuleEvaluator.Token(w.Text)).ToArray();
            foreach (var label in tokenized)
            {
                for (var start = 0; start + label.Length <= tokens.Length; start++)
                {
                    if (label.Select((t, k) => t == tokens[start + k]).All(x => x))
                    {
                        yield return (lines[i], i, [.. lines[i].Words.Skip(start).Take(label.Length)]);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The value to the right of a label on its line: the words after it within <paramref name="maxDistance"/>, up to the
    /// first gap wider than <see cref="ValueGap"/> unless a pattern picks the value out of them.
    /// </summary>
    private static List<IndexedWord> RightOf(TextLine line, IReadOnlyList<IndexedWord> label, double maxDistance, bool patterned)
    {
        var right = label[^1].Box.X1;
        var after = line.Words.Where(w => w.Box.X0 >= right - 1 && w.Box.X0 - right <= maxDistance && !label.Contains(w)).OrderBy(w => w.Box.X0).ToList();
        if (patterned || after.Count == 0)
        {
            return after;
        }

        var value = new List<IndexedWord> { after[0] };
        for (var i = 1; i < after.Count && after[i].Box.X0 - after[i - 1].Box.X1 <= ValueGap; i++)
        {
            value.Add(after[i]);
        }

        return value;
    }

    /// <summary>The value below a label: the next line within 1.5 line heights, words under the label's left edge to <paramref name="maxDistance"/> right of it.</summary>
    private static List<IndexedWord> Below(IReadOnlyList<TextLine> lines, int index, IReadOnlyList<IndexedWord> label, double maxDistance)
    {
        var labelBox = PageGeometry.Union(label)!.Value;
        var height = Math.Max(labelBox.Height, 1);
        for (var i = index + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Box.Y0 - labelBox.Y1 > 1.5 * height + height)
            {
                break;
            }

            var under = line.Words.Where(w => w.Box.CenterX >= labelBox.X0 - 10 && w.Box.X0 <= labelBox.X0 + maxDistance).ToList();
            if (under.Count > 0)
            {
                return under;
            }
        }

        return [];
    }

    /// <summary>Whether a run of the line's words reads as <paramref name="header"/> (compared as the table engine compares headers).</summary>
    private static bool HasRun(TextLine line, string header)
    {
        var normalized = line.Words.Select(w => ColumnTableEngine.NormalizeHeader(w.Text)).ToList();
        for (var start = 0; start < normalized.Count; start++)
        {
            var joined = new System.Text.StringBuilder();
            for (var end = start; end < normalized.Count && joined.Length < header.Length; end++)
            {
                joined.Append(normalized[end]);
                if (string.Equals(joined.ToString(), header, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The transforms in order (docs/11 §8.1).</summary>
    public static string? Transform(string? text, IReadOnlyList<LayoutTransform> transforms)
    {
        ArgumentNullException.ThrowIfNull(transforms);
        var value = text;
        foreach (var t in transforms)
        {
            if (value is null)
            {
                return null;
            }

            value = t.Op switch
            {
                "trim" => value.Trim(),
                "removeWhitespace" => string.Concat(value.Where(c => !char.IsWhiteSpace(c))),
                "uppercase" => value.ToUpperInvariant(),
                "regex" => Pick(value, IdentificationRuleEvaluator.Compile(t.Pattern!), t.Group),
                "replace" => Replace(value, t.Pattern!, t.With ?? string.Empty),
                _ => value,
            };
        }

        return value;
    }

    private static string Replace(string value, string pattern, string with)
    {
        var regex = IdentificationRuleEvaluator.Compile(pattern);
        try
        {
            return regex is null ? value : regex.Replace(value, with);
        }
        catch (RegexMatchTimeoutException)
        {
            return value;
        }
    }

    /// <summary>
    /// A <c>column-table</c> rule on another document of the design: the first page with a line holding the columns'
    /// headers is the mapping page (the header row is that line), the table is read from there to the foot of the page
    /// (or <c>pageMargins.bottom</c>) and on every later page, by the review tool's engine. A table without a header row
    /// is read in its box as drawn. Null when the header is found nowhere.
    /// </summary>
    public static TableResult? Table(LayoutTableMapping table, IReadOnlyDictionary<string, TableField> rowFields, DocumentWords words, ValueLocale locale, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(rowFields);
        ArgumentNullException.ThrowIfNull(words);
        if (words.Pages.Count == 0)
        {
            return null;
        }

        var headers = table.Columns.Where(c => !string.IsNullOrWhiteSpace(c.Header)).Select(c => ColumnTableEngine.NormalizeHeader(c.Header)).Where(h => h.Length > 0).ToList();
        int page;
        BoundingBox region;
        (double, double)? headerRow;
        if (table.HeaderRow is not null && headers.Count > 0)
        {
            var need = table.MinAnchors > 0 ? Math.Min(table.MinAnchors, headers.Count) : Math.Min(3, headers.Count);

            // The header row is the line on the first page that has one where most column headers are printed, each as a
            // run of whole words ("ab 10"), not as part of a longer word: a sentence such as "Staffelpreise ab 10 und
            // ab 50 Gebinden" is no header. Among equally good lines the one nearest the stored band wins.
            (int Page, TextLine? Line) found = (0, null);
            foreach (var candidate in words.Pages)
            {
                var best = PageGeometry.Lines(candidate.Words)
                    .Select(l => (Line: l, Matches: headers.Count(h => HasRun(l, h))))
                    .Where(x => x.Matches >= need)
                    .OrderByDescending(x => x.Matches)
                    .ThenBy(x => Math.Abs(x.Line.Box.Y0 - table.HeaderRow.Value.Y0))
                    .FirstOrDefault();
                if (best.Line is not null)
                {
                    found = (candidate.Number, best.Line);
                    break;
                }
            }

            if (found.Line is null)
            {
                return null;
            }

            page = found.Page;

            // The band a little wider than the line, so every word of the header row lies inside it and none is read as a row.
            headerRow = (found.Line.Box.Y0 - 1, found.Line.Box.Y1 + 1);
            region = new BoundingBox(table.Region.X0, found.Line.Box.Y0 - 1, table.Region.X1, table.MarginBottom ?? ColumnTableEngine.PageBottom);
        }
        else
        {
            page = words.Pages[0].Number;
            headerRow = table.HeaderRow;
            region = table.Region;
        }

        var tierPattern = table.TierHeaderPattern is { } tp ? IdentificationRuleEvaluator.Compile(tp, ignoreCase: true) : null;
        var spec = new ColumnTableSpec
        {
            Page = page,
            Region = region,
            Columns = [.. table.Columns.Select(c => new TableColumn(c.X0, c.X1, c.Field is { } f && rowFields.ContainsKey(f) ? f : null, c.Header, c.TierMinQuantity))],
            Fields = rowFields,
            HeaderRow = headerRow,
            RowAnchor = table.RowAnchor,
            Pages = table.AllPages ? [.. words.Pages.Where(p => p.Number >= page).Select(p => p.Number)] : [page],
            RepeatHeader = table.RepeatHeader ? RepeatHeader.Auto : RepeatHeader.None,
            MinAnchors = table.MinAnchors,
            Continuation = table.Continuation ? Continuation.AppendToPrevious : Continuation.None,
            GroupHeader = table.GroupHeaderTarget is { } target && rowFields.ContainsKey(target) ? new GroupHeaderSpec(target, table.GroupHeaderWhen) : null,
            TierColumns = table.Columns.Any(c => c.TierMinQuantity is not null) ? new TierColumnsSpec(tierPattern, table.TierTarget) : null,
            SkipRows = [.. (table.SkipRows ?? []).Select(p => IdentificationRuleEvaluator.Compile(p)).OfType<Regex>()],
            StopAt = [.. (table.StopAt ?? []).Select(p => IdentificationRuleEvaluator.Compile(p)).OfType<Regex>()],
            PageMargins = table.MarginTop is null && table.MarginBottom is null ? null : new PageMargins(table.MarginTop, table.MarginBottom),
            Locale = table.Locale is { } name && ValueLocale.TryParse(name, out var own) ? own : locale,
        };

        if (!rowFields.ContainsKey(table.RowAnchor))
        {
            return null;
        }

        try
        {
            return ColumnTableEngine.Extract(words.Pages, spec, cancellationToken);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (TablePatternTimeoutException)
        {
            return null;
        }
    }
}
