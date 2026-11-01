using DocProc.Domain.Fields;

namespace DocProc.Llm.Extraction;

/// <summary>How a returned value was found in the positioned text layer (docs/06 "Provenance without citations").</summary>
public enum MatchQuality
{
    /// <summary>A run of words equals the value character for character.</summary>
    Exact,

    /// <summary>Equal after whitespace, case, edge punctuation or number-format normalisation.</summary>
    Normalized,

    /// <summary>Close under diacritic folding and a small edit distance, or contained in a longer word.</summary>
    Fuzzy,

    /// <summary>Nowhere in the text layer, or there is no text layer.</summary>
    NotFound,
}

/// <summary>
/// Confidence derived from locatability alone (docs/06 "Confidence"): the model's self-reported numbers are never
/// used. Validation, supplier history and double extraction adjust these further downstream.
/// </summary>
public static class LocatabilityConfidence
{
    public const double Exact = 0.95;
    public const double Normalized = 0.9;
    public const double Fuzzy = 0.8;
    public const double NotFound = 0.5;

    public static double For(MatchQuality quality) => quality switch
    {
        MatchQuality.Exact => Exact,
        MatchQuality.Normalized => Normalized,
        MatchQuality.Fuzzy => Fuzzy,
        _ => NotFound,
    };
}

/// <summary>Where a value was located: page and box in the 1000 × 1000 page space plus the words that matched.</summary>
public sealed record ProvenanceMatch(MatchQuality Quality, int? Page, BoundingBox? Box, string? RawText)
{
    public static readonly ProvenanceMatch None = new(MatchQuality.NotFound, null, null, null);

    public bool IsFound => Quality != MatchQuality.NotFound;

    public double Confidence => LocatabilityConfidence.For(Quality);

    public FieldSource ToSource() => new(SourceMethod.Llm, Page, Box, RawText);

    /// <summary>The weaker of two matches, used when one field is backed by several values (a party by name and VAT id).</summary>
    public static ProvenanceMatch Weaker(ProvenanceMatch a, ProvenanceMatch b) => a.Quality >= b.Quality ? a : b;
}
