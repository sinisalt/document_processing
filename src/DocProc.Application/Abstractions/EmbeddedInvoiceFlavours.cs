namespace DocProc.Application.Abstractions;

/// <summary>
/// Values of <see cref="EmbeddedXml.Flavour"/>: the hint stage 2 (docs/03 "Unpack and normalize") attaches to an
/// embedded e-invoice before stage 6a sniffs the XML itself. The hint comes from the attachment's file name when it is
/// a well-known one, otherwise from the XML root element.
/// </summary>
public static class EmbeddedInvoiceFlavours
{
    /// <summary>Factur-X (file name <c>factur-x.xml</c>): CII inside a PDF/A-3.</summary>
    public const string FacturX = "Factur-X";

    /// <summary>ZUGFeRD 1.0 or 2.x (file names <c>ZUGFeRD-invoice.xml</c>, <c>zugferd-invoice.xml</c>).</summary>
    public const string Zugferd = "ZUGFeRD";

    /// <summary>XRechnung embedded as <c>xrechnung.xml</c>; CII or UBL syntax.</summary>
    public const string XRechnung = "XRechnung";

    /// <summary>An attachment whose root element is <c>CrossIndustryInvoice</c> but whose file name is not a known one.</summary>
    public const string Cii = "CII";

    /// <summary>An attachment whose root element is a UBL <c>Invoice</c> or <c>CreditNote</c> but whose file name is not a known one.</summary>
    public const string Ubl = "UBL";
}
