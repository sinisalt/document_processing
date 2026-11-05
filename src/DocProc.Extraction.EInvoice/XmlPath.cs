using System.Globalization;
using System.Xml.Linq;
using DocProc.Extraction.EInvoice.Internal;

namespace DocProc.Extraction.EInvoice;

/// <summary>One value an <see cref="XmlPath"/> selected: its text and the concrete element path it came from (what <c>FieldSource.Path</c> records).</summary>
public sealed record XmlPathHit(string Value, string Path);

/// <summary>
/// The restricted walker of the layouts' <c>xml-path</c> rules (docs/11 §8.1, §9.3), not an XPath engine: a path is a
/// chain of element steps separated by <c>/</c>, read from the root element, each a local name with an optional
/// namespace prefix (ignored: e-invoices in the wild use varying prefixes, so steps match by local name, like the
/// parsers), and either a 1-based position (<c>cac:InvoiceLine[2]</c>) or an equality filter on the text of a direct
/// child (<c>cac:AdditionalItemProperty[cbc:Name='Farbe']</c>), ending optionally in an attribute (<c>cbc:ID/@schemeID</c>). A leading <c>/</c> and the root's own name are
/// allowed and skipped. Every match is returned in document order (a step without a position walks every match).
/// Bounded: at most <see cref="MaxSteps"/> steps and <see cref="MaxHits"/> values.
/// </summary>
public sealed class XmlPath
{
    public const int MaxSteps = 32;
    public const int MaxHits = 1000;
    public const int MaxLength = 1000;

    private readonly IReadOnlyList<Step> _steps;
    private readonly string? _attribute;

    private XmlPath(string text, IReadOnlyList<Step> steps, string? attribute)
    {
        Text = text;
        _steps = steps;
        _attribute = attribute;
    }

    public string Text { get; }

    private sealed record Step(string LocalName, int? Position, string? FilterChild, string? FilterValue);

    /// <summary>The path of <paramref name="text"/>, or null with the reason it is not one this walker reads.</summary>
    public static XmlPath? TryParse(string? text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength)
        {
            error = $"An xml-path is a chain of element names separated by '/', at most {MaxLength} characters.";
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            error = "An xml-path starts at the root: '//' (search anywhere) is not supported.";
            return null;
        }

        var parts = (trimmed.StartsWith('/') ? trimmed[1..] : trimmed).Split('/');
        string? attribute = null;
        if (parts[^1].StartsWith('@'))
        {
            attribute = LocalName(parts[^1][1..]);
            parts = parts[..^1];
            if (attribute.Length == 0)
            {
                error = "The attribute step names an attribute (@schemeID).";
                return null;
            }
        }

        if (parts.Length is 0 or > MaxSteps)
        {
            error = $"An xml-path has 1 to {MaxSteps} element steps.";
            return null;
        }

        var steps = new List<Step>(parts.Length);
        foreach (var part in parts)
        {
            if (ParseStep(part) is not { } step)
            {
                error = $"'{part}' is not an element step (a name, an optional [n] position or [child='value'] filter).";
                return null;
            }

            steps.Add(step);
        }

        return new XmlPath(text.Trim(), steps, attribute);
    }

    /// <summary>The values the path selects below (and including) <paramref name="root"/>, with their concrete paths.</summary>
    public IReadOnlyList<XmlPathHit> Select(XElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var steps = _steps;

        // The root may be named as the first step (Invoice/cbc:ID); otherwise steps start below it.
        IEnumerable<(XElement Element, string Path)> current = [(root, root.Name.LocalName)];
        var start = steps.Count > 1 && steps[0].LocalName == root.Name.LocalName && steps[0].Position is null or 1 && steps[0].FilterChild is null ? 1 : 0;
        foreach (var step in steps.Skip(start))
        {
            current = current.SelectMany(c => Apply(step, c.Element, c.Path)).Take(MaxHits).ToList();
        }

        var hits = new List<XmlPathHit>();
        foreach (var (element, path) in current)
        {
            if (_attribute is null)
            {
                if (element.Text() is { } text)
                {
                    hits.Add(new XmlPathHit(text, path));
                }
            }
            else if (element.Attr(_attribute) is { } value)
            {
                hits.Add(new XmlPathHit(value, $"{path}/@{_attribute}"));
            }

            if (hits.Count >= MaxHits)
            {
                break;
            }
        }

        return hits;
    }

    /// <summary>The first value the path selects, or null.</summary>
    public XmlPathHit? First(XElement root) => Select(root) is { Count: > 0 } hits ? hits[0] : null;

    private static IEnumerable<(XElement, string)> Apply(Step step, XElement parent, string path)
    {
        var matches = parent.Children(step.LocalName);
        if (step.FilterChild is { } child)
        {
            matches = matches.Where(m => string.Equals(m.Child(child).Text(), step.FilterValue, StringComparison.Ordinal));
        }

        var indexed = matches.Select((m, i) => (Element: m, Index: i + 1));
        if (step.Position is { } position)
        {
            indexed = indexed.Where(m => m.Index == position);
        }

        return indexed.Select(m => (m.Element, $"{path}/{step.LocalName}[{m.Index.ToString(CultureInfo.InvariantCulture)}]"));
    }

    private static Step? ParseStep(string part)
    {
        var bracket = part.IndexOf('[', StringComparison.Ordinal);
        var name = LocalName(bracket < 0 ? part : part[..bracket]);
        if (!IsName(name))
        {
            return null;
        }

        if (bracket < 0)
        {
            return new Step(name, null, null, null);
        }

        if (!part.EndsWith(']'))
        {
            return null;
        }

        var inner = part[(bracket + 1)..^1].Trim();
        if (int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var position) && position >= 1)
        {
            return new Step(name, position, null, null);
        }

        var equals = inner.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
        {
            return null;
        }

        var child = LocalName(inner[..equals].Trim());
        var value = inner[(equals + 1)..].Trim();
        if (!IsName(child) || value.Length < 2 || value[0] != value[^1] || value[0] is not ('\'' or '"'))
        {
            return null;
        }

        return new Step(name, null, child, value[1..^1]);
    }

    private static string LocalName(string qualified)
    {
        var colon = qualified.IndexOf(':', StringComparison.Ordinal);
        return (colon < 0 ? qualified : qualified[(colon + 1)..]).Trim();
    }

    private static bool IsName(string name) =>
        name.Length is > 0 and <= 100 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.');
}
