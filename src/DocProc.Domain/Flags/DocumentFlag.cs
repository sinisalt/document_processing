using System.Collections.Frozen;

namespace DocProc.Domain.Flags;

public enum FlagSeverity
{
    Info,
    Warning,
    Blocking,
}

public enum FlagOrigin
{
    Pipeline,
    Human,
}

/// <summary>Something the customer should see about a document: unreadable data, failed checks, informational notes.</summary>
public sealed record DocumentFlag(
    string Code,
    FlagSeverity Severity,
    string Message,
    string? Field = null,
    FlagOrigin CreatedBy = FlagOrigin.Pipeline,
    DateTimeOffset? ResolvedAt = null)
{
    public bool IsResolved => ResolvedAt is not null;

    public DocumentFlag Resolve(DateTimeOffset at) => this with { ResolvedAt = at };
}

/// <summary>Flag catalogue (docs/02-data-model.md §5). Codes are stable strings so they can be stored, filtered and documented.</summary>
public static class FlagCodes
{
    public const string UnreadablePages = "UNREADABLE_PAGES";
    public const string TypeUnknown = "TYPE_UNKNOWN";
    public const string SupplierUnmatched = "SUPPLIER_UNMATCHED";
    public const string CustomerMismatch = "CUSTOMER_MISMATCH";
    public const string MissingRequired = "MISSING_REQUIRED";
    public const string TotalsMismatch = "TOTALS_MISMATCH";
    public const string TaxMismatch = "TAX_MISMATCH";
    public const string LineArithmetic = "LINE_ARITHMETIC";
    public const string LowConfidence = "LOW_CONFIDENCE";
    public const string DuplicateSuspected = "DUPLICATE_SUSPECTED";
    public const string ProfileFallback = "PROFILE_FALLBACK";
    public const string LlmProviderUnavailable = "LLM_PROVIDER_UNAVAILABLE";
    public const string MultiDocumentSplit = "MULTI_DOCUMENT_SPLIT";
    public const string CurrencyInferred = "CURRENCY_INFERRED";
    public const string PoUnmatched = "PO_UNMATCHED";
    public const string DateImplausible = "DATE_IMPLAUSIBLE";
    public const string InvalidValue = "INVALID_VALUE";
    public const string UnsupportedType = "UNSUPPORTED_TYPE";

    public sealed record Definition(string Code, FlagSeverity DefaultSeverity, string Description);

    public static readonly FrozenDictionary<string, Definition> Catalogue = new Definition[]
    {
        new(UnreadablePages, FlagSeverity.Blocking, "OCR text density below threshold or image unreadable."),
        new(TypeUnknown, FlagSeverity.Blocking, "Classifier confidence below threshold."),
        new(SupplierUnmatched, FlagSeverity.Warning, "No supplier matched; a new supplier is suggested."),
        new(CustomerMismatch, FlagSeverity.Blocking, "The customer block does not look like this tenant."),
        new(MissingRequired, FlagSeverity.Blocking, "A required header field is missing or unreadable."),
        new(TotalsMismatch, FlagSeverity.Blocking, "Line sum does not match the subtotal, or net plus tax does not match the gross total."),
        new(TaxMismatch, FlagSeverity.Warning, "The tax breakdown does not reproduce the total tax."),
        new(LineArithmetic, FlagSeverity.Warning, "Quantity times unit price does not match the line net."),
        new(LowConfidence, FlagSeverity.Warning, "A field's confidence is below the tenant threshold."),
        new(DuplicateSuspected, FlagSeverity.Blocking, "A document with the same supplier and number already exists."),
        new(ProfileFallback, FlagSeverity.Info, "The extractor profile failed and the LLM path was used."),
        new(LlmProviderUnavailable, FlagSeverity.Blocking, "The tenant's LLM provider is invalid, over quota or capped; the document waits."),
        new(MultiDocumentSplit, FlagSeverity.Info, "The attachment was split into several documents."),
        new(CurrencyInferred, FlagSeverity.Info, "No currency printed; the tenant default was assumed."),
        new(PoUnmatched, FlagSeverity.Info, "A PO reference is present but no matching purchase order was found."),
        new(DateImplausible, FlagSeverity.Warning, "Issue date is in the future or more than a year old."),
        new(InvalidValue, FlagSeverity.Warning, "A value failed its type or format check (for example a malformed currency code)."),
        new(UnsupportedType, FlagSeverity.Warning, "The document type has no extraction path yet; the reviewer handles it by hand."),
    }.ToFrozenDictionary(d => d.Code, StringComparer.Ordinal);

    public static bool IsKnown(string code) => Catalogue.ContainsKey(code);

    /// <summary>Creates a flag with the catalogue's default severity for the code.</summary>
    public static DocumentFlag Create(string code, string message, string? field = null)
    {
        if (!Catalogue.TryGetValue(code, out var def))
        {
            throw new ArgumentException($"Unknown flag code '{code}'.", nameof(code));
        }

        return new DocumentFlag(code, def.DefaultSeverity, message, field);
    }
}
