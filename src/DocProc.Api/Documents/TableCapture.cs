using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocProc.Application.Configuration;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Domain.Fields;
using DocProc.Mapping.Tables;
using DocProc.Mapping.Values;

namespace DocProc.Api.Documents;

/// <summary>
/// Turns a <c>regions:table</c> request into the table engine's mapping (docs/11 §12.4) against the document's type
/// version, and the engine's result into the API's shapes and the rows the patcher writes. Every limit of the request
/// is checked here, so the engine only ever sees a mapping it can run.
/// </summary>
public static partial class TableCapture
{
    public const int MaxColumns = 40;
    public const int MaxPatterns = 20;
    public const int MaxPatternLength = 200;

    /// <summary>The most rows a response lists (the report always counts them all).</summary>
    public const int MaxRowsInResponse = 1000;

    /// <summary>The tier header pattern when the request names none: <c>ab 10</c>, <c>from 50</c>.</summary>
    public const string DefaultTierPattern = @"^(?:ab|from|von)\s*(\d+)";

    /// <summary>The kinds a column can be read into (one printed value each).</summary>
    public static readonly IReadOnlySet<string> ReadableKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        FieldKinds.String, FieldKinds.Text, FieldKinds.Identifier, FieldKinds.Integer, FieldKinds.Decimal, FieldKinds.Percent,
        FieldKinds.Money, FieldKinds.Currency, FieldKinds.Date, FieldKinds.Boolean, FieldKinds.Enum, FieldKinds.Unit,
    };

    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>The repeating group a table fills: its path, descriptor and row fields.</summary>
    public sealed record Target(string Path, FieldDescriptor Group, IReadOnlyList<FieldDescriptor> RowFields, FieldDefinition? Definition);

    /// <summary>
    /// The top-level repeating group the request names, or the type's line items (<c>roles.lineItems</c>), or its only
    /// top-level group; null with an error message otherwise.
    /// </summary>
    public static (Target? Target, string? Error) ResolveTarget(EffectiveType type, CompiledDefinition compiled, string? requested)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(compiled);
        var descriptors = compiled.Descriptors;
        var path = requested?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            path = type.Roles.LineItems;
            if (string.IsNullOrEmpty(path))
            {
                var groups = descriptors.Roots.Where(f => f.Kind == FieldKinds.Array).ToList();
                if (groups.Count != 1)
                {
                    return (null, groups.Count == 0 ? $"{type.TypeKey} has no repeating group to fill." : $"{type.TypeKey} has several repeating groups; name one in 'path' ({string.Join(", ", groups.Select(g => g.Key))}).");
                }

                path = groups[0].Key;
            }
        }

        var group = descriptors.Find(path);
        if (group is not { Kind: FieldKinds.Array } || path.Contains('.', StringComparison.Ordinal) || path.Contains('[', StringComparison.Ordinal))
        {
            return (null, $"'{path}' is not a top-level repeating group of {type.TypeKey}.");
        }

        var rowFields = group.Children.Select(descriptors.Find).OfType<FieldDescriptor>().ToList();
        var definition = type.Definition.Fields.FirstOrDefault(f => f.Key == path);
        return (new Target(path, group, rowFields, definition), null);
    }

    /// <summary>The row fields a proposal may name a column for, with the labels and extraction hints they are printed under.</summary>
    public static IReadOnlyList<ProposableField> Proposable(Target target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var definitions = target.Definition?.Item?.Fields ?? [];
        return
        [
            .. target.RowFields
                .Where(f => f.IsEnvelope && !f.ReadOnly && !f.IsList && ReadableKinds.Contains(f.Kind))
                .Select(f =>
                {
                    var definition = definitions.FirstOrDefault(d => d.Key == f.Key);
                    var labels = (definition?.Label?.Values ?? []).Concat(definition?.Extraction?.Labels?.Values.SelectMany(l => l) ?? []).ToList();
                    return new ProposableField(f.Key, labels, f.Kind, f.Options);
                }),
        ];
    }

    /// <summary>The tier header pattern of the request, or the default one.</summary>
    public static Regex TierPattern(TableTierColumnsDto? tiers, Dictionary<string, string[]> errors) =>
        Pattern(tiers?.HeaderPattern ?? DefaultTierPattern, "tierColumns.headerPattern", errors, ignoreCase: true) ?? DefaultTier();

    /// <summary>The engine's mapping for a request with columns; null with <paramref name="errors"/> filled when the request does not fit.</summary>
    public static ColumnTableSpec? Build(ReadTableRequest request, Target target, int pageCount, ValueLocale locale, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(errors);
        var columns = request.Columns ?? [];
        if (columns.Count is 0 or > MaxColumns)
        {
            errors["columns"] = [$"A table has 1 to {MaxColumns} columns."];
            return null;
        }

        var fields = new Dictionary<string, TableField>(StringComparer.Ordinal);
        var rowDefinitions = target.Definition?.Item?.Fields ?? [];
        var mapped = new List<TableColumn>();
        var previousRight = double.MinValue;
        for (var i = 0; i < columns.Count; i++)
        {
            var c = columns[i];
            if (!IsUnit(c.X0) || !IsUnit(c.X1) || c.X0 >= c.X1)
            {
                errors[$"columns[{i}]"] = ["A column lies within the page (0 to 1000) with x0 < x1."];
                continue;
            }

            if (c.X0 < previousRight - 0.01)
            {
                errors[$"columns[{i}]"] = ["Columns are given left to right and do not overlap."];
            }

            previousRight = c.X1;
            if (c.Header is { Length: > 200 })
            {
                errors[$"columns[{i}].header"] = ["A header is at most 200 characters."];
            }

            if (c.TierMinQuantity is < 0)
            {
                errors[$"columns[{i}].tierMinQuantity"] = ["A minimum quantity is 0 or more."];
            }

            if (c.Field is { } key)
            {
                var field = target.RowFields.FirstOrDefault(f => f.Key == key);
                if (field is null || !field.IsEnvelope || field.ReadOnly || field.IsList || !ReadableKinds.Contains(field.Kind))
                {
                    errors[$"columns[{i}].field"] = [$"'{key}' is not a field a column of '{target.Path}' can fill ({string.Join(", ", Proposable(target).Select(f => f.Key))})."];
                    continue;
                }

                if (fields.ContainsKey(key))
                {
                    errors[$"columns[{i}].field"] = [$"'{key}' is filled by another column already."];
                    continue;
                }

                var pattern = rowDefinitions.FirstOrDefault(d => d.Key == key)?.Pattern is { } fieldPattern ? Pattern(fieldPattern, $"columns[{i}].field", errors) : null;
                fields[key] = new TableField(key, field.Kind, pattern, field.Options);
            }

            mapped.Add(new TableColumn(c.X0, c.X1, c.Field, string.IsNullOrWhiteSpace(c.Header) ? null : c.Header.Trim(), c.TierMinQuantity));
        }

        if (!IsBox(request.BBox))
        {
            errors["bBox"] = ["The box lies within the page (0 to 1000 on both axes) with x0 < x1 and y0 < y1."];
        }

        if (request.Page < 1 || request.Page > pageCount)
        {
            errors["page"] = [$"The document has {pageCount} page(s) with text."];
        }

        (double, double)? header = null;
        if (request.HeaderRow is { } band)
        {
            if (!IsUnit(band.Y0) || !IsUnit(band.Y1) || band.Y0 >= band.Y1)
            {
                errors["headerRow"] = ["The header row lies within the page with y0 < y1."];
            }
            else
            {
                header = (band.Y0, band.Y1);
            }
        }

        var anchor = request.RowAnchor?.Trim();
        if (string.IsNullOrEmpty(anchor))
        {
            anchor = DefaultAnchor(target, mapped);
        }

        if (anchor is null || !fields.ContainsKey(anchor))
        {
            errors["rowAnchor"] = [anchor is null ? "Map a column to a field that starts each row (the row anchor)." : $"'{anchor}' is not a mapped column; the row anchor is the field of one."];
        }

        var pages = Pages(request.Pages, request.Page, pageCount, errors);
        var repeat = Choice(request.RepeatHeader, RepeatHeader.Auto, "repeatHeader", errors, ("auto", RepeatHeader.Auto), ("none", RepeatHeader.None));
        var continuation = Choice(request.Continuation, Continuation.AppendToPrevious, "continuation", errors, ("appendToPrevious", Continuation.AppendToPrevious), ("none", Continuation.None));
        if (request.MinAnchors is < 0 or > MaxColumns)
        {
            errors["minAnchors"] = [$"From 0 (the default: three, or fewer headers) to {MaxColumns}."];
        }

        GroupHeaderSpec? heading = null;
        if (request.GroupHeader is { } group)
        {
            var targetField = target.RowFields.FirstOrDefault(f => f.Key == group.Target);
            if (targetField is null || !targetField.IsEnvelope || targetField.Kind is not (FieldKinds.String or FieldKinds.Text))
            {
                errors["groupHeader.target"] = [$"'{group.Target}' is not a text field of a row of '{target.Path}'."];
            }
            else if (mapped.Any(c => c.Field == group.Target))
            {
                errors["groupHeader.target"] = [$"'{group.Target}' is filled by a column; a heading fills a field no column does."];
            }
            else
            {
                fields[group.Target] = new TableField(group.Target, targetField.Kind);
            }

            if (group.When is { } when && !fields.ContainsKey(when))
            {
                errors["groupHeader.when"] = [$"'{when}' is not a mapped column."];
            }

            heading = new GroupHeaderSpec(group.Target, group.When);
        }

        var tiers = Tiers(request, target, mapped, errors);
        var skip = Patterns(request.SkipRows, "skipRows", errors);
        var stop = Patterns(request.StopAt, "stopAt", errors);
        PageMargins? margins = null;
        if (request.PageMargins is { } m)
        {
            if ((m.Top is { } top && !IsUnit(top)) || (m.Bottom is { } bottom && !IsUnit(bottom)) || (m.Top is { } t && m.Bottom is { } b && t >= b))
            {
                errors["pageMargins"] = ["Margins lie within the page with top < bottom."];
            }
            else
            {
                margins = new PageMargins(m.Top, m.Bottom);
            }
        }

        var overrides = new List<PageOverride>();
        var requestedOverrides = request.PageOverrides ?? [];
        if (requestedOverrides.Count > pageCount)
        {
            errors["pageOverrides"] = [$"At most one override per page ({pageCount})."];
            requestedOverrides = [];
        }

        foreach (var (o, i) in requestedOverrides.Select((o, i) => (o, i)))
        {
            if (o.Page < 1 || o.Page > pageCount || overrides.Any(x => x.Page == o.Page))
            {
                errors[$"pageOverrides[{i}].page"] = ["A page of the document, at most one override each."];
                continue;
            }

            if (o.Columns is null || o.Columns.Count != columns.Count || o.Columns.Any(c => !IsUnit(c.X0) || !IsUnit(c.X1) || c.X0 >= c.X1))
            {
                errors[$"pageOverrides[{i}].columns"] = [$"One pair of dividers per column ({columns.Count}), each within the page with x0 < x1."];
                continue;
            }

            overrides.Add(new PageOverride(o.Page, [.. o.Columns.Select(c => new ColumnBounds(c.X0, c.X1))]));
        }

        if (errors.Count > 0)
        {
            return null;
        }

        return new ColumnTableSpec
        {
            Page = request.Page,
            Region = Box(request.BBox),
            Columns = mapped,
            Fields = fields,
            HeaderRow = header,
            RowAnchor = anchor!,
            Pages = pages,
            RepeatHeader = repeat,
            MinAnchors = request.MinAnchors ?? 0,
            Continuation = continuation,
            GroupHeader = heading,
            TierColumns = tiers,
            SkipRows = skip,
            StopAt = stop,
            PageMargins = margins,
            PageOverrides = overrides,
            Locale = locale,
        };
    }

    /// <summary>The group's anchor when a column fills it, else the first identifier column, else the first mapped column.</summary>
    public static string? DefaultAnchor(Target target, IEnumerable<TableColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(target);
        var mapped = columns.Where(c => c.Field is not null).Select(c => c.Field!).ToList();
        if (target.Group.Anchor is { } anchor && mapped.Contains(anchor))
        {
            return anchor;
        }

        return mapped.FirstOrDefault(f => target.RowFields.FirstOrDefault(r => r.Key == f)?.Kind == FieldKinds.Identifier) ?? mapped.FirstOrDefault();
    }

    /// <summary>
    /// The tier columns (docs/11 §8.6): on whenever a row has the tier group (<c>tiers</c> with <c>minQuantity</c> and
    /// <c>price</c>), so a column headed <c>ab 10</c> becomes tier entries without more set-up; an error when the request
    /// asks for tiers a row cannot hold.
    /// </summary>
    public static TierColumnsSpec? Tiers(ReadTableRequest request, Target target, IReadOnlyList<TableColumn> mapped, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(mapped);
        var pattern = TierPattern(request.TierColumns, errors);
        var asked = request.TierColumns ?? new TableTierColumnsDto();
        var tierTarget = asked.Target ?? "tiers";
        var quantityKey = asked.QuantityField ?? "minQuantity";
        var priceKey = asked.PriceField ?? "price";
        var tierPath = $"{target.Path}[*].{tierTarget}";
        var group = target.RowFields.FirstOrDefault(f => f.Key == tierTarget);
        if (group is { Kind: FieldKinds.Array }
            && group.Children.Contains($"{tierPath}[*].{quantityKey}", StringComparer.Ordinal)
            && group.Children.Contains($"{tierPath}[*].{priceKey}", StringComparer.Ordinal))
        {
            return new TierColumnsSpec(pattern, tierTarget, quantityKey, priceKey, TierPriceKind(target, tierTarget, priceKey));
        }

        if (request.TierColumns is not null || mapped.Any(c => c.Field is null && c.TierMinQuantity is not null))
        {
            errors["tierColumns"] = [$"A row of '{target.Path}' has no repeating group '{tierTarget}' with the fields '{quantityKey}' and '{priceKey}'."];
        }

        return null;
    }

    private static string TierPriceKind(Target target, string tierTarget, string priceKey)
    {
        var tierDefinition = target.Definition?.Item?.Fields.FirstOrDefault(f => f.Key == tierTarget);
        return tierDefinition?.Item?.Fields.FirstOrDefault(f => f.Key == priceKey)?.Kind ?? FieldKinds.Money;
    }

    /// <summary>The page list of <c>pages</c>: <c>1-*</c>, <c>2-5,7</c>, <c>*</c>; just <paramref name="page"/> when null.</summary>
    public static IReadOnlyList<int>? Pages(string? expression, int page, int pageCount, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (string.IsNullOrWhiteSpace(expression))
        {
            return [page];
        }

        var pages = new SortedSet<int>();
        foreach (var part in expression.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var match = PageRange().Match(part);
            if (!match.Success)
            {
                errors["pages"] = ["Pages are like 1-*, 2-5,7 or *."];
                return null;
            }

            var from = match.Groups["all"].Success ? 1 : int.Parse(match.Groups["from"].Value, CultureInfo.InvariantCulture);
            var to = match.Groups["all"].Success || match.Groups["open"].Success ? pageCount
                : match.Groups["to"].Success ? int.Parse(match.Groups["to"].Value, CultureInfo.InvariantCulture)
                : from;
            if (from < 1 || to > pageCount || from > to)
            {
                errors["pages"] = [$"The document has {pageCount} page(s) with text; '{part}' is not among them."];
                return null;
            }

            for (var p = from; p <= to; p++)
            {
                pages.Add(p);
            }
        }

        if (!pages.Contains(page))
        {
            errors["pages"] = [$"The pages include the page the mapping was drawn on ({page})."];
            return null;
        }

        return [.. pages];
    }

    private static TValue Choice<TValue>(string? value, TValue fallback, string key, Dictionary<string, string[]> errors, params (string Name, TValue Value)[] options)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        foreach (var (name, option) in options)
        {
            if (string.Equals(name, value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return option;
            }
        }

        errors[key] = [$"Must be {string.Join(" or ", options.Select(o => o.Name))}."];
        return fallback;
    }

    private static List<Regex> Patterns(IReadOnlyList<string>? patterns, string key, Dictionary<string, string[]> errors)
    {
        if (patterns is null)
        {
            return [];
        }

        if (patterns.Count > MaxPatterns)
        {
            errors[key] = [$"At most {MaxPatterns} patterns."];
            return [];
        }

        var compiled = new List<Regex>();
        for (var i = 0; i < patterns.Count; i++)
        {
            if (Pattern(patterns[i], $"{key}[{i}]", errors) is { } regex)
            {
                compiled.Add(regex);
            }
        }

        return compiled;
    }

    /// <summary>
    /// A pattern as the platform runs patterns (NonBacktracking, culture-invariant, a time limit per match), or null with
    /// the error; at most <see cref="MaxPatternLength"/> characters.
    /// </summary>
    private static Regex? Pattern(string? pattern, string key, Dictionary<string, string[]> errors, bool ignoreCase = false)
    {
        if (string.IsNullOrEmpty(pattern) || pattern.Length > MaxPatternLength)
        {
            errors[key] = [$"A pattern of 1 to {MaxPatternLength} characters."];
            return null;
        }

        try
        {
            return new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), PatternTimeout);
        }
        catch (ArgumentException ex)
        {
            errors[key] = [$"Not a regular expression: {ex.Message}"];
        }
        catch (NotSupportedException)
        {
            errors[key] = ["The pattern uses a construct the platform does not run (backreferences, lookarounds)."];
        }

        return null;
    }

    private static Regex DefaultTier() => new(DefaultTierPattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, PatternTimeout);

    public static bool IsUnit(double value) => double.IsFinite(value) && value is >= 0 and <= CorrectionSources.PageUnits;

    public static bool IsBox(SourceBox? box) => box is not null && IsUnit(box.X0) && IsUnit(box.Y0) && IsUnit(box.X1) && IsUnit(box.Y1) && box.X0 < box.X1 && box.Y0 < box.Y1;

    public static BoundingBox Box(SourceBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        return new BoundingBox(box.X0, box.Y0, box.X1, box.Y1);
    }

    public static SourceBox Box(BoundingBox box) => new(Math.Round(box.X0, 2), Math.Round(box.Y0, 2), Math.Round(box.X1, 2), Math.Round(box.Y1, 2));

    /// <summary>The engine's report in the API's shape (also how it is stored, <c>DocumentTableReport.ReportJson</c>).</summary>
    public static TableReportDto ToDto(TableReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new TableReportDto(
            report.Rows,
            report.RejectedRows,
            report.Pages,
            report.PagesWithoutRows,
            report.StoppedOnPage,
            [
                .. report.PerPage.Select(p => new TablePageReportDto(
                    p.Page,
                    p.HeaderFound,
                    p.Rows,
                    p.Rejected,
                    [.. p.RejectedLines.Select(r => new TableRejectedLineDto(r.Page, Truncate(r.Text, CorrectionSources.MaxRawTextLength), Box(r.Box), r.Reason, r.Message, r.Field))],
                    p.SkippedLines,
                    p.GroupHeadings,
                    p.ContinuationLines,
                    [.. p.Columns.Select(c => new TableColumnBoundsDto(c.X0, c.X1))],
                    p.ColumnsFrom,
                    p.FirstRows)),
            ]);
    }

    public static TableRowDto ToDto(TableRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new TableRowDto(row.Page, Box(row.Box), [.. row.Cells.Select(Cell)], [.. row.Tiers.Select(t => new TableTierDto(t.MinQuantity, Cell(t.Price)))]);
    }

    private static TableCellDto Cell(TableCell cell) =>
        new(cell.Field, cell.Text, cell.Value is null ? null : JsonSerializer.SerializeToElement(cell.Value), cell.Page, Box(cell.Box));

    /// <summary>The rows as the patcher writes them: every value with its page, box and printed text (Human provenance, docs/11 §12.6).</summary>
    public static IReadOnlyList<CapturedRow> ToCaptured(IReadOnlyList<TableRow> rows, TierColumnsSpec? tiers)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return
        [
            .. rows.Select(row =>
            {
                var cells = row.Cells.Where(c => c.Value is not null).Select(Captured).ToList();
                IReadOnlyDictionary<string, IReadOnlyList<CapturedRow>>? groups = null;
                if (tiers is not null && row.Tiers.Count > 0)
                {
                    groups = new Dictionary<string, IReadOnlyList<CapturedRow>>(StringComparer.Ordinal)
                    {
                        [tiers.Target] =
                        [
                            .. row.Tiers.Select(t => new CapturedRow(
                            [
                                new CapturedCell(tiers.QuantityField, JsonSerializer.SerializeToElement(t.MinQuantity), Source(t.Price)),
                                Captured(t.Price with { Field = tiers.PriceField }),
                            ])),
                        ],
                    };
                }

                return new CapturedRow(cells, groups);
            }),
        ];
    }

    private static CapturedCell Captured(TableCell cell) => new(cell.Field, JsonSerializer.SerializeToElement(cell.Value), Source(cell));

    private static FieldSource Source(TableCell cell) => new(SourceMethod.Human, cell.Page, Rounded(cell.Box), Truncate(cell.Text, CorrectionSources.MaxRawTextLength));

    /// <summary>A box to two decimals of a page unit: finer than any print, and shorter in the stored tree of a long list.</summary>
    private static BoundingBox Rounded(BoundingBox box) => new(Math.Round(box.X0, 2), Math.Round(box.Y0, 2), Math.Round(box.X1, 2), Math.Round(box.Y1, 2));

    /// <summary>
    /// The request as the specification of the capture (what <c>Correction.SourceJson.table.spec</c> keeps, and what
    /// <c>:learn-layout</c> turns into a <c>column-table</c> rule in P4): the request without <c>write</c> and
    /// <c>reason</c>, with the path and row anchor resolved.
    /// </summary>
    public static JsonObject Specification(ReadTableRequest request, string path, string rowAnchor)
    {
        ArgumentNullException.ThrowIfNull(request);
        var spec = (JsonObject)JsonSerializer.SerializeToNode(request with { Write = false, Reason = null, Path = path, RowAnchor = rowAnchor }, SpecJson)!;
        spec.Remove("write");
        spec.Remove("reason");
        return spec;
    }

    private static readonly JsonSerializerOptions SpecJson = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    [GeneratedRegex(@"^(?:(?<all>\*)|(?<from>\d{1,5})(?:-(?:(?<open>\*)|(?<to>\d{1,5})))?)$", RegexOptions.CultureInvariant)]
    private static partial Regex PageRange();
}
