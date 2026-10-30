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

    public static EmbeddedXml? Find(PdfDocument document)
    {
        if (!document.Advanced.TryGetEmbeddedFiles(out var files) || files.Count == 0)
        {
            return null;
        }

        foreach (var (fileName, flavour) in KnownNames)
        {
            var known = files.FirstOrDefault(f => string.Equals(FileNameOf(f), fileName, StringComparison.OrdinalIgnoreCase));
            if (known is not null && TryReadRootElement(known, out _))
            {
                return new EmbeddedXml(FileNameOf(known), known.Memory.ToArray(), flavour);
            }
        }

        foreach (var file in files)
        {
            var name = FileNameOf(file);
            if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || !TryReadRootElement(file, out var root))
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
                return new EmbeddedXml(name, file.Memory.ToArray(), flavour);
            }
        }

        return null;
    }

    private static string FileNameOf(EmbeddedFile file) => Path.GetFileName(file.Name ?? string.Empty);

    /// <summary>Reads up to the root element without resolving anything; invalid XML is simply not an e-invoice.</summary>
    private static bool TryReadRootElement(EmbeddedFile file, out string rootElement)
    {
        rootElement = string.Empty;
        try
        {
            using var stream = new MemoryStream(file.Memory.ToArray(), writable: false);
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
}
