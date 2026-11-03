using DocProc.Application.Configuration;
using DocProc.Application.Outputs;
using DocProc.Definitions.Compilation;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Domain.Validation;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Pipeline.Support;

/// <summary>
/// The checks of the validate stage (docs/11 §11) over a document's stored tree, shared by the stage and by a
/// reviewer's correction of a definition-driven document, which re-validates inline (docs/11 §12.8).
/// </summary>
public static class DocumentChecks
{
    /// <summary>
    /// Replaces the open pipeline flags of <paramref name="ownedCodes"/> (and of the incoming codes) with
    /// <paramref name="flags"/>, as the stages do; what a correction's inline validation writes.
    /// </summary>
    public static void ReplaceFlags(DocProcDbContext db, Document document, IEnumerable<string> ownedCodes, IEnumerable<DocumentFlag> flags, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(document);
        var incoming = flags.ToList();
        var codes = new HashSet<string>(ownedCodes, StringComparer.Ordinal);
        codes.UnionWith(incoming.Select(f => f.Code));
        foreach (var stale in document.Flags.Where(f => f.CreatedBy == FlagOrigin.Pipeline && f.ResolvedAt is null && codes.Contains(f.Code)).ToList())
        {
            document.Flags.Remove(stale);
            db.DocumentFlags.Remove(stale);
        }

        foreach (var flag in incoming)
        {
            var record = DocumentFlagRecord.FromDomain(document.TenantId, document.Id, flag, now);
            db.DocumentFlags.Add(record);
            // EF's fix-up has usually put it into the tracked navigation already; a second Add would list it twice.
            if (!document.Flags.Contains(record))
            {
                document.Flags.Add(record);
            }
        }
    }

    /// <summary>Today in the tenant's time zone (<c>Tenants.TimeZone</c>), UTC when the zone is unknown.</summary>
    public static DateOnly TodayFor(Tenant tenant, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone);
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(now.UtcDateTime);
        }
        catch (InvalidTimeZoneException)
        {
            return DateOnly.FromDateTime(now.UtcDateTime);
        }
    }

    /// <summary>The flags the checks own (raise and replace).</summary>
    public static readonly string[] OwnedFlags =
    [
        FlagCodes.MissingRequired, FlagCodes.InvalidValue, FlagCodes.TotalsMismatch, FlagCodes.TaxMismatch, FlagCodes.LineArithmetic,
        FlagCodes.DateImplausible, FlagCodes.CustomerMismatch, FlagCodes.LowConfidence, FlagCodes.CurrencyInferred, FlagCodes.DuplicateSuspected,
        FlagCodes.FieldRequired, FlagCodes.FieldInvalid, FlagCodes.RuleFailed, FlagCodes.RevisionOf,
    ];

    /// <summary>The checks' flags and the rule result (null when the type lists no rules).</summary>
    public sealed record CheckRun(IReadOnlyList<DocumentFlag> Flags, ValidationResult? Result);

    /// <summary>
    /// The checks of the validate steps of <paramref name="type"/> over the stored tree of <paramref name="document"/>:
    /// the structural check (for a type without <c>validate.schema</c>, its <c>FIELD_REQUIRED</c> part for the fields
    /// <see cref="EffectiveType.RequiredOutsideSchemaStep"/> names), the rule list (with the duplicate check of the type's key for a <c>duplicate</c> rule) and
    /// the coded duplicate check, each when the type runs it. Also what a correction re-validates inline (docs/11 §12.8).
    /// </summary>
    public static async Task<CheckRun> RunAsync(DocProcDbContext db, Document document, EffectiveType type, Tenant tenant, DateOnly today, DocumentValidator validator, CancellationToken cancellationToken)
    {
        var flags = new List<DocumentFlag>();
        var view = DocumentView.FromJson(document.Type, document.CanonicalJson);
        var compiled = type.Compiled;
        if (compiled is not null && view.Root is { } root)
        {
            view.WithValues(DocumentDataView.Values(root, compiled.Descriptors), RolePaths(type));
        }

        if (type.RunsValidateStep(ValidateStepKinds.Schema) && compiled is not null && view.Values is { } values)
        {
            flags.AddRange(StructuralValidator.Validate(values, compiled));
        }
        else if (type.RequiredOutsideSchemaStep.Count > 0 && compiled is not null && view.Values is { } partial)
        {
            // Without validate.schema a field the tenant marked required is still checked (docs/11 §11): a copied
            // invoice's required custom field must keep the document out of auto-approval.
            flags.AddRange(StructuralValidator.ValidateRequired(partial, compiled, type.RequiredOutsideSchemaStep));
        }

        ValidationResult? result = null;
        if (type.Rules.Count > 0 && type.RunsValidateStep(ValidateStepKinds.Rules))
        {
            var data = RuleData.None;
            if (type.Rules.Any(r => r.Kind == Domain.Validation.Rules.GenericRules.Duplicate))
            {
                data = data with { Duplicate = await DuplicateKeyCheck.RunAsync(db, document, type, cancellationToken).ConfigureAwait(false) };
            }

            var context = new ValidationContext(
                today,
                ValidationOptions.Default with { ConfidenceThreshold = tenant.Settings.AutoApproveConfidenceThreshold },
                new Domain.Validation.TenantContext(tenant.LegalName, tenant.VatId, tenant.DefaultCurrency, tenant.NameAliases))
            { Data = data };
            result = validator.Validate(view, type.Rules, context);
            flags.AddRange(result.Flags);
        }

        if (type.Rules.Count > 0 && type.RunsValidateStep(ValidateStepKinds.Duplicate) && await IsDuplicateAsync(db, document, cancellationToken).ConfigureAwait(false))
        {
            flags.Add(FlagCodes.Create(FlagCodes.DuplicateSuspected, $"Another document of this supplier carries the number {document.DocumentNumber}.", "invoiceNumber", FlagParams.Of(("number", document.DocumentNumber))));
        }

        return new CheckRun(flags, result);
    }

    /// <summary>The role bindings as the generic rules read them (role name to path; <c>duplicateKey</c> joined with commas).</summary>
    public static IReadOnlyDictionary<string, string> RolePaths(EffectiveType type)
    {
        var roles = type.Definition.Roles;
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, path) in new (string, string?)[]
        {
            ("documentNumber", roles.DocumentNumber), ("documentDate", roles.DocumentDate), ("dueDate", roles.DueDate), ("currency", roles.Currency),
            ("total", roles.Total), ("net", roles.Net), ("tax", roles.Tax), ("validFrom", roles.ValidFrom), ("validUntil", roles.ValidUntil),
            ("supplierParty", roles.SupplierParty), ("customerParty", roles.CustomerParty), ("lineItems", roles.LineItems),
        })
        {
            if (path is not null)
            {
                paths[name] = path;
            }
        }

        if (roles.DuplicateKey is { Count: > 0 } key)
        {
            paths["duplicateKey"] = string.Join(", ", key);
        }

        return paths;
    }

    /// <summary>The review task's priority from the type's <c>review.priority</c> (docs/11 §6.2); Normal without one.</summary>
    public static ReviewPriority PriorityOf(EffectiveType? type) => type?.Definition.Processing.Review.Priority switch
    {
        "urgent" => ReviewPriority.Urgent,
        "high" => ReviewPriority.High,
        _ => ReviewPriority.Normal,
    };

    /// <summary>
    /// Same tenant, same matched supplier, same normalised number, on a document that is still alive. Unmatched
    /// documents are never compared with each other: "no supplier" is not a supplier, and two unrelated vendors can
    /// both number their first invoice 1.
    /// </summary>
    private static Task<bool> IsDuplicateAsync(DocProcDbContext db, Document document, CancellationToken cancellationToken)
    {
        if (document.SupplierId is not { } supplierId || document.DocumentNumberNormalized is not { Length: > 0 } number)
        {
            return Task.FromResult(false);
        }

        var tenantId = document.TenantId;
        return db.Documents.AsNoTracking().AnyAsync(
            d => d.TenantId == tenantId
                 && d.Id != document.Id
                 && d.SupplierId == supplierId
                 && d.DocumentNumberNormalized == number
                 && d.Status != DocumentStatus.Failed
                 && d.Status != DocumentStatus.Rejected,
            cancellationToken);
    }
}
