using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;

namespace DocProc.Extraction.EInvoice.Internal;

/// <summary>
/// The single place XML is read from untrusted bytes: DTDs are prohibited and no resolver exists, so entity
/// expansion and XXE are impossible regardless of what the document declares.
/// </summary>
internal static class SecureXml
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = false,
    };

    /// <summary>Wraps the bytes without copying when the memory is array-backed.</summary>
    public static MemoryStream OpenStream(ReadOnlyMemory<byte> bytes) =>
        MemoryMarshal.TryGetArray(bytes, out var segment) && segment.Array is not null
            ? new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(bytes.ToArray(), writable: false);

    public static XmlReader CreateReader(Stream stream) => XmlReader.Create(stream, ReaderSettings);

    /// <summary>Loads the whole document. Malformed XML or a DTD becomes an <see cref="EInvoiceParseException"/>.</summary>
    public static XDocument Load(ReadOnlyMemory<byte> bytes)
    {
        using var stream = OpenStream(bytes);
        using var reader = CreateReader(stream);
        try
        {
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new EInvoiceParseException($"The e-invoice XML is malformed: {ex.Message}", ex);
        }
    }
}
