using DocProc.Domain.Common;
using DocProc.Domain.Fields;

namespace DocProc.Domain.Invoices;

/// <summary>One printed line of an invoice or credit note. Deposits and discounts printed as their own lines are their own entries.</summary>
public sealed record InvoiceLine
{
    public required int LineNumber { get; init; }

    public FieldValue<string> SupplierSku { get; init; } = FieldValue<string>.Missing();
    public FieldValue<string> Gtin { get; init; } = FieldValue<string>.Missing();
    public FieldValue<string> Description { get; init; } = FieldValue<string>.Missing();
    public FieldValue<decimal> Quantity { get; init; } = FieldValue<decimal>.Missing();
    public FieldValue<UnitOfMeasure> Unit { get; init; } = FieldValue<UnitOfMeasure>.Missing();

    /// <summary>The unit exactly as printed; <see cref="Unit"/> is the normalised form.</summary>
    public string? UnitRaw { get; init; }

    public FieldValue<string> PackSize { get; init; } = FieldValue<string>.Missing();
    public FieldValue<Money> UnitPrice { get; init; } = FieldValue<Money>.Missing();
    public FieldValue<decimal> DiscountPercent { get; init; } = FieldValue<decimal>.Missing();
    public FieldValue<Money> DiscountAmount { get; init; } = FieldValue<Money>.Missing();
    public FieldValue<Money> LineNet { get; init; } = FieldValue<Money>.Missing();
    public FieldValue<decimal> TaxRate { get; init; } = FieldValue<decimal>.Missing();
    public FieldValue<Money> LineGross { get; init; } = FieldValue<Money>.Missing();
    public FieldValue<Money> DepositAmount { get; init; } = FieldValue<Money>.Missing();
    public FieldValue<string> LotNumber { get; init; } = FieldValue<string>.Missing();
    public FieldValue<DateOnly> BestBefore { get; init; } = FieldValue<DateOnly>.Missing();

    /// <summary>Tenant-side catalog mapping, filled by matching, never by extraction.</summary>
    public Guid? MatchedProductId { get; init; }

    /// <summary>All provenance-carrying fields with their JSON-path style names, for validation and the review UI.</summary>
    public IEnumerable<(string Path, IFieldValue Field)> EnumerateFields(string prefix)
    {
        yield return ($"{prefix}.supplierSku", SupplierSku);
        yield return ($"{prefix}.gtin", Gtin);
        yield return ($"{prefix}.description", Description);
        yield return ($"{prefix}.quantity", Quantity);
        yield return ($"{prefix}.unit", Unit);
        yield return ($"{prefix}.packSize", PackSize);
        yield return ($"{prefix}.unitPrice", UnitPrice);
        yield return ($"{prefix}.discountPercent", DiscountPercent);
        yield return ($"{prefix}.discountAmount", DiscountAmount);
        yield return ($"{prefix}.lineNet", LineNet);
        yield return ($"{prefix}.taxRate", TaxRate);
        yield return ($"{prefix}.lineGross", LineGross);
        yield return ($"{prefix}.depositAmount", DepositAmount);
        yield return ($"{prefix}.lotNumber", LotNumber);
        yield return ($"{prefix}.bestBefore", BestBefore);
    }
}
