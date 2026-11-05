using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DocProc.Infrastructure.Outputs.Delivery;

/// <summary>What a delivery's templates (an http URL, a file name) can name.</summary>
/// <param name="DocumentId"><c>{{meta.documentId}}</c>.</param>
/// <param name="Version"><c>{{meta.version}}</c>, the document version the payload was rendered from (the revision).</param>
/// <param name="Event"><c>{{meta.event}}</c>.</param>
/// <param name="TypeKey"><c>{{meta.typeKey}}</c>.</param>
/// <param name="DeliveryId"><c>{{meta.deliveryId}}</c>.</param>
/// <param name="Output">The rendered payload, which <c>{{output.path}}</c> reads; null when it is not JSON.</param>
public sealed record DeliveryTemplateValues(Guid DocumentId, int Version, string Event, string TypeKey, Guid DeliveryId, JsonNode? Output);

/// <summary>
/// The <c>{{...}}</c> placeholders of a destination's <c>urlTemplate</c>, <c>updateUrlTemplate</c> and
/// <c>fileNameTemplate</c> and a mapping version's <c>FileNameTemplate</c> (docs/11 §13.3). They read the snapshot of the
/// delivery, not the live document, so a retry or a redelivery addresses the same place: <c>meta.documentId</c>,
/// <c>meta.version</c>, <c>meta.event</c>, <c>meta.typeKey</c>, <c>meta.deliveryId</c>, and <c>output.a.b[0].c</c>, a
/// value of the rendered payload (the <c>priceListId</c> of an article-price update, docs/11 §13.10). A missing value
/// is a permanent error of the delivery, not an empty segment.
/// </summary>
public static partial class DeliveryTemplates
{
    /// <summary>The placeholders, for error messages.</summary>
    public const string Describe = "{{meta.documentId}}, {{meta.version}}, {{meta.event}}, {{meta.typeKey}}, {{meta.deliveryId}} and {{output.<path>}} (a value of the rendered payload)";

    private static readonly HashSet<string> Meta = new(StringComparer.Ordinal) { "meta.documentId", "meta.version", "meta.event", "meta.typeKey", "meta.deliveryId" };

    /// <summary>Whether <paramref name="placeholder"/> (with its braces) is one this build fills.</summary>
    public static bool IsKnownPlaceholder(string placeholder)
    {
        ArgumentNullException.ThrowIfNull(placeholder);
        var name = Name(placeholder);
        return Meta.Contains(name) || (name.StartsWith("output.", StringComparison.Ordinal) && OutputPath().IsMatch(name[7..]));
    }

    /// <summary>
    /// The URL <paramref name="template"/> names, each value percent-encoded as one path or query segment. A path
    /// segment that is <c>.</c> or <c>..</c> and holds a value (<c>/{{output.x}}/</c> with <c>x = ".."</c>, or
    /// <c>/{{output.x}}{{output.y}}/</c> with both <c>"."</c>) is refused (a <see cref="DeliveryTemplateException"/>):
    /// escaping leaves dots as they are, so it is a dot segment, which the URL parser removes, and the request would go
    /// to another path. The same value in the query, or beside other text of its segment (<c>.z</c>), is sent as it is.
    /// </summary>
    public static string ExpandUrl(string template, DeliveryTemplateValues values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        var builder = new StringBuilder(template.Length);
        var filled = new List<(int Start, int End)>();
        var at = 0;
        foreach (Match match in Placeholder().Matches(template))
        {
            builder.Append(template, at, match.Index - at);
            var start = builder.Length;
            builder.Append(Uri.EscapeDataString(Value(match.Value, values)));
            filled.Add((start, builder.Length));
            at = match.Index + match.Length;
        }

        builder.Append(template, at, template.Length - at);
        var url = builder.ToString();

        // Escaping turns a value's '/', '?' and '#' into %2F, %3F and %23, so the path ends at the template's own
        // first '?' or '#', and its segments are the text between the '/'s before it.
        var end = url.IndexOfAny(['?', '#']);
        end = end < 0 ? url.Length : end;
        var segment = 0;
        for (var i = 0; i <= end; i++)
        {
            if (i < end && url[i] != '/')
            {
                continue;
            }

            if (url.AsSpan(segment, i - segment) is "." or ".." && filled.Exists(f => f.Start < i && f.End > segment))
            {
                throw new DeliveryTemplateException($"A URL template value makes the path segment '{url[segment..i]}', a dot segment that would move the request to another path; it is not sent.");
            }

            segment = i + 1;
        }

        return url;
    }

    /// <summary>
    /// The file name <paramref name="template"/> names: values with characters a file name cannot hold
    /// (separators, control characters, <c>..</c>) replaced by <c>_</c>.
    /// </summary>
    public static string ExpandFileName(string template, DeliveryTemplateValues values)
    {
        var name = Expand(template, values, SafeFileName);
        name = SafeFileName(name);
        return name.Length > 200 ? name[..200] : name;
    }

    private static string Expand(string template, DeliveryTemplateValues values, Func<string, string> encode)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        return Placeholder().Replace(template, match => encode(Value(match.Value, values)));
    }

    /// <summary>The value <paramref name="placeholder"/> names for this delivery; a missing or empty one is a <see cref="DeliveryTemplateException"/>.</summary>
    private static string Value(string placeholder, DeliveryTemplateValues values)
    {
        var name = Name(placeholder);
        var value = name switch
        {
            "meta.documentId" => values.DocumentId.ToString("D"),
            "meta.version" => values.Version.ToString(CultureInfo.InvariantCulture),
            "meta.event" => values.Event,
            "meta.typeKey" => values.TypeKey,
            "meta.deliveryId" => values.DeliveryId.ToString("D"),
            _ when name.StartsWith("output.", StringComparison.Ordinal) => Read(values.Output, name[7..]),
            _ => null,
        };
        return value is { Length: > 0 } text
            ? text
            : throw new DeliveryTemplateException($"The template's {placeholder} has no value for this delivery.");
    }

    private static string? Read(JsonNode? node, string path)
    {
        foreach (Match segment in Segment().Matches(path))
        {
            if (node is null)
            {
                return null;
            }

            node = segment.Groups["index"].Success
                ? node is JsonArray array && int.Parse(segment.Groups["index"].Value, CultureInfo.InvariantCulture) is var i && i < array.Count ? array[i] : null
                : node is JsonObject obj && obj.TryGetPropertyValue(segment.Groups["name"].Value, out var child) ? child : null;
        }

        return node switch
        {
            JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
            JsonValue value when value.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToJsonString(),
            _ => null,
        };
    }

    private static string SafeFileName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
        }

        var text = builder.ToString().Replace("..", "__", StringComparison.Ordinal).Trim();
        return text.Length == 0 || text.StartsWith('.') ? "_" + text : text;
    }

    private static string Name(string placeholder) => placeholder.Trim('{', '}').Trim();

    [GeneratedRegex(@"\{\{[^{}]*\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_$-]*(\[[0-9]{1,6}\])*(\.[A-Za-z_$][A-Za-z0-9_$-]*(\[[0-9]{1,6}\])*)*$")]
    private static partial Regex OutputPath();

    [GeneratedRegex(@"(?<name>[A-Za-z_$][A-Za-z0-9_$-]*)|\[(?<index>[0-9]{1,6})\]")]
    private static partial Regex Segment();
}

/// <summary>A template names a value the delivery does not have: a permanent failure of that delivery.</summary>
public sealed class DeliveryTemplateException(string message) : Exception(message);
