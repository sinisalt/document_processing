using System.Collections.Frozen;
using DocProc.Domain.Common;

namespace DocProc.Extraction.EInvoice.Codes;

/// <summary>
/// UN/ECE Recommendation 20 (and the Rec 21 package codes EN 16931 admits) to the canonical
/// <see cref="UnitOfMeasure"/>. Unknown codes map to <see cref="UnitOfMeasure.Other"/>; the raw code is kept on the line.
/// </summary>
public static class UnitCodes
{
    private static readonly FrozenDictionary<string, UnitOfMeasure> Map = new Dictionary<string, UnitOfMeasure>(StringComparer.Ordinal)
    {
        ["KGM"] = UnitOfMeasure.Kilogram,
        ["GRM"] = UnitOfMeasure.Gram,
        ["LTR"] = UnitOfMeasure.Litre,
        ["MLT"] = UnitOfMeasure.Millilitre,
        ["C62"] = UnitOfMeasure.Piece,
        ["H87"] = UnitOfMeasure.Piece,
        ["EA"] = UnitOfMeasure.Piece,
        ["PCE"] = UnitOfMeasure.Piece,
        ["NAR"] = UnitOfMeasure.Piece,
        ["BX"] = UnitOfMeasure.Box,
        ["XBX"] = UnitOfMeasure.Box,
        ["CS"] = UnitOfMeasure.Case,
        ["XCS"] = UnitOfMeasure.Case,
        ["CR"] = UnitOfMeasure.Crate,
        ["XCR"] = UnitOfMeasure.Crate,
        ["PF"] = UnitOfMeasure.Pallet,
        ["PX"] = UnitOfMeasure.Pallet,
        ["XPX"] = UnitOfMeasure.Pallet,
        ["XPF"] = UnitOfMeasure.Pallet,
        ["HUR"] = UnitOfMeasure.Hour,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static UnitOfMeasure ToUnit(string? unitCode) =>
        unitCode is not null && Map.TryGetValue(unitCode.Trim().ToUpperInvariant(), out var unit) ? unit : UnitOfMeasure.Other;
}
