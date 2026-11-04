using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocProc.Definitions;
using DocProc.Domain.Fields;
using DocProc.Mapping.Identification;
using DocProc.Mapping.Layouts;
using DocProc.Mapping.Tables;
using DocProc.Mapping.Text;
using DocProc.Mapping.Values;

namespace DocProc.Mapping.Learning;

/// <summary>
/// A confirmed value of a reviewed document the learner turns into field rules (docs/11 §12.6): the field's path and
/// kind, the printed text and the value as stored, and where it sits. A value without a place is learned only as a
/// <c>constant</c>, and only when <see cref="AllowConstant"/> is set (a currency, a tax rate, the supplier's name).
/// </summary>
public sealed record LearnSample(string Path, string Kind, string? Text, JsonNode? Value, int? Page, BoundingBox? Box, IReadOnlyList<string>? Options = null, bool AllowConstant = false);

/// <summary>One value of a row of a repeating group, with its place.</summary>
public sealed record LearnTableCell(string Field, int Page, BoundingBox Box, string? Text);

/// <summary>
/// A repeating group of the reviewed document (docs/11 §12.6 "a table assignment becomes column-table"): the rows with
/// the place of each value, or the review table tool's specification (<c>Correction.SourceJson.table.spec</c>), which is
/// taken as it is.
/// </summary>
public sealed record LearnTable(string Path, IReadOnlyList<IReadOnlyList<LearnTableCell>> Rows, IReadOnlyDictionary<string, TableField> RowFields, string? RowAnchor = null, JsonObject? Specification = null);

/// <summary>What the learner is given: the document's words, its confirmed values and tables, the locale, and label synonyms per path (definition and mapping labels) it prefers.</summary>
public sealed record LearnRequest(
    DocumentWords Words,
    IReadOnlyList<LearnSample> Values,
    IReadOnlyList<LearnTable> Tables,
    ValueLocale Locale,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Synonyms = null);

/// <summary>The rules learned for one field: the strategies in order, the label and pattern, and whether running them on the document reads the confirmed value.</summary>
public sealed record LearnedField(string Path, IReadOnlyList<string> Strategies, string? Label, string? Pattern, bool Verified);

/// <summary>A field the learner could not turn into a rule, and why.</summary>
public sealed record LearnSkip(string Path, string Reason);

/// <summary>The learner's answer: the field mapping (<c>MappingJson</c>), the per-field report and the fields left out.</summary>
public sealed record LearnResult(JsonObject Mapping, IReadOnlyList<LearnedField> Fields, IReadOnlyList<LearnSkip> Skipped);

/// <summary>
/// Turns the confirmed values of one reviewed document into a layout's field mapping (docs/11 §12.6, <c>AnchorLearner</c>):
/// for each value the nearest label to its left on the same line (<c>label-right</c>) or above it within 1.5 line heights
/// (<c>label-below</c>), preferring the given synonyms, with a value pattern derived from the kind and the printed shape
/// (<c>123 456789</c> gives <c>[0-9]{3}\s?[0-9]{6}</c>) and a tolerant <c>region</c> rule as fallback; a value without a
/// label is learned by its region alone. A repeating group becomes a <c>column-table</c>: the review tool's
/// specification when it has one, otherwise columns measured from the rows' values, the header row above them and the
/// first line below them as <c>stopAt</c>. Every rule is run on the document itself (<see cref="LayoutEngine"/>) and
/// kept only when it reads the confirmed value back. Pure and deterministic.
/// </summary>
public static partial class AnchorLearner
{
    /// <summary>The widest gap between two words of one label, in page units.</summary>
    public const double LabelWordGap = 15;

    /// <summary>The farthest a label may be printed to the left of its value, in page units.</summary>
    public const double MaxLabelDistance = 300;

    /// <summary>The largest tolerance of a learned region, in page units.</summary>
    public const double MaxRegionTolerance = 12;

    private const string SelfRef = "learned@0";

    public static LearnResult Learn(LearnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fields = new JsonObject();
        var report = new List<LearnedField>();
        var skipped = new List<LearnSkip>();
        // Values with a place first; a value without one that equals a learned value (a tax row's taxable amount and the
        // net total) is read by that value's rules.
        var samples = request.Values.GroupBy(v => v.Path, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var learnedValues = new List<(string? Value, JsonObject Rules, LearnedField Field)>();
        foreach (var sample in samples.OrderBy(s => s.Box is null ? 1 : 0))
        {
            if (sample.Box is null && Comparable(sample.Value) is { } value && learnedValues.FirstOrDefault(l => l.Value == value && l.Field.Strategies[0] != LayoutStrategies.Constant) is { Rules: { } same, Field: { } of }
                && Reads((JsonObject)same["rules"]![0]!, sample.Path, new LayoutFieldKind(sample.Kind, sample.Options), request, value))
            {
                fields[sample.Path] = same.DeepClone();
                report.Add(of with { Path = sample.Path });
                continue;
            }

            if (LearnField(sample, request, out var rules, out var learned, out var reason))
            {
                fields[sample.Path] = rules;
                report.Add(learned!);
                learnedValues.Add((Comparable(sample.Value), rules, learned!));
            }
            else
            {
                skipped.Add(new LearnSkip(sample.Path, reason!));
            }
        }

        foreach (var table in request.Tables)
        {
            if (LearnTableRule(table, request, out var rule, out var learned, out var reason))
            {
                fields[table.Path] = rule;
                report.Add(learned!);
            }
            else
            {
                skipped.Add(new LearnSkip(table.Path, reason!));
            }
        }

        var mapping = new JsonObject { ["locale"] = request.Locale.Name, ["fields"] = fields };
        return new LearnResult(mapping, report, skipped);
    }

    private static bool LearnField(LearnSample sample, LearnRequest request, out JsonObject rules, out LearnedField? learned, out string? reason)
    {
        rules = new JsonObject();
        learned = null;
        reason = null;
        var expected = Comparable(sample.Value);
        if (sample.Page is not { } pageNumber || sample.Box is not { } box || request.Words.Page(pageNumber) is not { } page)
        {
            if (sample.AllowConstant && sample.Value is not null && ConstantText(sample) is { } constant)
            {
                rules["rules"] = new JsonArray(new JsonObject { ["strategy"] = LayoutStrategies.Constant, ["value"] = constant });
                learned = new LearnedField(sample.Path, [LayoutStrategies.Constant], null, null, true);
                return true;
            }

            reason = "The value has no place on the page; select it on the page to learn where it is printed.";
            return false;
        }

        var valueWords = ValueWords(page, box);
        if (valueWords.Count == 0)
        {
            reason = "No words of the text layer lie where the value was taken from.";
            return false;
        }

        var kind = new LayoutFieldKind(sample.Kind, sample.Options);
        var printed = sample.Text is { Length: > 0 } t ? t : PageGeometry.ReadingText(valueWords);
        var pattern = ShapePattern(sample.Kind, printed);
        var lines = PageGeometry.Lines(page.Words);
        var synonyms = request.Synonyms?.GetValueOrDefault(sample.Path) ?? [];
        var candidates = new List<(JsonObject Rule, string Strategy, string? Label)>();
        if (sample.Kind == FieldKinds.Currency && ConstantText(sample) is { Length: 3 } code && code.All(char.IsLetter))
        {
            // A currency code is read wherever it is printed; its place on the page does not identify it.
            candidates.Add((new JsonObject { ["strategy"] = LayoutStrategies.RegexAnywhere, ["pattern"] = $"\\b({code.ToUpperInvariant()})\\b", ["group"] = 1 }, LayoutStrategies.RegexAnywhere, null));
        }

        if (LabelRight(lines, valueWords, synonyms) is { } right)
        {
            var rule = new JsonObject
            {
                ["strategy"] = LayoutStrategies.LabelRight,
                ["labels"] = new JsonArray(right.Label),
                ["page"] = PageNode(pageNumber, request.Words),
                ["maxDistance"] = Math.Ceiling(Math.Min(MaxLabelDistance, valueWords[^1].Box.X1 - right.Right + 20)),
            };
            if (pattern is not null)
            {
                rule["pattern"] = pattern;
            }

            candidates.Add((rule, LayoutStrategies.LabelRight, right.Label));
        }

        if (LabelBelow(lines, valueWords, synonyms) is { } below)
        {
            var rule = new JsonObject
            {
                ["strategy"] = LayoutStrategies.LabelBelow,
                ["labels"] = new JsonArray(below.Label),
                ["page"] = PageNode(pageNumber, request.Words),
                ["maxDistance"] = Math.Ceiling(Math.Max(10, valueWords.Max(w => w.Box.X0) - below.Left + 1)),
            };
            if (pattern is not null)
            {
                rule["pattern"] = pattern;
            }

            candidates.Add((rule, LayoutStrategies.LabelBelow, below.Label));
        }

        var union = PageGeometry.Union(valueWords)!.Value;
        var region = new JsonObject
        {
            ["strategy"] = LayoutStrategies.Region,
            ["page"] = PageNode(pageNumber, request.Words),
            ["bBox"] = new JsonArray(Math.Floor(Math.Max(0, union.X0)), Math.Floor(Math.Max(0, union.Y0)), Math.Ceiling(Math.Min(1000, union.X1)), Math.Ceiling(Math.Min(1000, union.Y1))),
            ["tolerance"] = RegionTolerance(page, valueWords, union),
        };
        if (pattern is not null)
        {
            region["pattern"] = pattern;
        }

        // Each candidate is kept only when it reads the confirmed value back on this document; the region comes last.
        var kept = new JsonArray();
        var strategies = new List<string>();
        string? label = null;
        foreach (var (rule, strategy, candidateLabel) in candidates)
        {
            if (strategies.Count == 0 && Reads(rule, sample.Path, kind, request, expected))
            {
                kept.Add(rule);
                strategies.Add(strategy);
                label = candidateLabel;
            }
        }

        var regionReads = Reads(region, sample.Path, kind, request, expected);
        if (regionReads)
        {
            kept.Add(region);
            strategies.Add(LayoutStrategies.Region);
        }

        if (kept.Count == 0 && sample.AllowConstant && sample.Value is not null && ConstantText(sample) is { } fallback)
        {
            // A currency or a code printed nowhere a rule can read it back stays what it was: a constant of the design.
            rules["rules"] = new JsonArray(new JsonObject { ["strategy"] = LayoutStrategies.Constant, ["value"] = fallback });
            learned = new LearnedField(sample.Path, [LayoutStrategies.Constant], null, null, true);
            return true;
        }

        if (kept.Count == 0)
        {
            reason = "Neither a label nor the region reads the confirmed value back on this document.";
            return false;
        }

        rules["rules"] = kept;
        learned = new LearnedField(sample.Path, strategies, label, pattern, true);
        return true;
    }

    /// <summary>Whether one rule, run on the document, reads the confirmed value (or, without one, any value).</summary>
    private static bool Reads(JsonObject rule, string path, LayoutFieldKind kind, LearnRequest request, string? expected)
    {
        var json = new JsonObject { ["fields"] = new JsonObject { [path] = new JsonObject { ["rules"] = new JsonArray(rule.DeepClone()) } } };
        if (LayoutMapping.TryParse(json.ToJsonString(), out _) is not { } mapping)
        {
            return false;
        }

        var run = LayoutEngine.Run(mapping, request.Words, SelfRef, p => p == path ? kind : null, _ => null, request.Locale);
        var hit = run.Hits.FirstOrDefault(h => h.Path == path);
        return hit is not null && (expected is null || string.Equals(Comparable(hit.Value), expected, StringComparison.Ordinal));
    }

    /// <summary>The words where the value was taken from: those whose centre lies in its box, grown by two units.</summary>
    private static List<IndexedWord> ValueWords(WordPage page, BoundingBox box)
    {
        var grown = new BoundingBox(box.X0 - 2, box.Y0 - 2, box.X1 + 2, box.Y1 + 2);
        return [.. PageGeometry.Hit(page.Words, grown).OrderBy(w => w.Box.CenterY).ThenBy(w => w.Box.X0)];
    }

    /// <summary>
    /// The label to the left of the value on its line: a synonym printed there, else the run of words nearest the
    /// value's first word (words at most <see cref="LabelWordGap"/> apart, at least one letter, no word of the value).
    /// </summary>
    private static (string Label, double Right)? LabelRight(IReadOnlyList<TextLine> lines, IReadOnlyList<IndexedWord> value, IReadOnlyList<string> synonyms)
    {
        var first = value.OrderBy(w => w.Box.X0).First();
        var line = lines.FirstOrDefault(l => l.Words.Contains(first));
        if (line is null)
        {
            return null;
        }

        var left = line.Words.Where(w => w.Box.X1 <= first.Box.X0 + 1 && !value.Contains(w)).OrderByDescending(w => w.Box.X0).ToList();
        if (left.Count == 0 || first.Box.X0 - left[0].Box.X1 > MaxLabelDistance)
        {
            return null;
        }

        // A synonym printed to the left of the value wins over the nearest words.
        foreach (var synonym in synonyms)
        {
            if (FindRun(left.OrderBy(w => w.Box.X0).ToList(), synonym) is { } run)
            {
                return (synonym, run.Max(w => w.Box.X1));
            }
        }

        var label = new List<IndexedWord> { left[0] };
        for (var i = 1; i < left.Count && label[0].Box.X0 - left[i].Box.X1 <= LabelWordGap; i++)
        {
            label.Insert(0, left[i]);
        }

        var text = string.Join(' ', label.Select(w => w.Text));
        return IsLabel(text) ? (text, label[^1].Box.X1) : null;
    }

    /// <summary>The label above the value: on the nearest line above within 1.5 line heights, the run of words that starts at the value's left edge.</summary>
    private static (string Label, double Left)? LabelBelow(IReadOnlyList<TextLine> lines, IReadOnlyList<IndexedWord> value, IReadOnlyList<string> synonyms)
    {
        var union = PageGeometry.Union(value)!.Value;
        var index = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Words.Any(value.Contains))
            {
                index = i;
                break;
            }
        }

        for (var i = index - 1; i >= 0; i--)
        {
            var above = lines[i];
            var height = Math.Max(above.Box.Height, 1);
            if (union.Y0 - above.Box.Y1 > 1.5 * height + height)
            {
                break;
            }

            var words = above.Words.Where(w => w.Box.X0 >= union.X0 - 40 && w.Box.X0 <= union.X0 + 10).OrderBy(w => w.Box.X0).ToList();
            if (words.Count == 0)
            {
                continue;
            }

            foreach (var synonym in synonyms)
            {
                var run = FindRun([.. above.Words.Where(w => w.Box.X0 >= words[0].Box.X0)], synonym);
                if (run is not null)
                {
                    return (synonym, run[0].Box.X0);
                }
            }

            var label = new List<IndexedWord> { words[0] };
            foreach (var next in above.Words.Where(w => w.Box.X0 > words[0].Box.X0).OrderBy(w => w.Box.X0))
            {
                if (next.Box.X0 - label[^1].Box.X1 > LabelWordGap)
                {
                    break;
                }

                label.Add(next);
            }

            var text = string.Join(' ', label.Select(w => w.Text));
            return IsLabel(text) ? (text, label[0].Box.X0) : null;
        }

        return null;
    }

    /// <summary>The words that spell <paramref name="label"/> consecutively, compared as identification compares words.</summary>
    private static List<IndexedWord>? FindRun(List<IndexedWord> words, string label)
    {
        var tokens = label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => IdentificationRuleEvaluator.Token(t)).ToArray();
        if (tokens.Length == 0)
        {
            return null;
        }

        for (var start = 0; start + tokens.Length <= words.Count; start++)
        {
            var all = true;
            for (var k = 0; k < tokens.Length && all; k++)
            {
                all = string.Equals(IdentificationRuleEvaluator.Token(words[start + k].Text), tokens[k], StringComparison.Ordinal);
            }

            if (all)
            {
                return [.. words.Skip(start).Take(tokens.Length)];
            }
        }

        return null;
    }

    /// <summary>A label has a letter and is not mostly digits (a value printed beside another is no label).</summary>
    private static bool IsLabel(string text)
    {
        var letters = text.Count(char.IsLetter);
        var digits = text.Count(char.IsDigit);
        return letters >= 2 && letters > digits && text.Length <= 80;
    }

    /// <summary>A region's tolerance: half the distance to the nearest other word, between 2 and <see cref="MaxRegionTolerance"/>, so the region does not take in its neighbours.</summary>
    private static double RegionTolerance(WordPage page, IReadOnlyList<IndexedWord> value, BoundingBox union)
    {
        var nearest = MaxRegionTolerance * 2;
        foreach (var w in page.Words)
        {
            if (value.Contains(w))
            {
                continue;
            }

            var dx = Math.Max(0, Math.Max(w.Box.X0 - union.X1, union.X0 - w.Box.X1));
            var dy = Math.Max(0, Math.Max(w.Box.Y0 - union.Y1, union.Y0 - w.Box.Y1));
            nearest = Math.Min(nearest, Math.Max(dx, dy));
        }

        return Math.Round(Math.Clamp(nearest / 2 - 0.5, 2, MaxRegionTolerance), 1);
    }

    /// <summary>The page of a rule: the page number, or <c>last</c> when the value is on the last page of a document with several.</summary>
    private static JsonValue PageNode(int page, DocumentWords words) =>
        words.LastPage > 1 && page == words.LastPage ? JsonValue.Create("last") : JsonValue.Create(page);

    /// <summary>
    /// A value pattern from the kind and the printed shape (docs/11 §12.6): for identifiers and strings that carry digits,
    /// each run of digits as <c>[0-9]{n}</c>, of capitals as <c>[A-Z]{n}</c>, of letters as <c>[A-Za-z]{n}</c>, spaces as
    /// <c>\s?</c> and other characters escaped; none for free text, amounts, dates and numbers (their kind reads them).
    /// </summary>
    public static string? ShapePattern(string kind, string? printed)
    {
        if (kind is not (FieldKinds.Identifier or FieldKinds.String) || string.IsNullOrWhiteSpace(printed))
        {
            return null;
        }

        var text = PrintedValues.Collapse(printed);
        if (!text.Any(char.IsDigit) || text.Length > 40)
        {
            return null;
        }

        var builder = new StringBuilder();
        var runs = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var j = i;
            string atom;
            if (char.IsDigit(c))
            {
                while (j < text.Length && char.IsDigit(text[j]))
                {
                    j++;
                }

                atom = "[0-9]";
            }
            else if (char.IsLetter(c))
            {
                var upper = true;
                while (j < text.Length && char.IsLetter(text[j]))
                {
                    upper &= char.IsUpper(text[j]);
                    j++;
                }

                atom = upper ? "[A-Z]" : "[A-Za-z]";
            }
            else if (char.IsWhiteSpace(c))
            {
                while (j < text.Length && char.IsWhiteSpace(text[j]))
                {
                    j++;
                }

                builder.Append("\\s?");
                i = j;
                continue;
            }
            else
            {
                builder.Append(Regex.Escape(c.ToString()));
                i++;
                continue;
            }

            builder.Append(atom).Append('{').Append((j - i).ToString(CultureInfo.InvariantCulture)).Append('}');
            runs++;
            i = j;
        }

        return runs is 0 or > 8 ? null : builder.ToString();
    }

    /// <summary>A value as two documents' values are compared (<see cref="Comparable"/> of the replay).</summary>
    public static string? Comparable(JsonNode? value) => ValueComparison.Comparable(value);

    private static string? ConstantText(LearnSample sample) => sample.Value switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        JsonObject o when o["amount"] is JsonValue a => a.ToJsonString(),
        _ => sample.Text,
    };

    private static bool LearnTableRule(LearnTable table, LearnRequest request, out JsonObject rule, out LearnedField? learned, out string? reason)
    {
        learned = null;
        reason = null;
        rule = table.Specification is { } spec ? FromSpecification(spec) : FromRows(table, request.Words, out reason) ?? new JsonObject();
        if (reason is not null)
        {
            return false;
        }

        var json = new JsonObject { ["fields"] = new JsonObject { [table.Path] = rule.DeepClone() } };
        if (LayoutMapping.TryParse(json.ToJsonString(), out var error) is not { } mapping)
        {
            reason = $"The table's rule does not read: {error}";
            return false;
        }

        var result = LayoutEngine.Table(mapping.Tables[0], table.RowFields, request.Words, request.Locale);
        var rows = result?.Rows.Count ?? 0;
        var expected = table.Rows.Count;
        var verified = rows > 0 && (expected == 0 || rows == expected);
        if (rows == 0)
        {
            reason = "The column table learned from the rows reads no row back on this document.";
            return false;
        }

        learned = new LearnedField(table.Path, [LayoutStrategies.ColumnTable], null, null, verified);
        return true;
    }

    /// <summary>The review table tool's specification (<c>regions:table</c>) as a layout's <c>column-table</c> rule (docs/11 §12.4: "exactly what :learn-layout turns into the layout's column-table rule").</summary>
    public static JsonObject FromSpecification(JsonObject spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var rule = new JsonObject { ["strategy"] = LayoutStrategies.ColumnTable };
        rule["bBox"] = spec["bBox"] switch
        {
            JsonObject b => new JsonArray(b["x0"]?.DeepClone(), b["y0"]?.DeepClone(), b["x1"]?.DeepClone(), b["y1"]?.DeepClone()),
            JsonArray a => a.DeepClone(),
            _ => null,
        };
        foreach (var key in new[] { "columns", "headerRow", "rowAnchor", "minAnchors", "groupHeader", "tierColumns", "skipRows", "stopAt", "pageMargins", "locale" })
        {
            if (spec[key] is { } value)
            {
                rule[key] = value.DeepClone();
            }
        }

        rule["page"] = "*";
        if (spec["pages"] is not JsonValue pages || !pages.TryGetValue<string>(out var range) || !range.Contains('-', StringComparison.Ordinal))
        {
            rule["pages"] = "page";
        }

        if (spec["repeatHeader"] is JsonValue repeat && repeat.TryGetValue<string>(out var r) && r == "none")
        {
            rule["repeatHeader"] = "none";
        }

        if (spec["continuation"] is JsonValue cont && cont.TryGetValue<string>(out var c) && c == "none")
        {
            rule["continuation"] = "none";
        }

        if (rule["columns"] is JsonArray columns)
        {
            foreach (var column in columns.OfType<JsonObject>())
            {
                foreach (var drop in column.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
                {
                    column.Remove(drop);
                }
            }
        }

        return rule;
    }

    /// <summary>
    /// A <c>column-table</c> measured from the rows' values: one column per field (from the leftmost to the rightmost
    /// value of that field on the table's first page, dividers half-way between neighbours), the header row the line
    /// above the first row, each column's header the words of that line inside it, and the first line under the last row
    /// as <c>stopAt</c>.
    /// </summary>
    private static JsonObject? FromRows(LearnTable table, DocumentWords words, out string? reason)
    {
        reason = null;
        var cells = table.Rows.SelectMany(r => r).Where(c => table.RowFields.ContainsKey(c.Field)).ToList();
        if (cells.Count == 0)
        {
            reason = "The rows have no values with a place on the page.";
            return null;
        }

        var firstPage = cells.Min(c => c.Page);
        var lastPage = cells.Max(c => c.Page);
        var onFirst = cells.Where(c => c.Page == firstPage).ToList();
        var extents = onFirst.GroupBy(c => c.Field, StringComparer.Ordinal)
            .Select(g => (Field: g.Key, X0: g.Min(c => c.Box.X0), X1: g.Max(c => c.Box.X1), Count: g.Count()))
            .OrderBy(e => e.X0)
            .ToList();
        if (words.Page(firstPage) is not { } page)
        {
            reason = "The table's page has no words.";
            return null;
        }

        var lines = PageGeometry.Lines(page.Words);
        var top = onFirst.Min(c => c.Box.Y0);
        var header = lines.Where(l => l.Box.Y1 <= top + 1 && top - l.Box.Y1 <= 4 * Math.Max(l.Box.Height, 8)).OrderByDescending(l => l.Box.Y1).FirstOrDefault();
        var columns = new JsonArray();
        for (var i = 0; i < extents.Count; i++)
        {
            var x0 = i == 0 ? Math.Max(0, extents[i].X0 - 6) : (extents[i - 1].X1 + extents[i].X0) / 2;
            var x1 = i == extents.Count - 1 ? Math.Min(1000, extents[i].X1 + 6) : (extents[i].X1 + extents[i + 1].X0) / 2;
            if (x1 <= x0)
            {
                continue;
            }

            var column = new JsonObject { ["x0"] = Math.Round(x0, 1), ["x1"] = Math.Round(x1, 1), ["field"] = extents[i].Field };
            if (header is not null)
            {
                var text = string.Join(' ', header.Words.Where(w => w.Box.CenterX >= x0 && w.Box.CenterX <= x1).Select(w => w.Text));
                if (text.Length > 0)
                {
                    column["header"] = text;
                }
            }

            columns.Add(column);
        }

        var anchor = table.RowAnchor is { } a && extents.Any(e => e.Field == a)
            ? a
            : extents.OrderByDescending(e => e.Count).ThenBy(e => e.X0).First().Field;
        var tableX0 = columns.OfType<JsonObject>().Min(c => c["x0"]!.GetValue<double>());
        var tableX1 = columns.OfType<JsonObject>().Max(c => c["x1"]!.GetValue<double>());
        var y0 = header?.Box.Y0 ?? top;
        var rule = new JsonObject
        {
            ["strategy"] = LayoutStrategies.ColumnTable,
            ["page"] = "*",
            ["bBox"] = new JsonArray(Math.Floor(tableX0), Math.Floor(y0), Math.Ceiling(tableX1), 1000.0),
            ["columns"] = columns,
            ["rowAnchor"] = anchor,
        };
        if (header is not null)
        {
            rule["headerRow"] = new JsonObject { ["y0"] = Math.Floor(header.Box.Y0), ["y1"] = Math.Ceiling(header.Box.Y1) };
        }

        if (lastPage == firstPage && words.Pages.Count == 1)
        {
            rule["pages"] = "page";
        }

        // The first line under the last row that is no row of the table ends it (a "Summe netto" or "Zwischensumme").
        if (words.Page(lastPage) is { } last)
        {
            var bottom = cells.Where(c => c.Page == lastPage).Max(c => c.Box.Y1);
            var after = PageGeometry.Lines(last.Words).FirstOrDefault(l => l.Box.Y0 >= bottom - 1 && l.Words.Any(w => w.Box.CenterX >= tableX0 && w.Box.CenterX <= tableX1) && l.Words[0].Text.Any(char.IsLetter));
            if (after is not null)
            {
                rule["stopAt"] = new JsonArray("^" + Regex.Escape(after.Words[0].Text));
            }
        }

        return rule;
    }
}
