using System.Runtime.InteropServices;
using System.Xml;
using DocProc.Application.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace DocProc.Infrastructure.Pdf;

/// <summary>
/// Finds the e-invoice XML attached to a PDF/A-3 (Factur-X, ZUGFeRD, XRechnung) in the catalog's
/// <c>Names → EmbeddedFiles</c> tree (docs/03 stage 2 "Detect embedded structured data first"). Well-known file
/// names win; otherwise any <c>.xml</c> attachment whose root element is a CII <c>CrossIndustryInvoice</c> or a UBL
/// <c>Invoice</c> / <c>CreditNote</c> qualifies.
/// </summary>
/// <remarks>
/// An attachment has two names: the key in the name tree (<see cref="EmbeddedFile.Name"/>), which producers may
/// fill with anything (a counter, a UUID), and the file specification (<see cref="EmbeddedFile.FileSpecification"/>,
/// the <c>/F</c> entry), which is the file name. Both are matched; the file specification is reported as the
/// attachment's file name when it has one. Attachments larger than the configured maximum are ignored, before and
/// after decoding, so a decompression bomb never reaches the XML reader.
/// </remarks>
internal static class EmbeddedInvoiceXmlFinder
{
    private const string CiiRoot = "CrossIndustryInvoice";
    private const string UblInvoiceRoot = "Invoice";
    private const string UblCreditNoteRoot = "CreditNote";

    /// <summary>Known attachment names in order of preference, with the flavour each one implies.</summary>
    private static readonly (string FileName, string Flavour)[] KnownNames =
    [
        ("factur-x.xml", EmbeddedInvoiceFlavours.FacturX),
        ("zugferd-invoice.xml", EmbeddedInvoiceFlavours.Zugferd),
        ("ZUGFeRD-invoice.xml", EmbeddedInvoiceFlavours.Zugferd),
        ("xrechnung.xml", EmbeddedInvoiceFlavours.XRechnung),
    ];

    private static readonly XmlReaderSettings SniffSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
    };

    public static EmbeddedXml? Find(PdfDocument document, int maximumAttachmentBytes)
    {
        if (!document.Advanced.TryGetEmbeddedFiles(out var files) || files.Count == 0)
        {
            return null;
        }

        var attachments = files.Select(Attachment.Of).ToList();

        foreach (var (fileName, flavour) in KnownNames)
        {
            var known = attachments.FirstOrDefault(a => a.HasName(fileName));
            if (known is not null && known.TryDecode(maximumAttachmentBytes, out var content) && TryReadRootElement(content, out _))
            {
                return new EmbeddedXml(known.FileName, content.ToArray(), flavour);
            }
        }

        foreach (var attachment in attachments)
        {
            if (!attachment.IsXml || !attachment.TryDecode(maximumAttachmentBytes, out var content) || !TryReadRootElement(content, out var root))
            {
                continue;
            }

            var flavour = root switch
            {
                CiiRoot => EmbeddedInvoiceFlavours.Cii,
                UblInvoiceRoot or UblCreditNoteRoot => EmbeddedInvoiceFlavours.Ubl,
                _ => null,
            };
            if (flavour is not null)
            {
                return new EmbeddedXml(attachment.FileName, content.ToArray(), flavour);
            }
        }

        return null;
    }

    /// <summary>Reads up to the root element without resolving anything; invalid XML is simply not an e-invoice.</summary>
    private static bool TryReadRootElement(ReadOnlyMemory<byte> content, out string rootElement)
    {
        rootElement = string.Empty;
        try
        {
            using var stream = MemoryMarshal.TryGetArray(content, out var segment)
                ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
                : new MemoryStream(content.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, SniffSettings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    rootElement = reader.LocalName;
                    return true;
                }
            }

            return false;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>The file name part of a name-tree key or a file specification, whichever path separator the producer used.</summary>
    private static string LeafName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var cut = path.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? path.Trim() : path[(cut + 1)..].Trim();
    }

    /// <summary>One embedded file with both of its names; decoded at most once and only when it is small enough.</summary>
    private sealed class Attachment(EmbeddedFile file, string key, string specification)
    {
        private ReadOnlyMemory<byte>? _content;

        public static Attachment Of(EmbeddedFile file) => new(file, LeafName(file.Name), LeafName(file.FileSpecification));

        /// <summary>The file specification when the producer gave one, else the name-tree key.</summary>
        public string FileName => specification.Length > 0 ? specification : key;

        public bool IsXml => EndsWithXml(key) || EndsWithXml(specification);

        public bool HasName(string fileName) =>
            string.Equals(key, fileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(specification, fileName, StringComparison.OrdinalIgnoreCase);

        public bool TryDecode(int maximumBytes, out ReadOnlyMemory<byte> content)
        {
            if (_content is null)
            {
                // The stored stream is usually Flate-compressed; its length bounds nothing about the decoded size,
                // so both are checked. PdfPig decodes on access, hence the single read.
                _content = file.Stream.Data.Length > maximumBytes ? ReadOnlyMemory<byte>.Empty : file.Memory;
            }

            content = _content.Value;
            return content.Length > 0 && content.Length <= maximumBytes;
        }

        private static bool EndsWithXml(string name) => name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
    }
}
