using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;

namespace DocProc.Extraction.EInvoice.Internal;

/// <summary>
/// The single place XML is read from untrusted bytes: DTDs are prohibited and no resolver exists, so entity
/// expansion and XXE are impossible regardless of what the document declares; the payload size and the element
/// nesting depth are capped by <see cref="EInvoiceParserOptions"/>, so a crafted document cannot pin a worker.
/// </summary>
internal static class SecureXml
{
    /// <summary>Wraps the bytes without copying when the memory is array-backed.</summary>
    public static MemoryStream OpenStream(ReadOnlyMemory<byte> bytes) =>
        MemoryMarshal.TryGetArray(bytes, out var segment) && segment.Array is not null
            ? new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(bytes.ToArray(), writable: false);

    /// <summary>Rejects a payload above the byte limit before any of it is parsed.</summary>
    public static void RequireSize(ReadOnlyMemory<byte> bytes, EInvoiceParserOptions options)
    {
        if (bytes.Length > options.MaxDocumentBytes)
        {
            throw new EInvoiceParseException(
                $"The e-invoice XML is {bytes.Length} bytes, above the limit of {options.MaxDocumentBytes} bytes.");
        }
    }

    /// <summary>A secure forward-only reader that throws <see cref="EInvoiceParseException"/> past the depth limit.</summary>
    public static XmlReader CreateReader(Stream stream, EInvoiceParserOptions options) =>
        new DepthLimitedXmlReader(XmlReader.Create(stream, Settings(options)), options.MaxElementDepth);

    /// <summary>
    /// Loads the whole document. Malformed XML, a DTD, or a document over the size or depth limit becomes an
    /// <see cref="EInvoiceParseException"/>.
    /// </summary>
    public static XDocument Load(ReadOnlyMemory<byte> bytes, EInvoiceParserOptions options)
    {
        RequireSize(bytes, options);
        using var stream = OpenStream(bytes);
        using var reader = CreateReader(stream, options);
        try
        {
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new EInvoiceParseException($"The e-invoice XML is malformed: {ex.Message}", ex);
        }
    }

    private static XmlReaderSettings Settings(EInvoiceParserOptions options) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = false,
        MaxCharactersInDocument = options.MaxDocumentBytes,
    };
}
