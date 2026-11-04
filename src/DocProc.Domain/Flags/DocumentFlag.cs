using System.Collections.Frozen;
using System.Globalization;

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

/// <summary>
/// Something the customer should see about a document: unreadable data, failed checks, informational notes.
/// <see cref="Message"/> is the English text for API clients; <see cref="Params"/> carries the same facts as invariant
/// strings (field paths, ISO dates, amounts and ratios with a dot, and a <c>reason</c> naming the variant when a code
/// has several messages), so a UI can say it in its own language (docs/02 §5 "Flag parameters").
/// </summary>
public sealed record DocumentFlag(
    string Code,
    FlagSeverity Severity,
    string Message,
    string? Field = null,
    FlagOrigin CreatedBy = FlagOrigin.Pipeline,
    DateTimeOffset? ResolvedAt = null,
    IReadOnlyDictionary<string, string>? Params = null)
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

    // Definition-driven validation (docs/11 §11).
    public const string FieldRequired = "FIELD_REQUIRED";
    public const string FieldInvalid = "FIELD_INVALID";
    public const string RuleFailed = "RULE_FAILED";
    public const string RevisionOf = "REVISION_OF";

    /// <summary>Checks the document reached a per-document limit for (docs/11 §11, <c>PatternBudget</c>) did not run.</summary>
    public const string ChecksSkipped = "CHECKS_SKIPPED";

    /// <summary>A delivery of the document was dead-lettered (docs/11 §13.6): retries ran out or the failure is permanent.</summary>
    public const string DeliveryFailed = "DELIVERY_FAILED";

    // Source routing and supplier corroboration (docs/11 §7.2, §7.4, P4).

    /// <summary>A sender rule would have matched but the sender is not authenticated (DMARC); the document never auto-approves.</summary>
    public const string SupplierSourceUnverified = "SUPPLIER_SOURCE_UNVERIFIED";

    /// <summary>Source rules (or VAT and IBAN) of several suppliers match equally; no supplier was assigned.</summary>
    public const string SupplierAmbiguous = "SUPPLIER_AMBIGUOUS";

    /// <summary>The content names another supplier than an authoritative source rule; the source supplier is kept and review decides.</summary>
    public const string SupplierConflict = "SUPPLIER_CONFLICT";

    // Supplier layouts and identification (docs/11 §8.4, §8.2, P4).

    /// <summary>The supplier is layout-managed and none of its Active layouts' identification rules holds; the document is held for review.</summary>
    public const string NewLayout = "NEW_LAYOUT";

    /// <summary>Two Active layouts of the supplier hold with equal priority and specificity; none is applied.</summary>
    public const string LayoutAmbiguous = "LAYOUT_AMBIGUOUS";

    /// <summary>The layout's identification rule held, but its field rules found less than the layout's usual coverage.</summary>
    public const string MappingDrift = "MAPPING_DRIFT";

    /// <summary>The document is one of the first under a new layout version (its review warm-up, decision 2 of docs/11 §24); it goes to review.</summary>
    public const string LayoutWarmup = "LAYOUT_WARMUP";

    /// <summary>The prefix of definition-scoped codes (<c>X_&lt;RULEID&gt;</c>), which come with their own message.</summary>
    public static readonly string DefinitionScopedPrefix = "X_";

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
        new(FieldRequired, FlagSeverity.Blocking, "A field the document type requires has no value."),
        new(FieldInvalid, FlagSeverity.Warning, "A value does not fit its field's definition (format, range, length or code); blocking for a required field."),
        new(RuleFailed, FlagSeverity.Warning, "A rule of the document type's definition failed; the message is the rule's."),
        new(RevisionOf, FlagSeverity.Warning, "An earlier document with the same duplicate key exists; this one may replace it."),
        new(ChecksSkipped, FlagSeverity.Blocking, "Checks were skipped because the document reached a per-document limit (pattern evaluations); a reviewer checks the values."),
        new(SupplierSourceUnverified, FlagSeverity.Warning, "A source rule for another supplier would have matched, but the sender is not authenticated (DMARC); the document is never auto-approved."),
        new(SupplierAmbiguous, FlagSeverity.Warning, "Several suppliers match the source rules (or the VAT id and IBAN) equally; review assigns the supplier."),
        new(SupplierConflict, FlagSeverity.Blocking, "The document's content names another supplier than its authoritative source rule; the source supplier is kept until review decides."),
        new(NewLayout, FlagSeverity.Blocking, "The supplier sent a design none of its layouts recognises; the document is held for review and not processed with another layout."),
        new(LayoutAmbiguous, FlagSeverity.Blocking, "Several layouts of the supplier recognise the document equally; none was applied and review decides."),
        new(MappingDrift, FlagSeverity.Warning, "The layout recognised the document, but its field rules found fewer fields than usual; the supplier may have moved them."),
        new(LayoutWarmup, FlagSeverity.Info, "The document is one of the first documents under a new layout version and goes to review (the layout's warm-up), already filled by the layout."),
        new(DeliveryFailed, FlagSeverity.Warning, "A delivery to a destination failed for good (retries ran out or the receiver refused it); an integration admin redelivers it."),
    }.ToFrozenDictionary(d => d.Code, StringComparer.Ordinal);

    public static bool IsKnown(string code) => Catalogue.ContainsKey(code);

    /// <summary>Whether the code is a definition-scoped one (<c>X_&lt;RULEID&gt;</c>, docs/11 §11): upper-case letters, digits and underscores after the prefix.</summary>
    public static bool IsDefinitionScoped(string code) =>
        code.Length > DefinitionScopedPrefix.Length && code.Length <= 64 && code.StartsWith(DefinitionScopedPrefix, StringComparison.Ordinal)
        && code.Skip(DefinitionScopedPrefix.Length).All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_');

    /// <summary>
    /// Creates a flag with the catalogue's default severity for the code. A definition-scoped code (<c>X_*</c>) is not
    /// in the catalogue: it carries the rule's own message and is a warning unless the caller sets another severity.
    /// </summary>
    public static DocumentFlag Create(string code, string message, string? field = null, IReadOnlyDictionary<string, string>? parameters = null)
    {
        FlagSeverity severity;
        if (Catalogue.TryGetValue(code, out var def))
        {
            severity = def.DefaultSeverity;
        }
        else if (IsDefinitionScoped(code))
        {
            severity = FlagSeverity.Warning;
        }
        else
        {
            throw new ArgumentException($"Unknown flag code '{code}'.", nameof(code));
        }

        return new DocumentFlag(code, severity, message, field, Params: parameters is { Count: > 0 } ? parameters : null);
    }
}

/// <summary>
/// Builds a flag's <see cref="DocumentFlag.Params"/>: invariant strings only, so a value reads the same in every
/// culture and the UI formats it itself. Keys are camelCase; <see cref="Reason"/> names the message variant of a code
/// that has several (docs/02 §5 "Flag parameters").
/// </summary>
public static class FlagParams
{
    /// <summary>The key that tells a code's message variants apart (<c>DATE_IMPLAUSIBLE</c>: <c>future</c>, <c>tooOld</c>, ...).</summary>
    public const string Reason = "reason";

    /// <summary>The pairs that have a value; a null value (an unknown VAT id, no number) is left out.</summary>
    public static IReadOnlyDictionary<string, string> Of(params (string Key, string? Value)[] pairs)
    {
        var result = new Dictionary<string, string>(pairs.Length, StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            if (value is not null)
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>A money amount with two decimals and a dot (<c>1234.50</c>).</summary>
    public static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>A confidence or threshold with two decimals (<c>0.80</c>).</summary>
    public static string Ratio(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>An ISO date (<c>2026-10-31</c>).</summary>
    public static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A plain number as printed (a quantity, a tax rate, a line or page number): invariant, no fixed decimals.</summary>
    public static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
