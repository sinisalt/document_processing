using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Domain.Fields;

namespace DocProc.Mapping.Identification;

/// <summary>The kinds of identifier of docs/11 §8.5.</summary>
public enum IdentifierKind
{
    /// <summary>An exact word or phrase on one line.</summary>
    Text,

    /// <summary>A regular expression over the joined text of the region.</summary>
    Regex,

    /// <summary>A value of the supplier's master data printed on the page (<c>vatId</c>, <c>taxId</c>, <c>iban</c>, <c>attributes.&lt;key&gt;</c>).</summary>
    SupplierValue,

    /// <summary>A source fact of the ingestion (<c>itemKind</c>, <c>fileName</c>, <c>subject</c>, <c>mailbox</c>, <c>folderPath</c>).</summary>
    Source,

    /// <summary>An element of a structured XML source (P6; refused at save until then).</summary>
    XmlPath,

    /// <summary>A cell of a tabular source (P6; refused at save until then).</summary>
    Cell,
}

/// <summary>Which page an identifier looks at: a page number, the last page, or any page (the default).</summary>
public readonly record struct PageSelector(int? Number, bool Last)
{
    public static PageSelector Any { get; } = new(null, false);

    public bool IsAny => Number is null && !Last;

    public bool Selects(int page, int lastPage) => Last ? page == lastPage : Number is not { } n || n == page;

    public override string ToString() => Last ? "last" : Number is { } n ? n.ToString(CultureInfo.InvariantCulture) : "any";
}

/// <summary>
/// Where an identifier looks (docs/11 §8.5 "Where"): a page, a named region (<c>title</c>, <c>header</c>, <c>footer</c>,
/// <c>body</c>) or a box with a tolerance. Each key is inherited from the enclosing groups one by one, so an identifier's
/// own key overrides only that key.
/// </summary>
public sealed record IdentifierLocation(PageSelector? Page = null, string? Region = null, BoundingBox? BBox = null, double? Tolerance = null)
{
    public static IdentifierLocation None { get; } = new();

    /// <summary>The default tolerance of a <c>bBox</c>, in page units.</summary>
    public const double DefaultTolerance = 30;

    /// <summary>This location's keys over <paramref name="inherited"/>'s.</summary>
    public IdentifierLocation Over(IdentifierLocation inherited)
    {
        ArgumentNullException.ThrowIfNull(inherited);

        // A box and a named region are one key in two spellings: an own box replaces an inherited region and the other way round.
        var ownArea = Region is not null || BBox is not null;
        return new IdentifierLocation(
            Page ?? inherited.Page,
            ownArea ? Region : inherited.Region,
            ownArea ? BBox : inherited.BBox,
            ownArea ? Tolerance : inherited.Tolerance);
    }

    /// <summary>The area of the page this location covers, in the normalised 1000 × 1000 space.</summary>
    public BoundingBox Area()
    {
        if (BBox is { } box)
        {
            var t = Tolerance ?? DefaultTolerance;
            return new BoundingBox(box.X0 - t, box.Y0 - t, box.X1 + t, box.Y1 + t);
        }

        return Region switch
        {
            IdentificationRegions.Title => new BoundingBox(0, 0, 1000, 300),
            IdentificationRegions.Header => new BoundingBox(0, 0, 1000, 150),
            IdentificationRegions.Footer => new BoundingBox(0, 850, 1000, 1000),
            IdentificationRegions.Body => new BoundingBox(0, 150, 1000, 850),
            _ => new BoundingBox(0, 0, 1000, 1000),
        };
    }

    public string Describe()
    {
        var parts = new List<string>();
        if (Page is { IsAny: false } page)
        {
            parts.Add(page.Last ? "last page" : $"page {page}");
        }

        if (Region is { } region)
        {
            parts.Add($"{region} area");
        }
        else if (BBox is { } b)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"box [{b.X0:0}, {b.Y0:0}, {b.X1:0}, {b.Y1:0}]"));
        }

        return parts.Count == 0 ? "anywhere" : string.Join(", ", parts);
    }
}

/// <summary>The named regions of docs/11 §8.5.</summary>
public static class IdentificationRegions
{
    public const string Title = "title";
    public const string Header = "header";
    public const string Footer = "footer";
    public const string Body = "body";

    public static readonly IReadOnlyList<string> All = [Title, Header, Footer, Body];

    /// <summary>The y band of the title area (0 to 300): where a word proposed as a title identifier must sit.</summary>
    public const double TitleBottom = 300;
}

/// <summary>A node of an identification rule: an identifier or a combination group.</summary>
public abstract record IdentificationNode(IdentifierLocation Location);

/// <summary>One component of the document (docs/11 §8.5 "Identifiers").</summary>
public sealed record Identifier(
    IdentifierKind Kind,
    string Value,
    IdentifierLocation Location,
    bool CaseSensitive = false,
    bool AllowOcrErrors = false,
    string? Pattern = null,
    string? EqualsValue = null) : IdentificationNode(Location)
{
    /// <summary>A short English description for evidence and the test report (<c>text "Rechnung" on page 1, title area</c>).</summary>
    public string Describe(IdentifierLocation effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        var what = Kind switch
        {
            IdentifierKind.Text => $"text \"{Value}\"",
            IdentifierKind.Regex => $"pattern /{Value}/",
            IdentifierKind.SupplierValue => $"the supplier's {Value}",
            IdentifierKind.Source => Pattern is { } p ? $"source {Value} matching /{p}/" : $"source {Value} = \"{EqualsValue}\"",
            IdentifierKind.XmlPath => $"xml path {Value}",
            _ => $"cell {Value}",
        };
        return Kind is IdentifierKind.Source ? what : $"{what} ({effective.Describe()})";
    }
}

/// <summary>A combination group: <c>all</c>, <c>any</c> and <c>none</c>, an implicit AND of the ones present (docs/11 §8.5).</summary>
public sealed record IdentifierGroup(
    IReadOnlyList<IdentificationNode>? All,
    IReadOnlyList<IdentificationNode>? Any,
    IReadOnlyList<IdentificationNode>? None,
    IdentifierLocation Location) : IdentificationNode(Location);

/// <summary>A rule that could not be read; <see cref="Path"/> names the place (<c>all[1].text</c>).</summary>
public sealed class IdentificationRuleException : Exception
{
    public IdentificationRuleException()
        : this(string.Empty, "The identification rule could not be read.")
    {
    }

    public IdentificationRuleException(string message)
        : this(string.Empty, message)
    {
    }

    public IdentificationRuleException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Path = string.Empty;
    }

    public IdentificationRuleException(string path, string message)
        : base(message)
    {
        Path = path;
    }

    public string Path { get; }
}

/// <summary>
/// An identification rule (docs/11 §8.5): a boolean combination of identifiers, evaluated strictly by
/// <see cref="IdentificationRuleEvaluator"/>. Stored as <c>SupplierLayoutVersion.IdentificationJson</c>; the format's
/// meta-schema is <see cref="IdentificationRuleSchema"/> (<c>schemas/identification-rule.v1.json</c>).
/// </summary>
public sealed class IdentificationRule
{
    /// <summary>The deepest nesting of groups (the rule itself is depth 1).</summary>
    public const int MaxDepth = 3;

    /// <summary>The most identifiers a rule holds, across all its groups.</summary>
    public const int MaxIdentifiers = 20;

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    private IdentificationRule(IdentifierGroup root, JsonObject json)
    {
        Root = root;
        Json = json;
        Hash = HashOf(json);
        PositiveCount = CountPositive(root);
        Identifiers = [.. Walk(root, string.Empty, positive: true)];
    }

    public IdentifierGroup Root { get; }

    /// <summary>The rule as JSON (a copy is handed out by <see cref="ToJson"/>).</summary>
    private JsonObject Json { get; }

    /// <summary>The first 8 hex characters of the SHA-256 of the rule's compact JSON (<c>IdentificationHash</c>).</summary>
    public string Hash { get; }

    /// <summary>The positive identifiers (outside <c>none</c>), counted through nested groups: the flag catalogue's specificity (docs/11 §8.4 step 4).</summary>
    public int PositiveCount { get; }

    /// <summary>Every identifier with its path (<c>all[0]</c>, <c>any[1].all[0]</c>) and whether it is positive.</summary>
    public IReadOnlyList<(string Path, Identifier Identifier, bool Positive)> Identifiers { get; }

    public string ToJson() => Json.ToJsonString(Compact);

    public JsonObject ToJsonObject() => (JsonObject)Json.DeepClone();

    /// <summary>Reads a rule from its JSON. Throws <see cref="IdentificationRuleException"/> naming the place that does not read.</summary>
    public static IdentificationRule Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new IdentificationRuleException(string.Empty, $"The rule is not JSON: {ex.Message}");
        }

        return Parse(node);
    }

    /// <summary>Reads a rule from a JSON object.</summary>
    public static IdentificationRule Parse(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            throw new IdentificationRuleException(string.Empty, "An identification rule is a JSON object with all, any or none.");
        }

        var copy = (JsonObject)obj.DeepClone();
        var root = ReadGroup(copy, string.Empty, 1) ?? throw new IdentificationRuleException(string.Empty, "An identification rule needs all, any or none.");
        return new IdentificationRule(root, copy);
    }

    /// <summary>Tries <see cref="Parse(string)"/>; null with the error otherwise.</summary>
    public static IdentificationRule? TryParse(string? json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "No rule.";
            return null;
        }

        try
        {
            return Parse(json);
        }
        catch (IdentificationRuleException ex)
        {
            error = ex.Path.Length > 0 ? $"{ex.Path}: {ex.Message}" : ex.Message;
            return null;
        }
    }

    private static string HashOf(JsonObject json)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json.ToJsonString(Compact)));
        return Convert.ToHexStringLower(bytes)[..8];
    }

    private static int CountPositive(IdentifierGroup group) =>
        (group.All ?? []).Concat(group.Any ?? []).Sum(n => n switch
        {
            Identifier => 1,
            IdentifierGroup g => CountPositive(g),
            _ => 0,
        });

    private static IEnumerable<(string, Identifier, bool)> Walk(IdentifierGroup group, string prefix, bool positive)
    {
        foreach (var (name, members, isPositive) in new[] { ("all", group.All, positive), ("any", group.Any, positive), ("none", group.None, false) })
        {
            if (members is null)
            {
                continue;
            }

            for (var i = 0; i < members.Count; i++)
            {
                var path = $"{prefix}{name}[{i.ToString(CultureInfo.InvariantCulture)}]";
                switch (members[i])
                {
                    case Identifier identifier:
                        yield return (path, identifier, isPositive);
                        break;
                    case IdentifierGroup nested:
                        foreach (var inner in Walk(nested, path + ".", isPositive))
                        {
                            yield return inner;
                        }

                        break;
                }
            }
        }
    }

    private static readonly string[] GroupKeys = ["all", "any", "none"];
    private static readonly string[] LocationKeys = ["page", "region", "bBox", "tolerance"];
    private static readonly string[] IdentifierKeys = ["text", "regex", "supplierValue", "source", "xmlPath", "cell"];

    private static IdentifierGroup? ReadGroup(JsonObject obj, string path, int depth)
    {
        if (!GroupKeys.Any(obj.ContainsKey))
        {
            return null;
        }

        if (depth > MaxDepth)
        {
            throw new IdentificationRuleException(path, $"Groups nest at most {MaxDepth} deep.");
        }

        foreach (var (key, _) in obj)
        {
            if (!GroupKeys.Contains(key, StringComparer.Ordinal) && !LocationKeys.Contains(key, StringComparer.Ordinal))
            {
                throw new IdentificationRuleException(Join(path, key), $"'{key}' is not a key of a group (all, any, none, page, region, bBox, tolerance).");
            }
        }

        var location = ReadLocation(obj, path);
        return new IdentifierGroup(
            ReadMembers(obj, "all", path, depth),
            ReadMembers(obj, "any", path, depth),
            ReadMembers(obj, "none", path, depth),
            location);
    }

    private static List<IdentificationNode>? ReadMembers(JsonObject obj, string key, string path, int depth)
    {
        if (!obj.TryGetPropertyValue(key, out var node))
        {
            return null;
        }

        var here = Join(path, key);
        if (node is not JsonArray array || array.Count == 0)
        {
            throw new IdentificationRuleException(here, $"'{key}' is a non-empty array of identifiers or groups.");
        }

        var members = new List<IdentificationNode>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            var at = $"{here}[{i.ToString(CultureInfo.InvariantCulture)}]";
            if (array[i] is not JsonObject member)
            {
                throw new IdentificationRuleException(at, "An entry is an identifier object or a group.");
            }

            members.Add(ReadGroup(member, at, depth + 1) ?? (IdentificationNode)ReadIdentifier(member, at));
        }

        return members;
    }

    private static Identifier ReadIdentifier(JsonObject obj, string path)
    {
        // A source identifier's pattern is its "regex" parameter, not a regex identifier of its own.
        List<string> kinds = obj.ContainsKey("source")
            ? ["source", .. IdentifierKeys.Where(k => k is not ("source" or "regex") && obj.ContainsKey(k))]
            : [.. IdentifierKeys.Where(obj.ContainsKey)];
        if (kinds.Count != 1)
        {
            throw new IdentificationRuleException(path, kinds.Count == 0
                ? "An identifier names exactly one of text, regex, supplierValue, source, xmlPath or cell."
                : $"An identifier names exactly one of text, regex, supplierValue, source, xmlPath or cell, not {string.Join(" and ", kinds)}.");
        }

        var kindKey = kinds[0];
        var kind = kindKey switch
        {
            "text" => IdentifierKind.Text,
            "regex" => IdentifierKind.Regex,
            "supplierValue" => IdentifierKind.SupplierValue,
            "source" => IdentifierKind.Source,
            "xmlPath" => IdentifierKind.XmlPath,
            _ => IdentifierKind.Cell,
        };

        string[] allowed = kind switch
        {
            IdentifierKind.Text => ["text", "caseSensitive", "allowOcrErrors", .. LocationKeys],
            IdentifierKind.Regex => ["regex", .. LocationKeys],
            IdentifierKind.SupplierValue => ["supplierValue", .. LocationKeys],
            IdentifierKind.Source => ["source", "regex", "equals"],
            _ => [kindKey, "equals"],
        };
        foreach (var (key, _) in obj)
        {
            if (!allowed.Contains(key, StringComparer.Ordinal))
            {
                throw new IdentificationRuleException(Join(path, key), $"'{key}' is not a key of a {kindKey} identifier.");
            }
        }

        var value = String(obj, kindKey, path) ?? throw new IdentificationRuleException(Join(path, kindKey), $"'{kindKey}' is a non-empty string.");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new IdentificationRuleException(Join(path, kindKey), $"'{kindKey}' is a non-empty string.");
        }

        string? pattern = null;
        string? equals = String(obj, "equals", path);
        if (kind == IdentifierKind.Source)
        {
            pattern = String(obj, "regex", path);
            if ((pattern is null) == (equals is null))
            {
                throw new IdentificationRuleException(path, "A source identifier has exactly one of regex and equals.");
            }
        }

        return new Identifier(
            kind,
            kind == IdentifierKind.Text ? Values.PrintedValues.Collapse(value) : value,
            ReadLocation(obj, path),
            Bool(obj, "caseSensitive", path),
            Bool(obj, "allowOcrErrors", path),
            pattern,
            equals);
    }

    private static IdentifierLocation ReadLocation(JsonObject obj, string path)
    {
        PageSelector? page = null;
        if (obj.TryGetPropertyValue("page", out var pageNode))
        {
            page = pageNode switch
            {
                JsonValue v when v.TryGetValue<int>(out var n) && n >= 1 => new PageSelector(n, false),
                JsonValue v when v.TryGetValue<string>(out var s) && s == "last" => new PageSelector(null, true),
                JsonValue v when v.TryGetValue<string>(out var s) && s == "any" => PageSelector.Any,
                _ => throw new IdentificationRuleException(Join(path, "page"), "'page' is a page number (1 or more), \"last\" or \"any\"."),
            };
        }

        var region = String(obj, "region", path);
        if (region is not null && !IdentificationRegions.All.Contains(region, StringComparer.Ordinal))
        {
            throw new IdentificationRuleException(Join(path, "region"), $"'region' is one of {string.Join(", ", IdentificationRegions.All)}.");
        }

        BoundingBox? box = null;
        if (obj.TryGetPropertyValue("bBox", out var boxNode))
        {
            if (boxNode is not JsonArray { Count: 4 } b || b.Any(x => x is not JsonValue xv || !xv.TryGetValue<double>(out _)))
            {
                throw new IdentificationRuleException(Join(path, "bBox"), "'bBox' is [x0, y0, x1, y1] in page units (0 to 1000).");
            }

            var values = b.Select(x => x!.GetValue<double>()).ToArray();
            if (values.Any(v => v is < 0 or > 1000) || values[0] >= values[2] || values[1] >= values[3])
            {
                throw new IdentificationRuleException(Join(path, "bBox"), "'bBox' lies within the page (0 to 1000) with x0 < x1 and y0 < y1.");
            }

            box = new BoundingBox(values[0], values[1], values[2], values[3]);
        }

        if (region is not null && box is not null)
        {
            throw new IdentificationRuleException(path, "Give a region or a bBox, not both.");
        }

        double? tolerance = null;
        if (obj.TryGetPropertyValue("tolerance", out var toleranceNode))
        {
            if (toleranceNode is not JsonValue tv || !tv.TryGetValue<double>(out var t) || t is < 0 or > 200)
            {
                throw new IdentificationRuleException(Join(path, "tolerance"), "'tolerance' is a number of page units from 0 to 200.");
            }

            if (box is null)
            {
                throw new IdentificationRuleException(Join(path, "tolerance"), "'tolerance' applies to a bBox.");
            }

            tolerance = t;
        }

        return new IdentifierLocation(page, region, box, tolerance);
    }

    private static string? String(JsonObject obj, string key, string path)
    {
        if (!obj.TryGetPropertyValue(key, out var node))
        {
            return null;
        }

        return node is JsonValue v && v.TryGetValue<string>(out var s)
            ? s
            : throw new IdentificationRuleException(Join(path, key), $"'{key}' is a string.");
    }

    private static bool Bool(JsonObject obj, string key, string path)
    {
        if (!obj.TryGetPropertyValue(key, out var node))
        {
            return false;
        }

        return node is JsonValue v && v.TryGetValue<bool>(out var b)
            ? b
            : throw new IdentificationRuleException(Join(path, key), $"'{key}' is true or false.");
    }

    private static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";
}
