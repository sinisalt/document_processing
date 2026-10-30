using DocProc.Domain.Invoices;

namespace DocProc.Application.Abstractions;

public enum EInvoiceFlavour
{
    /// <summary>OASIS UBL 2.1 Invoice / CreditNote (Peppol BIS 3.0, XRechnung UBL).</summary>
    Ubl,

    /// <summary>UN/CEFACT Cross Industry Invoice (Factur-X / ZUGFeRD / XRechnung CII).</summary>
    Cii,
}

public sealed record EInvoiceDetection(EInvoiceFlavour Flavour, string? Profile, string RootElement);

/// <summary>
/// Stage 6a (docs/03): structured e-invoice XML to the canonical model, confidence 1.0, no OCR, no LLM.
/// Implementations live in DocProc.Extraction.EInvoice and are pure (XML in, Invoice out).
/// </summary>
public interface IEInvoiceParser
{
    /// <summary>Cheap sniff of the root element and namespace; null when the XML is not a supported e-invoice.</summary>
    EInvoiceDetection? Detect(ReadOnlyMemory<byte> xml);

    /// <summary>Parses a detected document. Fields the XML does not carry are Missing; nothing is guessed.</summary>
    Invoice Parse(ReadOnlyMemory<byte> xml, EInvoiceDetection detection);
}
