using System.Xml;
using System.Xml.Linq;
using DocProc.Application.Abstractions;
using DocProc.Extraction.EInvoice.Internal;

namespace DocProc.Extraction.EInvoice;

/// <summary>
/// Stage 2 sniff (docs/03-pipeline.md): recognises UBL Invoice / CreditNote and CII CrossIndustryInvoice from the
/// root element and its namespace with a forward-only <see cref="XmlReader"/>, and reads the profile identifier
/// (UBL CustomizationID, CII GuidelineSpecifiedDocumentContextParameter/ID) from the document preamble without
/// parsing the rest. DTDs are prohibited, so a document with a DOCTYPE is not an e-invoice, whatever its root says,
/// and neither is one over the size or nesting limits of <see cref="EInvoiceParserOptions"/>.
/// </summary>
public static class EInvoiceDetector
{
    private const string UblCustomizationId = "CustomizationID";
    private const string UblExtensions = "UBLExtensions";
    private const string UblVersionId = "UBLVersionID";
    private const string CiiDocumentContext = "ExchangedDocumentContext";
    private const string CiiGuidelineParameter = "GuidelineSpecifiedDocumentContextParameter";
    private const string Id = "ID";

    public static EInvoiceDetection? Detect(ReadOnlyMemory<byte> xml) => Detect(xml, EInvoiceParserOptions.Default);

    /// <summary>
    /// Detection under explicit resource limits: a payload over the byte limit, or one nested deeper than the depth
    /// limit before the profile is found, is not an e-invoice this parser accepts and yields null.
    /// </summary>
    public static EInvoiceDetection? Detect(ReadOnlyMemory<byte> xml, EInvoiceParserOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (xml.IsEmpty || xml.Length > options.MaxDocumentBytes)
        {
            return null;
        }

        using var stream = SecureXml.OpenStream(xml);
        using var reader = SecureXml.CreateReader(stream, options);

        string rootElement;
        EInvoiceFlavour flavour;
        try
        {
            if (reader.MoveToContent() != XmlNodeType.Element)
            {
                return null;
            }

            rootElement = reader.LocalName;
            if (Classify(rootElement, reader.NamespaceURI) is not { } recognised)
            {
                return null;
            }

            flavour = recognised;
        }
        catch (XmlException)
        {
            // Not XML, a DOCTYPE, or a broken prolog: not an e-invoice.
            return null;
        }

        string? profile;
        try
        {
            profile = flavour == EInvoiceFlavour.Ubl ? ReadUblProfile(reader) : ReadCiiProfile(reader);
        }
        catch (XmlException)
        {
            // The root is a recognised e-invoice but the body is broken; Parse reports the exact problem.
            profile = null;
        }
        catch (EInvoiceParseException)
        {
            // Nested past the depth limit before the profile was found: rejected outright, not handed to Parse.
            return null;
        }

        return new EInvoiceDetection(flavour, profile, rootElement);
    }

    private static EInvoiceFlavour? Classify(string localName, string namespaceUri) => (localName, namespaceUri) switch
    {
        (EInvoiceNamespaces.UblInvoiceRoot, EInvoiceNamespaces.UblInvoice) => EInvoiceFlavour.Ubl,
        (EInvoiceNamespaces.UblCreditNoteRoot, EInvoiceNamespaces.UblCreditNote) => EInvoiceFlavour.Ubl,
        (EInvoiceNamespaces.CiiRoot, EInvoiceNamespaces.CiiInvoice) => EInvoiceFlavour.Cii,
        _ => null,
    };

    /// <summary>UBL schema order puts UBLExtensions, UBLVersionID and CustomizationID first; the scan stops at the first other child.</summary>
    private static string? ReadUblProfile(XmlReader reader)
    {
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case UblCustomizationId:
                    return Normalise(reader.ReadElementContentAsString());
                case UblExtensions:
                case UblVersionId:
                    continue;
                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>CII puts the ExchangedDocumentContext first; only that small subtree is materialised.</summary>
    private static string? ReadCiiProfile(XmlReader reader)
    {
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1)
            {
                continue;
            }

            if (reader.LocalName != CiiDocumentContext)
            {
                return null;
            }

            using var subtree = reader.ReadSubtree();
            var context = XElement.Load(subtree);
            return Normalise(context.Path(CiiGuidelineParameter, Id).Text());
        }

        return null;
    }

    private static string? Normalise(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
