using System.Collections.Frozen;

namespace DocProc.Domain.Common;

/// <summary>Small normalised unit set. The raw printed unit is kept next to it on the line.</summary>
public enum UnitOfMeasure
{
    Other,
    Kilogram,
    Gram,
    Litre,
    Millilitre,
    Piece,
    Box,
    Case,
    Crate,
    Pallet,
    Hour,
}

public static class UnitOfMeasureParser
{
    private static readonly FrozenDictionary<string, UnitOfMeasure> Aliases = new Dictionary<string, UnitOfMeasure>(StringComparer.OrdinalIgnoreCase)
    {
        ["kg"] = UnitOfMeasure.Kilogram, ["kgs"] = UnitOfMeasure.Kilogram, ["kilo"] = UnitOfMeasure.Kilogram, ["kilogram"] = UnitOfMeasure.Kilogram, ["kilogramm"] = UnitOfMeasure.Kilogram,
        ["g"] = UnitOfMeasure.Gram, ["gr"] = UnitOfMeasure.Gram, ["gram"] = UnitOfMeasure.Gram, ["gramm"] = UnitOfMeasure.Gram,
        ["l"] = UnitOfMeasure.Litre, ["lt"] = UnitOfMeasure.Litre, ["ltr"] = UnitOfMeasure.Litre, ["liter"] = UnitOfMeasure.Litre, ["litre"] = UnitOfMeasure.Litre, ["litar"] = UnitOfMeasure.Litre,
        ["ml"] = UnitOfMeasure.Millilitre, ["milliliter"] = UnitOfMeasure.Millilitre,
        ["pc"] = UnitOfMeasure.Piece, ["pcs"] = UnitOfMeasure.Piece, ["piece"] = UnitOfMeasure.Piece, ["pieces"] = UnitOfMeasure.Piece, ["ea"] = UnitOfMeasure.Piece, ["each"] = UnitOfMeasure.Piece,
        ["stk"] = UnitOfMeasure.Piece, ["st"] = UnitOfMeasure.Piece, ["stück"] = UnitOfMeasure.Piece, ["stueck"] = UnitOfMeasure.Piece, ["kom"] = UnitOfMeasure.Piece, ["komad"] = UnitOfMeasure.Piece, ["pz"] = UnitOfMeasure.Piece, ["unité"] = UnitOfMeasure.Piece, ["un"] = UnitOfMeasure.Piece,
        ["box"] = UnitOfMeasure.Box, ["bx"] = UnitOfMeasure.Box, ["karton"] = UnitOfMeasure.Box, ["kt"] = UnitOfMeasure.Box, ["ctn"] = UnitOfMeasure.Box, ["carton"] = UnitOfMeasure.Box, ["kutija"] = UnitOfMeasure.Box,
        ["case"] = UnitOfMeasure.Case, ["cs"] = UnitOfMeasure.Case, ["tray"] = UnitOfMeasure.Case, ["gebinde"] = UnitOfMeasure.Case,
        ["crate"] = UnitOfMeasure.Crate, ["kiste"] = UnitOfMeasure.Crate, ["ki"] = UnitOfMeasure.Crate, ["kasten"] = UnitOfMeasure.Crate, ["sanduk"] = UnitOfMeasure.Crate, ["gajba"] = UnitOfMeasure.Crate,
        ["pal"] = UnitOfMeasure.Pallet, ["pallet"] = UnitOfMeasure.Pallet, ["palette"] = UnitOfMeasure.Pallet, ["paleta"] = UnitOfMeasure.Pallet,
        ["h"] = UnitOfMeasure.Hour, ["hr"] = UnitOfMeasure.Hour, ["hour"] = UnitOfMeasure.Hour, ["hours"] = UnitOfMeasure.Hour, ["std"] = UnitOfMeasure.Hour, ["stunde"] = UnitOfMeasure.Hour, ["stunden"] = UnitOfMeasure.Hour, ["sat"] = UnitOfMeasure.Hour,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maps a printed unit to the normalised enum; unknown or empty input maps to <see cref="UnitOfMeasure.Other"/>.</summary>
    public static UnitOfMeasure Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return UnitOfMeasure.Other;
        }

        var key = raw.Trim().TrimEnd('.');
        return Aliases.TryGetValue(key, out var unit) ? unit : UnitOfMeasure.Other;
    }
}
