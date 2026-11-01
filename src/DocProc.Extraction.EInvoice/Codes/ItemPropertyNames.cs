using System.Collections.Frozen;

namespace DocProc.Extraction.EInvoice.Codes;

/// <summary>
/// Names under which suppliers put lot numbers and best-before dates into free-form item attributes
/// (UBL AdditionalItemProperty, CII ApplicableProductCharacteristic). EN 16931 has no dedicated element for either.
/// </summary>
public static class ItemPropertyNames
{
    private static readonly FrozenSet<string> Lot = new[]
    {
        "lot", "lot number", "lotnumber", "lot no", "lot no.", "batch", "batch number", "batchnumber", "batch no", "batch no.",
        "charge", "chargennummer", "chargennr", "chargen-nr", "chargen-nr.", "charge nr", "charge nr.", "lotto", "šarža", "sarza",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> BestBefore = new[]
    {
        "best before", "bestbefore", "best before date", "best-before", "bbd", "bbe", "expiry", "expiry date", "use by", "use-by",
        "mhd", "mindesthaltbarkeitsdatum", "mindesthaltbarkeit", "haltbar bis", "verbrauchsdatum", "rok trajanja", "upotrijebiti do",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsLot(string? name) => name is not null && Lot.Contains(name.Trim());

    public static bool IsBestBefore(string? name) => name is not null && BestBefore.Contains(name.Trim());
}
