namespace DocProc.Domain.Common;

/// <summary>EN 16931 / UNCL 5305 VAT category codes.</summary>
public enum TaxCategory
{
    /// <summary>S – standard rate.</summary>
    Standard,

    /// <summary>Z – zero rated.</summary>
    Zero,

    /// <summary>E – exempt.</summary>
    Exempt,

    /// <summary>AE – reverse charge.</summary>
    ReverseCharge,

    /// <summary>O – outside scope of tax.</summary>
    OutsideScope,

    /// <summary>K – intra-community supply.</summary>
    IntraCommunity,

    /// <summary>G – export outside the EU.</summary>
    Export,
}

/// <summary>One row of the VAT summary: a rate, the taxable base at that rate and the resulting tax.</summary>
public sealed record TaxBreakdownLine(
    decimal Rate,
    Money TaxableAmount,
    Money TaxAmount,
    TaxCategory? Category = null)
{
    /// <summary>Tax this row should carry if the printed base and rate are right.</summary>
    public Money ExpectedTax => TaxableAmount * (Rate / 100m);
}
