using System.Collections.Frozen;
using DocProc.Domain.Invoices;

namespace DocProc.Extraction.EInvoice.Codes;

/// <summary>
/// UNCL 1001 document type codes (UBL InvoiceTypeCode / CreditNoteTypeCode, CII TypeCode) to the canonical
/// <see cref="InvoiceKind"/>. 381 is a credit note; 380, 384 (corrected) and 386 (prepayment) are invoices, as are
/// the other invoice-family codes EN 16931 admits. Anything else is unknown and left to the caller, never guessed.
/// </summary>
public static class DocumentTypeCodes
{
    public const string CommercialInvoice = "380";
    public const string CreditNote = "381";
    public const string CorrectedInvoice = "384";
    public const string PrepaymentInvoice = "386";

    private static readonly FrozenDictionary<string, InvoiceKind> Map = new Dictionary<string, InvoiceKind>(StringComparer.Ordinal)
    {
        [CreditNote] = InvoiceKind.CreditNote,
        ["261"] = InvoiceKind.CreditNote, // self-billed credit note
        ["396"] = InvoiceKind.CreditNote, // factored credit note
        ["532"] = InvoiceKind.CreditNote, // forwarder's credit note
        [CommercialInvoice] = InvoiceKind.Invoice,
        [CorrectedInvoice] = InvoiceKind.Invoice,
        [PrepaymentInvoice] = InvoiceKind.Invoice,
        ["326"] = InvoiceKind.Invoice, // partial invoice
        ["383"] = InvoiceKind.Invoice, // debit note
        ["389"] = InvoiceKind.Invoice, // self-billed invoice
        ["393"] = InvoiceKind.Invoice, // factored invoice
        ["575"] = InvoiceKind.Invoice, // insurer's invoice
        ["623"] = InvoiceKind.Invoice, // forwarder's invoice
        ["780"] = InvoiceKind.Invoice, // freight invoice
        ["875"] = InvoiceKind.Invoice, // partial construction invoice
        ["876"] = InvoiceKind.Invoice, // partial final construction invoice
        ["877"] = InvoiceKind.Invoice, // final construction invoice
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static InvoiceKind? ToKind(string? code) =>
        code is not null && Map.TryGetValue(code.Trim(), out var kind) ? kind : null;
}
