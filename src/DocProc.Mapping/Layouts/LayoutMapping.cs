using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Domain.Fields;
using DocProc.Domain.Validation;
using DocProc.Mapping.Identification;

namespace DocProc.Mapping.Layouts;

/// <summary>The field rule strategies this engine runs (docs/11 §8.1); the structured ones arrive with their layers.</summary>
public static class LayoutStrategies
{
    public const string LabelRight = "label-right";
    public const string LabelBelow = "label-below";
    public const string Region = "region";
    public const string RegexAnywhere = "regex-anywhere";
    public const string Constant = "constant";
    public const string ColumnTable = "column-table";

    /// <summary>The strategies of docs/11 §8.1 not run yet: structured sources (P6) and the source facts.</summary>
    public static readonly IReadOnlyList<string> NotYet = ["xml-path", "json-path", "cell", "source", "key-value-block", "label-row"];

    public static readonly IReadOnlyList<string> Supported = [LabelRight, LabelBelow, Region, RegexAnywhere, Constant];
}

/// <summary>A transform of the printed text before it is read as the field's kind (docs/11 §8.1 "Transforms").</summary>
public sealed record LayoutTransform(string Op, string? Pattern = null, int Group = 0, string? With = null);

/// <summary>One rule of a field: a strategy and its parameters (docs/11 §8.1).</summary>
public sealed record LayoutFieldRule(
    string Strategy,
    IReadOnlyList<string>? Labels = null,
    PageSelector? Page = null,
    double MaxDistance = 250,
    string? Pattern = null,
    int Group = 0,
    BoundingBox? BBox = null,
    double Tolerance = 25,
    string? Value = null);

/// <summary>The rules of one field, tried in order (the first that reads wins), and its transforms.</summary>
public sealed record LayoutFieldMapping(string Path, IReadOnlyList<LayoutFieldRule> Rules, IReadOnlyList<LayoutTransform> Transforms);

/// <summary>
/// A <c>column-table</c> rule (docs/11 §8.6), in the shape the review table tool's specification takes (§12.4, the request
/// of <c>regions:table</c>): the columns as drawn on the page the layout was learned from, with their header texts, which
/// find the header row again on another document of the design.
/// </summary>
public sealed record LayoutTableMapping(
    string Path,
    BoundingBox Region,
    (double Y0, double Y1)? HeaderRow,
    IReadOnlyList<LayoutTableColumn> Columns,
    string RowAnchor,
    int MinAnchors = 0,
    bool AllPages = true,
    bool RepeatHeader = true,
    bool Continuation = true,
    string? GroupHeaderTarget = null,
    string? GroupHeaderWhen = null,
    string? TierHeaderPattern = null,
    string TierTarget = "tiers",
    IReadOnlyList<string>? SkipRows = null,
    IReadOnlyList<string>? StopAt = null,
    double? MarginTop = null,
    double? MarginBottom = null,
    string? Locale = null);

/// <summary>One column of a <see cref="LayoutTableMapping"/>.</summary>
public sealed record LayoutTableColumn(double X0, double X1, string? Field, string? Header, decimal? TierMinQuantity = null);

/// <summary>A layout default (docs/11 §8.1 <c>defaults</c>): a value, or a supplier attribute, written when the field is missing.</summary>
public sealed record LayoutDefault(string Path, string? Value, string? FromSupplierAttribute);

/// <summary>A field mapping that could not be read; <see cref="Path"/> names the place.</summary>
public sealed class LayoutMappingException : Exception
{
    public LayoutMappingException()
        : this(string.Empty, "The field mapping could not be read.")
    {
    }

    public LayoutMappingException(string message)
        : this(string.Empty, message)
    {
    }

    public LayoutMappingException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Path = string.Empty;
    }

    public LayoutMappingException(string path, string message)
        : base(message)
    {
        Path = path;
    }

    public string Path { get; }
}

/// <summary>
/// A layout version's field mapping (docs/11 §8.1, <c>SupplierLayoutVersion.MappingJson</c>): <c>fields</c> (each field's
/// rules and transforms, or a <c>column-table</c> rule for a repeating group), <c>defaults</c>, <c>requiredFields</c> and
/// <c>llmHints</c>. Field keys are definition paths; a member of a composite is addressed with a dot (<c>supplier.vatId</c>).
/// </summary>
public sealed class LayoutMapping
{
    public const int MaxFields = 200;
    public const int MaxRulesPerField = 10;
    public const int MaxPatternLength = 500;

    private LayoutMapping(IReadOnlyList<LayoutFieldMapping> fields, IReadOnlyList<LayoutTableMapping> tables, IReadOnlyList<LayoutDefault> defaults, IReadOnlyList<string> requiredFields, string? culture)
    {
        Fields = fields;
        Tables = tables;
        Defaults = defaults;
        RequiredFields = requiredFields;
        Culture = culture;
    }

    public static LayoutMapping Empty { get; } = new([], [], [], [], null);

    public IReadOnlyList<LayoutFieldMapping> Fields { get; }

    public IReadOnlyList<LayoutTableMapping> Tables { get; }

    public IReadOnlyList<LayoutDefault> Defaults { get; }

    public IReadOnlyList<string> RequiredFields { get; }

    /// <summary>The locale printed values are read in (<c>locale</c>, for example <c>de-DE</c>); null for the supplier's or the tenant's.</summary>
    public string? Culture { get; }

    /// <summary>The paths the mapping covers (<c>CoverageJson</c>): every field with a rule and every table.</summary>
    public IReadOnlyList<string> Coverage => [.. Fields.Select(f => f.Path).Concat(Tables.Select(t => t.Path)).Distinct(StringComparer.Ordinal)];

    public static LayoutMapping Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new LayoutMappingException(string.Empty, $"The field mapping is not JSON: {ex.Message}");
        }

        if (node is not JsonObject root)
        {
            throw new LayoutMappingException(string.Empty, "A field mapping is a JSON object.");
        }

        var fields = new List<LayoutFieldMapping>();
        var tables = new List<LayoutTableMapping>();
        if (root["fields"] is JsonObject fieldMap)
        {
            if (fieldMap.Count > MaxFields)
            {
                throw new LayoutMappingException("fields", $"A mapping has at most {MaxFields} fields.");
            }

            foreach (var (path, value) in fieldMap)
            {
                var at = $"fields.{path}";
                if (value is not JsonObject field)
                {
                    throw new LayoutMappingException(at, "A field's mapping is an object with rules (or a column-table rule).");
                }

                if (Str(field, "strategy", at) == LayoutStrategies.ColumnTable)
                {
                    tables.Add(ReadTable(path, field, at));
                    continue;
                }

                fields.Add(ReadField(path, field, at));
            }
        }
        else if (root.ContainsKey("fields"))
        {
            throw new LayoutMappingException("fields", "'fields' is an object of field paths.");
        }

        var defaults = new List<LayoutDefault>();
        if (root["defaults"] is JsonObject defaultMap)
        {
            foreach (var (path, value) in defaultMap)
            {
                if (value is not JsonObject d)
                {
                    throw new LayoutMappingException($"defaults.{path}", "A default is {\"value\": …} or {\"fromSupplierAttribute\": …}.");
                }

                var constant = d["value"] is JsonValue v ? v.ToString() : null;
                var attribute = Str(d, "fromSupplierAttribute", $"defaults.{path}");
                if ((constant is null) == (attribute is null))
                {
                    throw new LayoutMappingException($"defaults.{path}", "A default has exactly one of value and fromSupplierAttribute.");
                }

                defaults.Add(new LayoutDefault(path, constant, attribute));
            }
        }

        var required = root["requiredFields"] is JsonArray r
            ? r.Select(x => x is JsonValue rv && rv.TryGetValue<string>(out var s) ? s : throw new LayoutMappingException("requiredFields", "requiredFields lists field paths.")).ToList()
            : [];
        var culture = Str(root, "locale", string.Empty);
        return new LayoutMapping(fields, tables, defaults, required, culture);
    }

    /// <summary>Tries <see cref="Parse"/>; null with the error otherwise.</summary>
    public static LayoutMapping? TryParse(string? json, out string? error)
    {
        error = null;
        try
        {
            return Parse(json);
        }
        catch (LayoutMappingException ex)
        {
            error = ex.Path.Length > 0 ? $"{ex.Path}: {ex.Message}" : ex.Message;
            return null;
        }
    }

    private static LayoutFieldMapping ReadField(string path, JsonObject field, string at)
    {
        if (field["rules"] is not JsonArray rules || rules.Count == 0 || rules.Count > MaxRulesPerField)
        {
            throw new LayoutMappingException($"{at}.rules", $"A field has 1 to {MaxRulesPerField} rules.");
        }

        var read = new List<LayoutFieldRule>();
        for (var i = 0; i < rules.Count; i++)
        {
            var ruleAt = $"{at}.rules[{i.ToString(CultureInfo.InvariantCulture)}]";
            if (rules[i] is not JsonObject rule)
            {
                throw new LayoutMappingException(ruleAt, "A rule is an object with a strategy.");
            }

            var strategy = Str(rule, "strategy", ruleAt) ?? throw new LayoutMappingException(ruleAt, "A rule names its strategy.");
            if (!LayoutStrategies.Supported.Contains(strategy, StringComparer.Ordinal))
            {
                throw new LayoutMappingException($"{ruleAt}.strategy", LayoutStrategies.NotYet.Contains(strategy, StringComparer.Ordinal)
                    ? $"The strategy '{strategy}' is not run yet; use {string.Join(", ", LayoutStrategies.Supported)}."
                    : $"'{strategy}' is not a strategy; use {string.Join(", ", LayoutStrategies.Supported)} or column-table.");
            }

            var labels = rule["labels"] is JsonArray l ? l.Select(x => x?.GetValue<string>() ?? string.Empty).Where(s => s.Length > 0).ToList() : null;
            var pattern = Str(rule, "pattern", ruleAt);
            CheckPattern(pattern, $"{ruleAt}.pattern");
            var box = Box(rule["bBox"], $"{ruleAt}.bBox");
            switch (strategy)
            {
                case LayoutStrategies.LabelRight or LayoutStrategies.LabelBelow when labels is not { Count: > 0 }:
                    throw new LayoutMappingException($"{ruleAt}.labels", $"{strategy} needs at least one label.");
                case LayoutStrategies.Region when box is null:
                    throw new LayoutMappingException($"{ruleAt}.bBox", "region needs a bBox.");
                case LayoutStrategies.RegexAnywhere when pattern is null:
                    throw new LayoutMappingException($"{ruleAt}.pattern", "regex-anywhere needs a pattern.");
                case LayoutStrategies.Constant when rule["value"] is null:
                    throw new LayoutMappingException($"{ruleAt}.value", "constant needs a value.");
            }

            read.Add(new LayoutFieldRule(
                strategy,
                labels,
                Page(rule["page"], $"{ruleAt}.page"),
                Num(rule, "maxDistance", ruleAt) ?? 250,
                pattern,
                (int)(Num(rule, "group", ruleAt) ?? 0),
                box,
                Num(rule, "tolerance", ruleAt) ?? 25,
                rule["value"] is JsonValue cv ? cv.ToString() : null));
        }

        var transforms = new List<LayoutTransform>();
        if (field["transforms"] is JsonArray ts)
        {
            for (var i = 0; i < ts.Count; i++)
            {
                var tAt = $"{at}.transforms[{i.ToString(CultureInfo.InvariantCulture)}]";
                if (ts[i] is not JsonObject t || Str(t, "op", tAt) is not { } op)
                {
                    throw new LayoutMappingException(tAt, "A transform is an object with an op.");
                }

                if (op is not ("trim" or "removeWhitespace" or "uppercase" or "regex" or "replace"))
                {
                    throw new LayoutMappingException($"{tAt}.op", $"'{op}' is not a transform here; use trim, removeWhitespace, uppercase, regex or replace (numbers and dates are read by the field's kind).");
                }

                var tPattern = Str(t, "pattern", tAt);
                CheckPattern(tPattern, $"{tAt}.pattern");
                if (op is "regex" or "replace" && tPattern is null)
                {
                    throw new LayoutMappingException($"{tAt}.pattern", $"{op} needs a pattern.");
                }

                transforms.Add(new LayoutTransform(op, tPattern, (int)(Num(t, "group", tAt) ?? 0), Str(t, "with", tAt)));
            }
        }

        return new LayoutFieldMapping(path, read, transforms);
    }

    private static LayoutTableMapping ReadTable(string path, JsonObject table, string at)
    {
        if (path.Contains('.', StringComparison.Ordinal) || path.Contains('[', StringComparison.Ordinal))
        {
            throw new LayoutMappingException(at, "A column-table fills a top-level repeating group.");
        }

        var box = Box(table["bBox"], $"{at}.bBox") ?? throw new LayoutMappingException($"{at}.bBox", "A column-table names the table's box on the page it was drawn on.");
        if (table["columns"] is not JsonArray columns || columns.Count == 0 || columns.Count > 40)
        {
            throw new LayoutMappingException($"{at}.columns", "A column-table has 1 to 40 columns.");
        }

        var read = new List<LayoutTableColumn>();
        for (var i = 0; i < columns.Count; i++)
        {
            var cAt = $"{at}.columns[{i.ToString(CultureInfo.InvariantCulture)}]";
            if (columns[i] is not JsonObject c || Num(c, "x0", cAt) is not { } x0 || Num(c, "x1", cAt) is not { } x1 || x0 >= x1)
            {
                throw new LayoutMappingException(cAt, "A column has x0 < x1 in page units.");
            }

            read.Add(new LayoutTableColumn(x0, x1, Str(c, "field", cAt), Str(c, "header", cAt), (decimal?)Num(c, "tierMinQuantity", cAt)));
        }

        (double, double)? header = table["headerRow"] is JsonObject h && Num(h, "y0", at) is { } y0 && Num(h, "y1", at) is { } y1 ? (y0, y1) : null;
        var anchor = Str(table, "rowAnchor", at) ?? read.FirstOrDefault(c => c.Field is not null)?.Field
                     ?? throw new LayoutMappingException($"{at}.rowAnchor", "A column-table needs a row anchor field.");
        var skip = Strings(table["skipRows"], $"{at}.skipRows");
        var stop = Strings(table["stopAt"], $"{at}.stopAt");
        var tiers = table["tierColumns"] as JsonObject;
        var group = table["groupHeader"] as JsonObject;
        var margins = table["pageMargins"] as JsonObject;
        return new LayoutTableMapping(
            path,
            box,
            header,
            read,
            anchor,
            (int)(Num(table, "minAnchors", at) ?? 0),
            !string.Equals(table["pages"]?.ToString(), "page", StringComparison.Ordinal),
            !string.Equals(Str(table, "repeatHeader", at), "none", StringComparison.Ordinal),
            !string.Equals(Str(table, "continuation", at), "none", StringComparison.Ordinal),
            group is null ? null : Str(group, "target", $"{at}.groupHeader"),
            group is null ? null : Str(group, "when", $"{at}.groupHeader"),
            tiers is null ? null : Str(tiers, "headerPattern", $"{at}.tierColumns"),
            tiers is null ? "tiers" : Str(tiers, "target", $"{at}.tierColumns") ?? "tiers",
            skip,
            stop,
            margins is null ? null : Num(margins, "top", at),
            margins is null ? null : Num(margins, "bottom", at),
            Str(table, "locale", at));
    }

    private static List<string>? Strings(JsonNode? node, string at)
    {
        if (node is null)
        {
            return null;
        }

        if (node is not JsonArray array || array.Count > 20)
        {
            throw new LayoutMappingException(at, "A list of at most 20 patterns.");
        }

        var list = new List<string>();
        foreach (var item in array)
        {
            var s = item is JsonValue v && v.TryGetValue<string>(out var text) ? text : throw new LayoutMappingException(at, "Patterns are strings.");
            CheckPattern(s, at);
            list.Add(s);
        }

        return list;
    }

    private static void CheckPattern(string? pattern, string at)
    {
        if (pattern is null)
        {
            return;
        }

        if (pattern.Length > MaxPatternLength)
        {
            throw new LayoutMappingException(at, $"The pattern is longer than {MaxPatternLength} characters.");
        }

        if (PatternSize.Problem(pattern) is { } problem)
        {
            throw new LayoutMappingException(at, $"The pattern repeats too much: {problem}");
        }

        if (IdentificationRuleEvaluator.Compile(pattern) is null)
        {
            throw new LayoutMappingException(at, "The pattern does not compile (NonBacktracking: no backreferences or lookarounds).");
        }
    }

    private static PageSelector? Page(JsonNode? node, string at) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<int>(out var n) && n >= 1 => new PageSelector(n, false),
        JsonValue v when v.TryGetValue<string>(out var s) && s == "last" => new PageSelector(null, true),
        JsonValue v when v.TryGetValue<string>(out var s) && s is "any" or "*" => PageSelector.Any,
        _ => throw new LayoutMappingException(at, "'page' is a page number, \"last\" or \"any\"."),
    };

    private static BoundingBox? Box(JsonNode? node, string at)
    {
        if (node is null)
        {
            return null;
        }

        if (node is not JsonArray { Count: 4 } b || b.Any(x => x is not JsonValue xv || !xv.TryGetValue<double>(out _)))
        {
            throw new LayoutMappingException(at, "A box is [x0, y0, x1, y1] in page units.");
        }

        var v = b.Select(x => x!.GetValue<double>()).ToArray();
        if (v.Any(x => x is < 0 or > 1000) || v[0] >= v[2] || v[1] >= v[3])
        {
            throw new LayoutMappingException(at, "A box lies within the page (0 to 1000) with x0 < x1 and y0 < y1.");
        }

        return new BoundingBox(v[0], v[1], v[2], v[3]);
    }

    private static string? Str(JsonObject obj, string key, string at)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new LayoutMappingException(at.Length == 0 ? key : $"{at}.{key}", $"'{key}' is a string.");
    }

    private static double? Num(JsonObject obj, string key, string at)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        return node is JsonValue v && v.TryGetValue<double>(out var d) ? d : throw new LayoutMappingException(at.Length == 0 ? key : $"{at}.{key}", $"'{key}' is a number.");
    }
}
