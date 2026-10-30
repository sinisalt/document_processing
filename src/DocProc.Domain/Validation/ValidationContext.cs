namespace DocProc.Domain.Validation;

/// <summary>Tenant-level knobs for validation. Per-tenant defaults live in tenant settings; these are the fallbacks.</summary>
public sealed record ValidationOptions
{
    public static readonly ValidationOptions Default = new();

    /// <summary>Absolute tolerance for money comparisons, in currency units (covers per-line rounding).</summary>
    public decimal MoneyTolerance { get; init; } = 0.02m;

    /// <summary>Extra tolerance per line when summing lines into a subtotal (each printed line may be rounded).</summary>
    public decimal PerLineRoundingTolerance { get; init; } = 0.005m;

    /// <summary>Fields with a confidence below this raise LOW_CONFIDENCE.</summary>
    public double ConfidenceThreshold { get; init; } = 0.8;

    /// <summary>Issue dates older than this raise DATE_IMPLAUSIBLE.</summary>
    public int MaxDocumentAgeDays { get; init; } = 365;

    /// <summary>Issue dates further in the future than this raise DATE_IMPLAUSIBLE (allows for time-zone skew and post-dating).</summary>
    public int MaxFutureDays { get; init; } = 7;

    /// <summary>Minimum share of the tenant's name tokens that must appear in the customer block for a name match.</summary>
    public double CustomerNameMatchRatio { get; init; } = 0.5;
}

/// <summary>What validation knows about the tenant the document was addressed to.</summary>
public sealed record TenantContext(
    string LegalName,
    string? VatId,
    string DefaultCurrency,
    IReadOnlyList<string>? NameAliases = null);

/// <summary>Everything a rule may consult besides the document itself.</summary>
public sealed record ValidationContext(DateOnly Today, ValidationOptions Options, TenantContext? Tenant = null)
{
    public static ValidationContext For(DateOnly today, TenantContext? tenant = null) => new(today, ValidationOptions.Default, tenant);
}
