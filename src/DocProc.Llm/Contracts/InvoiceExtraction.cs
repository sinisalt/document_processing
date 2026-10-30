using System.Text.Json.Serialization;

namespace DocProc.Llm.Contracts;

// Extraction output contracts. These are what the model returns, written to the structured-output
// intersection of Anthropic and OpenAI (docs/08 "Schema portability"): every property present, optional
// values nullable, no numeric or length constraints, no recursion. Normalisation into the canonical
// FieldValue model happens in code afterwards; the model copies values verbatim.

public sealed record ExtractedParty
{
    public string? Name { get; init; }
    public string? VatId { get; init; }
    public string? TaxId { get; init; }
    public string? RegistrationNumber { get; init; }
    public string? AddressLine1 { get; init; }
    public string? AddressLine2 { get; init; }
    public string? PostalCode { get; init; }
    public string? City { get; init; }
    public string? CountryCode { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Iban { get; init; }
    public string? Bic { get; init; }
}

public sealed record ExtractedTaxRow
{
    public decimal? RatePercent { get; init; }
    public decimal? TaxableAmount { get; init; }
    public decimal? TaxAmount { get; init; }

    /// <summary>UNCL 5305 code as printed, when any: S, Z, E, AE, O, K, G.</summary>
    public string? CategoryCode { get; init; }
}

public sealed record ExtractedChargeOrAllowance
{
    public string? Description { get; init; }
    public decimal? Amount { get; init; }
    public decimal? TaxRatePercent { get; init; }
    public required bool IsCharge { get; init; }
}

public sealed record ExtractedPaymentTerms
{
    public DateOnly? DueDate { get; init; }
    public decimal? DiscountPercent { get; init; }
    public DateOnly? DiscountUntil { get; init; }
    public string? PaymentReference { get; init; }
    public string? Iban { get; init; }
    public string? Bic { get; init; }
    public string? RawText { get; init; }
}

public sealed record ExtractedInvoiceLine
{
    public required int LineNumber { get; init; }
    public string? SupplierSku { get; init; }
    public string? Gtin { get; init; }
    public string? Description { get; init; }
    public decimal? Quantity { get; init; }

    /// <summary>Unit exactly as printed; normalised in code.</summary>
    public string? Unit { get; init; }

    public string? PackSize { get; init; }
    public decimal? UnitPrice { get; init; }
    public decimal? DiscountPercent { get; init; }
    public decimal? DiscountAmount { get; init; }
    public decimal? LineNet { get; init; }
    public decimal? TaxRatePercent { get; init; }
    public decimal? LineGross { get; init; }
    public decimal? DepositAmount { get; init; }
    public string? LotNumber { get; init; }
    public DateOnly? BestBefore { get; init; }

    /// <summary>Page the line was read from, for provenance.</summary>
    public int? Page { get; init; }
}

/// <summary>A region the model could not read, so the page can be flagged (docs/06 "Request design").</summary>
public sealed record UnreadableRegion
{
    public required int Page { get; init; }

    /// <summary>Rough location in words: "top right", "line items table rows 4-6", "stamp over totals".</summary>
    public required string Area { get; init; }

    public required string Reason { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ExtractedInvoiceKind>))]
public enum ExtractedInvoiceKind
{
    Invoice,
    CreditNote,
    Proforma,
}

/// <summary>Model output for stage 6c, invoice and credit note. Version in <see cref="Schema.CanonicalSchemas"/>.</summary>
public sealed record InvoiceExtraction
{
    public ExtractedInvoiceKind? Kind { get; init; }
    public string? InvoiceNumber { get; init; }
    public DateOnly? IssueDate { get; init; }
    public DateOnly? DeliveryDate { get; init; }
    public DateOnly? ServicePeriodFrom { get; init; }
    public DateOnly? ServicePeriodTo { get; init; }
    public DateOnly? DueDate { get; init; }

    /// <summary>ISO 4217 as printed, or null when no currency is printed. Never guessed.</summary>
    public string? Currency { get; init; }

    public ExtractedParty? Supplier { get; init; }
    public ExtractedParty? Customer { get; init; }
    public required IReadOnlyList<string> OrderReferences { get; init; }
    public required IReadOnlyList<string> DeliveryNoteReferences { get; init; }
    public string? OriginalInvoiceReference { get; init; }
    public decimal? SubtotalNet { get; init; }
    public required IReadOnlyList<ExtractedTaxRow> TaxBreakdown { get; init; }
    public decimal? TotalTax { get; init; }
    public decimal? TotalGross { get; init; }
    public decimal? AmountDue { get; init; }
    public decimal? Rounding { get; init; }
    public required IReadOnlyList<ExtractedChargeOrAllowance> ChargesAndAllowances { get; init; }
    public ExtractedPaymentTerms? PaymentTerms { get; init; }
    public string? Notes { get; init; }
    public required IReadOnlyList<ExtractedInvoiceLine> Lines { get; init; }
    public required IReadOnlyList<UnreadableRegion> UnreadableRegions { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ClassifiedDocumentType>))]
public enum ClassifiedDocumentType
{
    Invoice,
    CreditNote,
    PurchaseOrder,
    DeliveryNote,
    Statement,
    Reminder,
    Other,
}

/// <summary>Model output for stage 4 when keyword rules are below threshold.</summary>
public sealed record ClassificationResult
{
    public required ClassifiedDocumentType DocumentType { get; init; }

    /// <summary>Self-reported, weakly calibrated; combined with rule signals, never used alone (docs/06 "Confidence").</summary>
    public required double Confidence { get; init; }

    public string? Language { get; init; }
    public string? SupplierNameHint { get; init; }
}

public sealed record SplitRange
{
    public required int StartPage { get; init; }
    public required int EndPage { get; init; }
    public ClassifiedDocumentType? DocumentType { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Model output for stage 3: contiguous page ranges, each one logical document.</summary>
public sealed record SplitResult
{
    public required IReadOnlyList<SplitRange> Documents { get; init; }
}
