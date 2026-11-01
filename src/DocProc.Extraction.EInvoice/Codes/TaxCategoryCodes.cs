using DocProc.Domain.Common;

namespace DocProc.Extraction.EInvoice.Codes;

/// <summary>UNCL 5305 VAT category codes used by EN 16931 (BT-118, BT-151) to the canonical <see cref="TaxCategory"/>.</summary>
public static class TaxCategoryCodes
{
    public static TaxCategory? ToCategory(string? code) => code?.Trim().ToUpperInvariant() switch
    {
        "S" => TaxCategory.Standard,
        "Z" => TaxCategory.Zero,
        "E" => TaxCategory.Exempt,
        "AE" => TaxCategory.ReverseCharge,
        "O" => TaxCategory.OutsideScope,
        "K" => TaxCategory.IntraCommunity,
        "G" => TaxCategory.Export,
        _ => null,
    };
}
