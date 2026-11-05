using System.Text.Json;
using System.Text.Json.Serialization;
using DocProc.Domain.Common;
using DocProc.Domain.Fields;

namespace DocProc.Domain.Invoices;

public enum InvoiceKind
{
    Invoice,
    CreditNote,
    Proforma,
}

/// <summary>Freight, pallets, surcharges (charges) or document-level discounts (allowances).</summary>
public sealed record ChargeOrAllowance(string? Description, Money Amount, decimal? TaxRate, bool IsCharge);

/// <summary>
/// Canonical invoice / credit note header and lines (docs/02-data-model.md §4.2). Every extraction path
/// produces this shape; validation, review, exports and webhooks only ever see it.
/// </summary>
public sealed record Invoice
{
    public FieldValue<InvoiceKind> Kind { get; init; } = FieldValue<InvoiceKind>.Missing();
    public FieldValue<DocumentIdentifier> InvoiceNumber { get; init; } = FieldValue<DocumentIdentifier>.Missing();
    public FieldValue<DateOnly> IssueDate { get; init; } = FieldValue<DateOnly>.Missing();
    public FieldValue<DateOnly> DeliveryDate { get; init; } = FieldValue<DateOnly>.Missing();
    public FieldValue<DatePeriod> ServicePeriod { get; init; } = FieldValue<DatePeriod>.Missing();
    public FieldValue<DateOnly> DueDate { get; init; } = FieldValue<DateOnly>.Missing();
    public FieldValue<string> Currency { get; init; } = FieldValue<string>.Missing();
    public FieldValue<Party> Supplier { get; init; } = FieldValue<Party>.Missing();
    public FieldValue<Party> Customer { get; init; } = FieldValue<Party>.Missing();
    public IReadOnlyList<FieldValue<DocumentIdentifier>> OrderReferences { get; init; } = [];
    public IReadOnlyList<FieldValue<DocumentIdentifier>> DeliveryNoteReferences { get; init; } = [];
    public FieldValue<DocumentIdentifier> OriginalInvoiceReference { get; init; } = FieldValue<DocumentIdentifier>.Missing();
    public FieldValue<Money> SubtotalNet { get; init; } = FieldValue<Money>.Missing();
    public IReadOnlyList<TaxBreakdownLine> TaxBreakdown { get; init; } = [];
    public FieldValue<Money> TotalTax { get; init; } = FieldValue<Money>.Missing();
    public FieldValue<Money> TotalGross { get; init; } = FieldValue<Money>.Missing();
    public FieldValue<Money> AmountDue { get; init; } = FieldValue<Money>.Missing();
    public FieldValue<Money> Rounding { get; init; } = FieldValue<Money>.Missing();
    public IReadOnlyList<ChargeOrAllowance> Charges { get; init; } = [];
    public IReadOnlyList<ChargeOrAllowance> Allowances { get; init; } = [];
    public FieldValue<PaymentTerms> PaymentTerms { get; init; } = FieldValue<PaymentTerms>.Missing();
    public FieldValue<string> Notes { get; init; } = FieldValue<string>.Missing();
    public IReadOnlyList<InvoiceLine> Lines { get; init; } = [];

    /// <summary>
    /// Properties of the stored JSON that this record does not declare (custom fields of a tenant type, docs/11 §5.6),
    /// kept so a typed load, patch and save round trip does not drop them. Null when there are none. A dictionary of
    /// <see cref="JsonElement"/>, not a <c>JsonObject</c>: System.Text.Json writes a <c>JsonObject</c> extension bag as
    /// a bare nested object, which is invalid JSON (checked by <c>CanonicalRoundTripTests</c>).
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>Header fields with their canonical paths. Lines are enumerated separately.</summary>
    public IEnumerable<(string Path, IFieldValue Field)> EnumerateHeaderFields()
    {
        yield return ("kind", Kind);
        yield return ("invoiceNumber", InvoiceNumber);
        yield return ("issueDate", IssueDate);
        yield return ("deliveryDate", DeliveryDate);
        yield return ("servicePeriod", ServicePeriod);
        yield return ("dueDate", DueDate);
        yield return ("currency", Currency);
        yield return ("supplier", Supplier);
        yield return ("customer", Customer);
        for (var i = 0; i < OrderReferences.Count; i++)
        {
            yield return ($"orderReferences[{i}]", OrderReferences[i]);
        }

        for (var i = 0; i < DeliveryNoteReferences.Count; i++)
        {
            yield return ($"deliveryNoteReferences[{i}]", DeliveryNoteReferences[i]);
        }

        yield return ("originalInvoiceReference", OriginalInvoiceReference);
        yield return ("subtotalNet", SubtotalNet);
        yield return ("totalTax", TotalTax);
        yield return ("totalGross", TotalGross);
        yield return ("amountDue", AmountDue);
        yield return ("rounding", Rounding);
        yield return ("paymentTerms", PaymentTerms);
        yield return ("notes", Notes);
    }

    /// <summary>Every provenance-carrying field on the document, header and lines.</summary>
    public IEnumerable<(string Path, IFieldValue Field)> EnumerateAllFields()
    {
        foreach (var f in EnumerateHeaderFields())
        {
            yield return f;
        }

        for (var i = 0; i < Lines.Count; i++)
        {
            foreach (var f in Lines[i].EnumerateFields($"lines[{i}]"))
            {
                yield return f;
            }
        }
    }
}
