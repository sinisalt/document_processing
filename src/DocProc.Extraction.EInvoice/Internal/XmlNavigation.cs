using System.Xml.Linq;

namespace DocProc.Extraction.EInvoice.Internal;

/// <summary>
/// Local-name navigation over the XML tree. UBL and CII documents in the wild use varying prefixes and
/// occasionally a stale namespace version on inner elements, so mapping matches by local name; the root
/// element's namespace is checked once by the detector, where it matters.
/// </summary>
internal static class XmlNavigation
{
    public static XElement? Child(this XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    public static IEnumerable<XElement> Children(this XElement? parent, string localName) =>
        parent is null ? [] : parent.Elements().Where(e => e.Name.LocalName == localName);

    /// <summary>Walks a chain of child elements by local name; null as soon as one step is absent.</summary>
    public static XElement? Path(this XElement? parent, params ReadOnlySpan<string> localNames)
    {
        var current = parent;
        foreach (var name in localNames)
        {
            current = current.Child(name);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>Trimmed text content, or null for an absent or whitespace-only element.</summary>
    public static string? Text(this XElement? element)
    {
        var value = element?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Trimmed attribute value, or null when absent or blank. Attributes are matched without namespace.</summary>
    public static string? Attr(this XElement? element, string localName)
    {
        var value = element?.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Joined text of several elements (document notes), or null when none carries text.</summary>
    public static string? JoinedText(this IEnumerable<XElement?> elements)
    {
        var parts = elements.Select(e => e.Text()).Where(t => t is not null).ToArray();
        return parts.Length == 0 ? null : string.Join("\n", parts);
    }
}
