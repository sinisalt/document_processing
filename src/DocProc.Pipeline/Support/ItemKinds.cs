using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Pipeline.Support;

/// <summary>
/// Stage 1 (docs/03): what an ingestion item is, from its file extension first (mail clients and browsers send
/// <c>application/octet-stream</c> for anything unusual) and its declared content type second.
/// </summary>
internal static class ItemKinds
{
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
        [".htm"] = ItemKind.Text,
        [".html"] = ItemKind.Text,
        [".md"] = ItemKind.Text,
        [".zip"] = ItemKind.Archive,
    };

    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".webp"] = "image/webp",
        [".xml"] = "application/xml",
        [".zip"] = "application/zip",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ics"] = "text/calendar",
    };

    public static ItemKind Detect(string? mimeType, string? fileName)
    {
        if (Path.GetExtension(fileName ?? string.Empty) is { Length: > 1 } extension && ByExtension.TryGetValue(extension, out var byExtension))
        {
            return byExtension;
        }

        var mime = (mimeType ?? string.Empty).Split(';')[0].Trim();
        if (mime.Length == 0)
        {
            return ItemKind.Other;
        }

        if (mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            return ItemKind.Pdf;
        }

        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return ItemKind.Image;
        }

        if (mime.Equals("application/xml", StringComparison.OrdinalIgnoreCase) || mime.Equals("text/xml", StringComparison.OrdinalIgnoreCase) || mime.EndsWith("+xml", StringComparison.OrdinalIgnoreCase))
        {
            return ItemKind.Xml;
        }

        if (mime.Equals("application/zip", StringComparison.OrdinalIgnoreCase) || mime.Equals("application/x-zip-compressed", StringComparison.OrdinalIgnoreCase))
        {
            return ItemKind.Archive;
        }

        if (mime.StartsWith("application/vnd.openxmlformats", StringComparison.OrdinalIgnoreCase)
            || mime.StartsWith("application/vnd.ms-", StringComparison.OrdinalIgnoreCase)
            || mime.StartsWith("application/vnd.oasis.opendocument", StringComparison.OrdinalIgnoreCase)
            || mime.Equals("application/msword", StringComparison.OrdinalIgnoreCase)
            || mime.Equals("application/rtf", StringComparison.OrdinalIgnoreCase))
        {
            return ItemKind.Office;
        }

        if (mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return ItemKind.Text;
        }

        return ItemKind.Other;
    }

    /// <summary>Content type for a file unpacked from an archive, which carries no declared type of its own.</summary>
    public static string MimeTypeFor(string fileName) =>
        Path.GetExtension(fileName) is { Length: > 1 } extension && MimeByExtension.TryGetValue(extension, out var mime)
            ? mime
            : "application/octet-stream";

    public static bool IsCalendar(string? mimeType, string? fileName) =>
        string.Equals(Path.GetExtension(fileName ?? string.Empty), ".ics", StringComparison.OrdinalIgnoreCase)
        || (mimeType ?? string.Empty).StartsWith("text/calendar", StringComparison.OrdinalIgnoreCase);

    public static bool IsPng(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
}
