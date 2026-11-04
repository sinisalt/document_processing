using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Documents;
using DocProc.Definitions.Compilation;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Domain.Validation;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Layouts;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocProc.Pipeline.Stages;

/// <summary>
/// Stage 7 (docs/03 "Validate and enrich") and the hand-over to stage 8 or 9: runs the rule list of the type's
/// definition, resolved through <see cref="IEffectiveConfigResolver"/> (in P0 the adopted standard template, whose list
/// for invoices is the coded rules of <see cref="InvoiceValidator.Default"/> through <see cref="InvoiceRuleAdapter"/>,
/// exactly <see cref="StandardRuleSets"/>), with the tenant's identity and confidence threshold, adds
/// <c>DUPLICATE_SUSPECTED</c> when the same supplier already sent the same number, persists the flags and decides: no
/// blocking flag, nothing below threshold and no always-review rule means <c>AutoApproved</c>, with its outbox row in
/// the same save and a publish message as a nudge; everything else means <c>NeedsReview</c> with a review task. A
/// document without canonical data (unsupported type), or whose type has no rules or no auto-approval in its
/// definition, always goes to review.
/// <para>
/// Which checks run follows the definition's <c>processing.validate</c> steps (docs/11 §10.1, §11): <c>validate.schema</c>
/// (the structural check of the values view, <see cref="StructuralValidator"/>), <c>validate.rules</c> (the rule list:
/// the coded invoice kinds and the generic P1 kinds over the values view, <c>DocumentDataView</c>) and
/// <c>validate.duplicate</c> (the coded duplicate check); a definition without a validate list runs the rules and the
/// coded duplicate check, as before. A <c>duplicate</c> rule is given the result of the duplicate check of the type's
/// <c>roles.duplicateKey</c> (<see cref="DuplicateKeyCheck"/>). The review task takes its priority from
/// <c>review.priority</c>.
/// </para>
/// <para>
/// The layout conditions of the gate (docs/11 §11): an open <c>NEW_LAYOUT</c> or <c>LAYOUT_AMBIGUOUS</c> never
/// auto-approves, checked by name whatever its severity, nor does a type that is only proposed
/// (<see cref="TypeSource.Proposed"/>); for a layout-managed supplier the identified layout version must be Active and the
/// document past its review warm-up (<see cref="LayoutReviewGate"/>, <c>LAYOUT_WARMUP</c>).
/// </para>
/// </summary>
internal sealed class ValidateHandler(
    DocProcDbContext db,
    DocumentTimeline timeline,
    DocumentFlagWriter flags,
    IPipelineQueue queue,
    DocumentValidator validator,
    IEffectiveConfigResolver configResolver,
    LayoutReviewGate layoutGate,
    StagePipeline pipelines,
    ILogger<ValidateHandler> logger) : DocumentStageHandler(db, timeline, logger)
{
    public const string ApprovedBy = DocumentTimeline.Actor;

    /// <summary>The flags this stage owns (raises and replaces), also those a correction re-validates inline.</summary>
    private static string[] OwnedFlags => DocumentChecks.OwnedFlags;

    public override PipelineStage Stage => PipelineStage.Validate;

    protected override bool Accepts(DocumentStatus status) => status == DocumentStatus.Extracted;

    /// <summary>
    /// The stage runs in one transaction: a document's place in a layout version's warm-up
    /// (<see cref="LayoutReviewGate"/>) is taken by a direct update, and commits only with the <c>layout_warmup</c> event
    /// that records it, so an attempt dropped as a stale twin, or one that fails before its save, gives the place back
    /// and the first <c>ReviewWarmup</c> documents of the version all go to review (docs/11 §8.3; decision 2 of §24).
    /// </summary>
    protected override bool RunsInTransaction => true;

    protected override async Task<StageOutcome> RunAsync(Document document, ProcessDocument message, CancellationToken cancellationToken)
    {
        var tenant = await LoadTenantAsync(document, cancellationToken).ConfigureAwait(false);
        var supplier = document.SupplierId is { } supplierId
            ? await Db.Suppliers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == supplierId, cancellationToken).ConfigureAwait(false)
            : null;

        // A type whose definition lists no rules is not validated by them: nothing vouches for its data, so it goes to
        // review. The structural check runs whenever the definition lists it, its FIELD_REQUIRED part whenever the
        // definition requires a field that nothing else checks (EffectiveType.RequiredOutsideSchemaStep).
        var config = await configResolver.ResolveAsync(new EffectiveConfigRequest(document.TenantId, document.Type, document.SupplierId, document.DocumentTypeVersionId), cancellationToken).ConfigureAwait(false);
        var resolved = config.Type;
        // The validate steps of the document's pipeline (docs/11 §10.1): an override may switch validate.schema,
        // validate.rules or validate.duplicate off; the review gate below is fixed.
        var pipeline = await pipelines.ResolveAsync(document, resolved, DocProc.Definitions.Pipelines.PipelinePhases.Validate, cancellationToken).ConfigureAwait(false);
        if (resolved is not null && pipeline.Overridden)
        {
            resolved = resolved.WithPipeline(pipeline);
        }

        var type = resolved is { Rules.Count: > 0 } validated ? validated : null;
        if (document.DocumentTypeVersionId is null && resolved?.VersionId is { } versionId)
        {
            // Reprocessed from Validate onto the published version (configMode: current): the document pins the version
            // it is validated with (docs/11 §5.7), as Extract does, so corrections and approval keep resolving to it.
            document.DocumentTypeVersionId = versionId;
        }

        if (resolved is not null)
        {
            await CompleteStoredTreeAsync(document, resolved, cancellationToken).ConfigureAwait(false);
        }

        ValidationResult? result = null;
        var raised = new List<DocumentFlag>();
        if (document.CanonicalJson is { } json && resolved is not null && (type is not null || resolved.RunsValidateStep(ValidateStepKinds.Schema) || resolved.RequiredOutsideSchemaStep.Count > 0))
        {
            var run = await DocumentChecks.RunAsync(Db, document, resolved, tenant, Today(tenant), validator, cancellationToken).ConfigureAwait(false);
            result = type is null ? null : run.Result;
            raised.AddRange(run.Flags);
        }

        flags.Replace(document, OwnedFlags, raised);
        foreach (var flag in raised)
        {
            Timeline.Record(document, DocumentEventTypes.Flagged, null, null, new { code = flag.Code, severity = flag.Severity, field = flag.Field, message = flag.Message });
        }

        var layout = await layoutGate.EvaluateAsync(document, cancellationToken).ConfigureAwait(false);
        flags.Replace(document, [FlagCodes.LayoutWarmup], layout.Warmup is { } warmup ? [warmup] : []);

        var open = DocumentFlagWriter.Unresolved(document);
        var alwaysReview = supplier?.AlwaysReview == true
                           || (document.SupplierId is { } sid && tenant.Settings.AlwaysReviewSupplierIds.Contains(sid))
                           || await SupplierReviewsAlwaysAsync(supplier, cancellationToken).ConfigureAwait(false);

        // SUPPLIER_SOURCE_UNVERIFIED is a warning that never auto-approves (docs/11 §7.2): a rule for another supplier
        // would have routed the document had its sender been authenticated. SUPPLIER_AMBIGUOUS is a warning too, and
        // also never auto-approves (docs/11 §11 layer 3): source rules of two suppliers tied, so the supplier content
        // then names is a guess a person confirms.
        // NEW_LAYOUT and LAYOUT_AMBIGUOUS block by name (docs/11 §11), so a severity override cannot let them through.
        var eligible = result is { IsEligibleForAutoApproval: true }
                       && type is { AutoApprove: true }
                       && !open.Any(f => f.Severity == FlagSeverity.Blocking || f.Code is FlagCodes.LowConfidence or FlagCodes.SupplierSourceUnverified or FlagCodes.SupplierAmbiguous or FlagCodes.NewLayout or FlagCodes.LayoutAmbiguous)
                       && document.TypeSource != TypeSource.Proposed
                       && layout.Passes
                       && !alwaysReview;

        Timeline.Transition(document, DocumentStatus.Validated, new
        {
            flags = raised.Select(f => f.Code).ToList(),
            openFlags = open.Select(f => f.Code).Distinct(StringComparer.Ordinal).ToList(),
            eligibleForAutoApproval = eligible,
            alwaysReview,
            layout = layout.Passes ? null : layout.Reason,
            threshold = tenant.Settings.AutoApproveConfidenceThreshold,
        });

        if (eligible)
        {
            // One reading of the clock for the decision and its row: a row belongs to the settlement it was written at
            // or after (DocumentOutbox.IsOfSettlement), and a wall clock stepped back between two readings would leave
            // the decision's own row outside it.
            var now = Timeline.Now;
            document.ApprovedAt = now;
            document.ApprovedBy = ApprovedBy;
            Timeline.Transition(document, DocumentStatus.AutoApproved, new { by = ApprovedBy }, DocumentEventTypes.AutoApproved, now);
            // The outbox row commits with the decision (docs/11 §13.5); the publish message below is only a nudge,
            // and the publish sweeper finds the row if the message is lost.
            DocumentOutbox.Add(Db, document, now);
            await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var publish = new ProcessDocument(document.TenantId, document.Id, PipelineStage.Publish);
            AfterCommit(async ct => await queue.EnqueueAsync(publish, ct).ConfigureAwait(false));
            return StageOutcome.Stopped;
        }

        var reason = open.Count > 0
            ? string.Join(", ", open.Select(f => f.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            : alwaysReview ? "ALWAYS_REVIEW" : !layout.Passes ? layout.Reason! : "MANUAL_REVIEW";
        Db.ReviewTasks.Add(new ReviewTask
        {
            Id = Guid.CreateVersion7(),
            TenantId = document.TenantId,
            DocumentId = document.Id,
            Reason = reason.Length <= 1000 ? reason : reason[..1000],
            Status = ReviewTaskStatus.Open,
            Priority = DocumentChecks.PriorityOf(resolved),
            CreatedAt = Timeline.Now,
        });
        Timeline.Transition(document, DocumentStatus.NeedsReview, new { reason }, DocumentEventTypes.ReviewRequested);
        await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return StageOutcome.Stopped;
    }

    /// <summary>
    /// Completes a stored definition tree (<c>CanonicalSchemaVersion</c> 2) with the fields of the version it is validated
    /// with, before the checks read it: a document reprocessed from Validate onto a newer version (<c>configMode:
    /// current</c>, docs/11 §5.7) gains the fields that version added and keeps the value of a renamed field
    /// (<see cref="Definitions.Compilation.FieldDescriptor.FormerKeys"/>) under its new key, as the Extract stage does.
    /// When anything changed the moved tree is stored, with the role columns and the field index rewritten from it, so
    /// the checks, the review form, the values view and the output all read the same tree. A tree that is already
    /// complete (every document on the version it was extracted with) is left exactly as it is.
    /// </summary>
    private async Task CompleteStoredTreeAsync(Document document, EffectiveType type, CancellationToken cancellationToken)
    {
        if (document.CanonicalSchemaVersion != DefinitionTree.SchemaVersion || type.Compiled is not { } compiled || string.IsNullOrWhiteSpace(document.CanonicalJson))
        {
            return;
        }

        System.Text.Json.Nodes.JsonObject? tree;
        try
        {
            tree = System.Text.Json.Nodes.JsonNode.Parse(document.CanonicalJson) as System.Text.Json.Nodes.JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return; // The checks report what they can read of it; the tree is not rewritten.
        }

        if (tree is null || !DefinitionTree.Complete(tree, compiled.Descriptors))
        {
            return;
        }

        document.CanonicalJson = tree.ToJsonString(Domain.Documents.CanonicalJson.Options);
        DocumentRoleColumns.Refresh(document, type.Roles);
        await DocumentFieldIndexWriter.RefreshAsync(Db, document, type.IndexedFields, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The supplier's <c>review.mode: always</c> (docs/11 §6.4), its own or its group's.</summary>
    private async Task<bool> SupplierReviewsAlwaysAsync(Supplier? supplier, CancellationToken cancellationToken)
    {
        if (supplier is null)
        {
            return false;
        }

        var settings = DocProc.Application.Suppliers.SupplierSettings.Parse(supplier.SettingsJson);
        if (supplier.GroupId is { } groupId)
        {
            var group = await Db.SupplierGroups.AsNoTracking().Where(g => g.Id == groupId).Select(g => g.SettingsJson).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            settings = settings.Over(DocProc.Application.Suppliers.SupplierSettings.Parse(group));
        }

        return settings.AlwaysReview;
    }

    /// <summary>Today in the tenant's time zone, so an invoice dated tomorrow in Berlin is not "in the future" at 23:30 UTC.</summary>
    private DateOnly Today(Tenant tenant)
    {
        var now = Timeline.Now;
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
}
