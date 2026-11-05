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
        foreach (var stale in document.Flags.Where(f => f.CreatedBy == FlagOrigin.Pipeline && f.ResolvedAt is null && FlagCodes.IsOwned(codes, f.Code)).ToList())
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
        FlagCodes.FieldRequired, FlagCodes.FieldInvalid, FlagCodes.RuleFailed, FlagCodes.RevisionOf, FlagCodes.ChecksSkipped,
        FlagCodes.TableRowsRejected, FlagCodes.TablePageNoRows,
        FlagCodes.DefinitionScopedWildcard,
    ];

    /// <summary>The checks' flags and the rule result (null when the type lists no rules).</summary>
    /// <summary>What the checks raised, the rules' result, and the confidence threshold they applied (<see cref="ConfidenceThresholdAsync"/>).</summary>
    public sealed record CheckRun(IReadOnlyList<DocumentFlag> Flags, ValidationResult? Result, double ConfidenceThreshold = 0.8);

    /// <summary>
    /// The confidence a value needs for auto-approval (docs/11 §11 layer 3, Correction (P5) fix1): the supplier's
    /// <c>review.autoApproveMinConfidence</c> (its own, else its group's) when it sets one, else the tenant's
    /// <c>AutoApproveConfidenceThreshold</c>. A value below it raises <c>LOW_CONFIDENCE</c>, which keeps the document out
    /// of auto-approval, and it is the default of the type's <c>review.lines</c> threshold. The type's own
    /// <c>review.minConfidence</c> applies to <c>review.lines</c> only, as before.
    /// </summary>
    public static async Task<double> ConfidenceThresholdAsync(DocProcDbContext db, Document document, Tenant tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(tenant);
        if (document.SupplierId is not { } supplierId)
        {
            return tenant.Settings.AutoApproveConfidenceThreshold;
        }

        var tenantId = document.TenantId;
        var supplier = await db.Suppliers.AsNoTracking()
            .Where(s => s.Id == supplierId && s.TenantId == tenantId)
            .Select(s => new { s.SettingsJson, GroupSettings = s.Group != null ? s.Group.SettingsJson : null })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (supplier is null)
        {
            return tenant.Settings.AutoApproveConfidenceThreshold;
        }

        var settings = Application.Suppliers.SupplierSettings.Parse(supplier.SettingsJson).Over(Application.Suppliers.SupplierSettings.Parse(supplier.GroupSettings));
        return settings.Review?.AutoApproveMinConfidence is { } own and >= 0 and <= 1 ? own : tenant.Settings.AutoApproveConfidenceThreshold;
    }

    /// <summary>
    /// The checks of the validate steps of <paramref name="type"/> over the stored tree of <paramref name="document"/>:
    /// the structural check (for a type without <c>validate.schema</c>, its <c>FIELD_REQUIRED</c> part for the fields
    /// <see cref="EffectiveType.RequiredOutsideSchemaStep"/> names), the rule list (with the duplicate check of the type's key for a <c>duplicate</c> rule) and
    /// the coded duplicate check, each when the type runs it. Also what a correction re-validates inline (docs/11 §12.8).
    /// The field patterns and the <c>pattern</c> rules share one <see cref="PatternBudget"/>; values it had no room for
    /// raise one blocking <c>CHECKS_SKIPPED</c>, so the document goes to review.
    /// </summary>
    public static async Task<CheckRun> RunAsync(DocProcDbContext db, Document document, EffectiveType type, Tenant tenant, DateOnly today, DocumentValidator validator, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(type);
        var flags = new List<DocumentFlag>();
        var budget = new PatternBudget();
        var threshold = await ConfidenceThresholdAsync(db, document, tenant, cancellationToken).ConfigureAwait(false);
        var view = DocumentView.FromJson(document.Type, document.CanonicalJson);
        var compiled = type.Compiled;
        if (compiled is not null && view.Root is { } root)
        {
            view.WithValues(DocumentDataView.Values(root, compiled.Descriptors), RolePaths(type));
        }

        if (type.RunsValidateStep(ValidateStepKinds.Schema) && compiled is not null && view.Values is { } values)
        {
            flags.AddRange(StructuralValidator.Validate(values, compiled, budget));
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

            if (document.SupplierId is { } supplierId && type.Rules.Any(r => Definitions.RuleKinds.ExpressionParams.ContainsKey(r.Kind)))
            {
                data = data with { Supplier = await SupplierViewAsync(db, supplierId, cancellationToken).ConfigureAwait(false) };
            }

            if (type.Rules.Any(r => r.Kind == Domain.Validation.Rules.GenericRules.TableComplete))
            {
                data = data with { TableReports = await TableReportsAsync(db, document, cancellationToken).ConfigureAwait(false) };
            }

            if (compiled is not null && type.Rules.Any(r => r.Kind == Domain.Validation.Rules.GenericRules.DeltaToPrevious))
            {
                data = data with { Previous = await PreviousApprovedAsync(db, document, compiled, cancellationToken).ConfigureAwait(false) };
            }

            var context = new ValidationContext(
                today,
                ValidationOptions.Default with { ConfidenceThreshold = threshold },
                new Domain.Validation.TenantContext(tenant.LegalName, tenant.VatId, tenant.DefaultCurrency, tenant.NameAliases))
            { Data = data, PatternBudget = budget };
            result = validator.Validate(view, type.Rules, context);
            flags.AddRange(result.Flags);
        }

        if (type.Rules.Count > 0 && type.RunsValidateStep(ValidateStepKinds.Duplicate) && await IsDuplicateAsync(db, document, cancellationToken).ConfigureAwait(false))
        {
            flags.Add(FlagCodes.Create(FlagCodes.DuplicateSuspected, $"Another document of this supplier carries the number {document.DocumentNumber}.", "invoiceNumber", FlagParams.Of(("number", document.DocumentNumber))));
        }

        if (budget.SkippedFlag() is { } skipped)
        {
            flags.Add(skipped);
        }

        return new CheckRun(flags, result, threshold);
    }

    /// <summary>
    /// The table reports of the document (docs/11 §6.2), read for <c>tableComplete</c>: by group path, the rows read, the
    /// rejected rows per page and the pages without rows. A report that does not parse counts as no report.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, TableReportSummary>> TableReportsAsync(DocProcDbContext db, Document document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(document);
        var tenantId = document.TenantId;
        var documentId = document.Id;
        var rows = await db.DocumentTableReports.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.DocumentId == documentId)
            .Select(r => new { r.Path, r.ReportJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // A report written in this unit of work (the Extract stage's layout table, not saved yet) counts too.
        var pending = db.ChangeTracker.Entries<DocumentTableReport>()
            .Where(e => e.Entity.DocumentId == documentId && e.State is EntityState.Added or EntityState.Modified)
            .Select(e => new { e.Entity.Path, e.Entity.ReportJson });
        var deleted = db.ChangeTracker.Entries<DocumentTableReport>()
            .Where(e => e.Entity.DocumentId == documentId && e.State == EntityState.Deleted)
            .Select(e => e.Entity.Path).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, TableReportSummary>(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => !deleted.Contains(r.Path)).Concat(pending))
        {
            if (ParseReport(row.ReportJson) is { } summary)
            {
                result[row.Path] = summary;
            }
        }

        return result;
    }

    /// <summary>The summary of a stored table report (<c>LayoutExtractor.ReportJson</c>'s shape), or null.</summary>
    public static TableReportSummary? ParseReport(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonObject report)
            {
                return null;
            }

            static int Int(System.Text.Json.Nodes.JsonNode? node) => node is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
            static List<int> Ints(System.Text.Json.Nodes.JsonNode? node) => node is System.Text.Json.Nodes.JsonArray a ? [.. a.Select(Int)] : [];
            var byPage = new Dictionary<int, int>();
            if (report["perPage"] is System.Text.Json.Nodes.JsonArray pages)
            {
                foreach (var page in pages.OfType<System.Text.Json.Nodes.JsonObject>())
                {
                    var rejected = Int(page["rejected"]);
                    if (rejected > 0)
                    {
                        byPage[Int(page["page"])] = rejected;
                    }
                }
            }

            return new TableReportSummary(Int(report["rows"]), Int(report["rejectedRows"]), Ints(report["pages"]), Ints(report["pagesWithoutRows"]), byPage);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The last approved document of the same supplier and type (docs/11 §11 <c>deltaToPrevious</c>, the
    /// <c>SupplierHints.LastApproved</c> of the design): approved or auto-approved, not superseded, not this document,
    /// the latest approval first; its values view built with <paramref name="compiled"/> (the types of one key share the
    /// field tree of their version line). Null without a supplier or without such a document.
    /// </summary>
    public static async Task<PreviousDocument?> PreviousApprovedAsync(DocProcDbContext db, Document document, CompiledDefinition compiled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(compiled);
        if (document.SupplierId is not { } supplierId)
        {
            return null;
        }

        var tenantId = document.TenantId;
        var type = document.Type;
        var documentId = document.Id;
        var previous = await db.Documents.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.Id != documentId && d.SupplierId == supplierId && d.Type == type
                        && (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.AutoApproved)
                        && d.SupersededByDocumentId == null && d.CanonicalJson != null)
            .OrderByDescending(d => d.ApprovedAt)
            .ThenByDescending(d => d.Id)
            .Select(d => new { d.Id, d.ApprovedAt, d.CanonicalJson })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (previous is null)
        {
            return null;
        }

        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(previous.CanonicalJson!) is System.Text.Json.Nodes.JsonObject root
                ? new PreviousDocument(previous.Id, previous.ApprovedAt, DocumentDataView.Values(root, compiled.Descriptors))
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The matched supplier as rule expressions read it (<c>$supplier</c>: id, name, vatId, attributes).</summary>
    private static async Task<System.Text.Json.Nodes.JsonObject?> SupplierViewAsync(DocProcDbContext db, Guid supplierId, CancellationToken cancellationToken)
    {
        var supplier = await db.Suppliers.AsNoTracking().Where(s => s.Id == supplierId)
            .Select(s => new { s.Id, s.Name, s.VatId, s.AttributesJson })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (supplier is null)
        {
            return null;
        }

        System.Text.Json.Nodes.JsonNode? attributes = null;
        if (!string.IsNullOrWhiteSpace(supplier.AttributesJson))
        {
            try
            {
                attributes = System.Text.Json.Nodes.JsonNode.Parse(supplier.AttributesJson);
            }
            catch (System.Text.Json.JsonException)
            {
                attributes = null;
            }
        }

        return new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = supplier.Id.ToString(),
            ["name"] = supplier.Name,
            ["vatId"] = supplier.VatId,
            ["attributes"] = attributes as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject(),
        };
    }

    /// <summary>
    /// <paramref name="type"/> with the validate steps of the document's pipeline (docs/11 §10.1) when overrides change
    /// them (<c>configurableSteps</c>), for the inline re-validation of a correction, which checks what the Validate
    /// stage checks.
    /// </summary>
    public static async Task<EffectiveType> WithPipelineAsync(IPipelineConfigResolver? pipelines, Document document, EffectiveType type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(type);
        if (pipelines is null)
        {
            return type;
        }

        var config = await pipelines.ResolveAsync(new PipelineRequest(document.TenantId, type, document.SupplierId, document.SupplierLayoutVersionId), cancellationToken).ConfigureAwait(false);
        return config.Configurable && config.Pipeline.Overridden ? type.WithPipeline(config.Pipeline) : type;
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
    /// The review task's due time from the type's <c>review.dueBy</c> (docs/11 §5.8): the end of the day (23:59:59 in the
    /// tenant's time zone) of the date role it names (<c>validUntil</c> or <c>validFrom</c>, read from the document's
    /// columns); null without <c>dueBy</c> or without that date. There is no tenant review SLA yet, so nothing earlier
    /// replaces it.
    /// </summary>
    public static DateTimeOffset? DueAtOf(EffectiveType? type, Document document, Tenant tenant)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(tenant);
        var date = type?.Definition.Processing.Review.DueBy switch
        {
            "validUntil" => document.ValidUntil,
            "validFrom" => document.ValidFrom,
            _ => null,
        };
        if (date is not { } day)
        {
            return null;
        }

        var local = day.ToDateTime(new TimeOnly(23, 59, 59));
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone);
            return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return new DateTimeOffset(local, TimeSpan.Zero);
        }
    }

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
                 && d.Status != DocumentStatus.Rejected
                 && d.Status != DocumentStatus.Cancelled,
            cancellationToken);
    }
}
