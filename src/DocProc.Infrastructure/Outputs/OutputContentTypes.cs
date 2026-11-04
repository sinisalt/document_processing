using System.Net.Http.Headers;

namespace DocProc.Infrastructure.Outputs;

/// <summary>
/// The content types a mapping version's payload may declare (docs/11 §13.1): JSON, XML, CSV, plain text and opaque
/// bytes, and the structured-syntax suffixes <c>application/*+json</c> and <c>application/*+xml</c>, with an optional
/// <c>charset</c> and no other parameter. The value goes out as the payload's <c>Content-Type</c> (an http delivery, a
/// multipart part, a feed entry) and is what <c>GET /deliveries/{id}/payload</c> answers with, so it is checked when a
/// draft is created or saved and again at publish; a value saved before that check is served as opaque bytes and fails
/// a multipart delivery for good.
/// </summary>
public static class OutputContentTypes
{
    public const string Default = "application/json";

    public const string Opaque = "application/octet-stream";

    /// <summary>The allowed types, for error messages and docs.</summary>
    public const string Describe = "application/json, application/xml, text/xml, text/csv, text/plain, application/octet-stream, or application/<name>+json or +xml, with an optional charset";

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { "application/json", "application/xml", "text/xml", "text/csv", "text/plain", Opaque };

    /// <summary>
    /// The payload types <c>GET /deliveries/{id}/payload</c> serves inline: ones a browser shows as text and never runs.
    /// XML is not among them (an XHTML or SVG namespace in it would run as a page of the API's origin).
    /// </summary>
    private static readonly HashSet<string> Inline = new(StringComparer.Ordinal) { "application/json", "text/csv", "text/plain" };

    /// <summary>The value as a header carries it (lower-case type, the charset kept), or null when it is not an allowed content type.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !MediaTypeHeaderValue.TryParse(value.Trim(), out var parsed) || parsed.MediaType is not { } media)
        {
            return null;
        }

        media = media.ToLowerInvariant();
        if (!Allowed.Contains(media) && !IsSuffixed(media))
        {
            return null;
        }

        if (parsed.Parameters.Any(p => !string.Equals(p.Name, "charset", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(p.Value)))
        {
            return null;
        }

        return parsed.CharSet is { Length: > 0 } charset ? $"{media}; charset={charset.Trim('"')}" : media;
    }

    public static bool IsAllowed(string? value) => Normalize(value) is not null;

    /// <summary>
    /// How a stored payload of <paramref name="value"/> is served: inline under its own (normalised) type when that is
    /// JSON, CSV or plain text; else as an attachment, under its type when it is allowed and as opaque bytes when not.
    /// </summary>
    public static (string ContentType, bool Inline) Serving(string? value)
    {
        if (Normalize(value) is not { } normalized)
        {
            return (Opaque, false);
        }

        var media = normalized.Split(';', 2)[0];
        return (normalized, Inline.Contains(media) || (media.StartsWith("application/", StringComparison.Ordinal) && media.EndsWith("+json", StringComparison.Ordinal)));
    }

    private static bool IsSuffixed(string media)
    {
        if (!media.StartsWith("application/", StringComparison.Ordinal))
        {
            return false;
        }

        var subtype = media["application/".Length..];
        var suffix = subtype.EndsWith("+json", StringComparison.Ordinal) ? 5 : subtype.EndsWith("+xml", StringComparison.Ordinal) ? 4 : 0;
        return suffix > 0 && subtype.Length > suffix;
    }
}
