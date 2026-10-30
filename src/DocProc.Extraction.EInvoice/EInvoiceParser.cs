using System.Xml.Linq;
using DocProc.Application.Abstractions;
using DocProc.Domain.Invoices;
using DocProc.Extraction.EInvoice.Cii;
using DocProc.Extraction.EInvoice.Internal;
using DocProc.Extraction.EInvoice.Ubl;

namespace DocProc.Extraction.EInvoice;

/// <summary>
/// Stage 6a (docs/03-pipeline.md): the <see cref="IEInvoiceParser"/> for the pipeline. Detection is delegated to
/// <see cref="EInvoiceDetector"/>; parsing dispatches on the detected flavour to <see cref="UblInvoiceParser"/> or
/// <see cref="CiiInvoiceParser"/>. Pure and stateless: XML in, <see cref="Invoice"/> out, no I/O. The size and
/// nesting limits of <see cref="EInvoiceParserOptions"/> are enforced on both paths.
/// </summary>
public sealed class EInvoiceParser : IEInvoiceParser
{
    private readonly EInvoiceParserOptions _options;

    public EInvoiceParser()
        : this(EInvoiceParserOptions.Default)
    {
    }

    public EInvoiceParser(EInvoiceParserOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public EInvoiceDetection? Detect(ReadOnlyMemory<byte> xml) => EInvoiceDetector.Detect(xml, _options);

    /// <summary>
    /// Malformed XML, a DTD, a mismatched root or a document over the size or depth limit throw
    /// <see cref="EInvoiceParseException"/>; invalid values inside a well-formed document never throw.
    /// </summary>
    public Invoice Parse(ReadOnlyMemory<byte> xml, EInvoiceDetection detection)
    {
        ArgumentNullException.ThrowIfNull(detection);

        var root = SecureXml.Load(xml, _options).Root ?? throw new EInvoiceParseException("The e-invoice XML has no root element.");
        return detection.Flavour switch
        {
            EInvoiceFlavour.Ubl => UblInvoiceParser.Parse(RequireRoot(root, detection, IsUblRoot)),
            EInvoiceFlavour.Cii => CiiInvoiceParser.Parse(RequireRoot(root, detection, IsCiiRoot)),
            _ => throw new EInvoiceParseException($"Unsupported e-invoice flavour '{detection.Flavour}'."),
        };
    }

    private static bool IsUblRoot(XElement root) =>
        (root.Name.LocalName, root.Name.NamespaceName) is
            (EInvoiceNamespaces.UblInvoiceRoot, EInvoiceNamespaces.UblInvoice) or
            (EInvoiceNamespaces.UblCreditNoteRoot, EInvoiceNamespaces.UblCreditNote);

    private static bool IsCiiRoot(XElement root) =>
        (root.Name.LocalName, root.Name.NamespaceName) is (EInvoiceNamespaces.CiiRoot, EInvoiceNamespaces.CiiInvoice);

    private static XElement RequireRoot(XElement root, EInvoiceDetection detection, Func<XElement, bool> matches) =>
        matches(root)
            ? root
            : throw new EInvoiceParseException(
                $"The XML was detected as {detection.Flavour} but its root element is '{root.Name.LocalName}' in namespace '{root.Name.NamespaceName}'.");
}
