using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Api.Documents;

/// <summary>Maps an upload's content type (and, when that is generic, its extension) to the coarse <see cref="ItemKind"/> of docs/02 §2.</summary>
public static class ItemKinds
{
    public const string DefaultContentType = "application/octet-stream";

    private static readonly Dictionary<string, ItemKind> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = ItemKind.Pdf,
        [".png"] = ItemKind.Image,
        [".jpg"] = ItemKind.Image,
        [".jpeg"] = ItemKind.Image,
        [".tif"] = ItemKind.Image,
        [".tiff"] = ItemKind.Image,
        [".gif"] = ItemKind.Image,
        [".bmp"] = ItemKind.Image,
        [".webp"] = ItemKind.Image,
        [".heic"] = ItemKind.Image,
        [".xml"] = ItemKind.Xml,
        [".docx"] = ItemKind.Office,
        [".doc"] = ItemKind.Office,
        [".xlsx"] = ItemKind.Office,
        [".xls"] = ItemKind.Office,
        [".pptx"] = ItemKind.Office,
        [".odt"] = ItemKind.Office,
        [".ods"] = ItemKind.Office,
        [".rtf"] = ItemKind.Office,
        [".txt"] = ItemKind.Text,
        [".csv"] = ItemKind.Text,
        [".html"] = ItemKind.Text,
        [".htm"] = ItemKind.Text,
        [".eml"] = ItemKind.Text,
        [".zip"] = ItemKind.Archive,
        [".7z"] = ItemKind.Archive,
        [".rar"] = ItemKind.Archive,
        [".gz"] = ItemKind.Archive,
        [".tar"] = ItemKind.Archive,
    };

    public static ItemKind FromContentType(string? contentType, string? fileName)
    {
        var type = Normalize(contentType);
        var kind = type switch
        {
            "application/pdf" or "application/x-pdf" => ItemKind.Pdf,
            _ when type.StartsWith("image/", StringComparison.Ordinal) => ItemKind.Image,
            "application/xml" or "text/xml" => ItemKind.Xml,
            _ when type.EndsWith("+xml", StringComparison.Ordinal) => ItemKind.Xml,
            "application/msword" or "application/vnd.ms-excel" or "application/vnd.ms-powerpoint" or "application/rtf" => ItemKind.Office,
            _ when type.StartsWith("application/vnd.openxmlformats-officedocument.", StringComparison.Ordinal) => ItemKind.Office,
            _ when type.StartsWith("application/vnd.oasis.opendocument.", StringComparison.Ordinal) => ItemKind.Office,
            "message/rfc822" => ItemKind.Text,
            _ when type.StartsWith("text/", StringComparison.Ordinal) => ItemKind.Text,
            "application/zip" or "application/x-zip-compressed" or "application/x-7z-compressed" or "application/vnd.rar" or "application/x-rar-compressed" or "application/gzip" or "application/x-tar" => ItemKind.Archive,
            _ => ItemKind.Other,
        };

        if (kind == ItemKind.Other && !string.IsNullOrEmpty(fileName) && ByExtension.TryGetValue(Path.GetExtension(fileName), out var byExtension))
        {
            return byExtension;
        }

        return kind;
    }

    /// <summary>
    /// The media type without parameters, lower-cased; <see cref="DefaultContentType"/> when absent or when it is not
    /// a single <c>type/subtype</c> pair of RFC 9110 tokens. An upload's claimed type is stored and later compared
    /// in this form, so a list (<c>image/png,text/html</c>, which a browser reads as its last entry), whitespace or
    /// quotes inside it never survive as something that merely starts like an image type.
    /// </summary>
    public static string Normalize(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return DefaultContentType;
        }

        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        var type = (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim().ToLowerInvariant();
        return IsMediaType(type) ? type : DefaultContentType;
    }

    private static bool IsMediaType(string type)
    {
        var slash = type.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && IsToken(type.AsSpan(0, slash)) && IsToken(type.AsSpan(slash + 1));
    }

    /// <summary>RFC 9110 §5.6.2 <c>token</c>: visible ASCII except the delimiters, so no comma, slash, space or quote.</summary>
    private static bool IsToken(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && !TokenSymbols.Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private const string TokenSymbols = "!#$%&'*+-.^_`|~";
}
