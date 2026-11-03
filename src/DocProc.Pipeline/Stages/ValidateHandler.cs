using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Definitions.Compilation;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Domain.Validation;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
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
/// </summary>
internal sealed class ValidateHandler(
    DocProcDbContext db,
    DocumentTimeline timeline,
    DocumentFlagWriter flags,
    IPipelineQueue queue,
    DocumentValidator validator,
    IEffectiveConfigResolver configResolver,
    ILogger<ValidateHandler> logger) : DocumentStageHandler(db, timeline, logger)
{
    public const string ApprovedBy = DocumentTimeline.Actor;

    /// <summary>The flags this stage owns (raises and replaces), also those a correction re-validates inline.</summary>
    private static string[] OwnedFlags => DocumentChecks.OwnedFlags;

    public override PipelineStage Stage => PipelineStage.Validate;

    protected override bool Accepts(DocumentStatus status) => status == DocumentStatus.Extracted;

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
        var type = resolved is { Rules.Count: > 0 } validated ? validated : null;
        if (document.DocumentTypeVersionId is null && resolved?.VersionId is { } versionId)
        {
            // Reprocessed from Validate onto the published version (configMode: current): the document pins the version
            // it is validated with (docs/11 §5.7), as Extract does, so corrections and approval keep resolving to it.
            document.DocumentTypeVersionId = versionId;
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

        var open = DocumentFlagWriter.Unresolved(document);
        var alwaysReview = supplier?.AlwaysReview == true || (document.SupplierId is { } sid && tenant.Settings.AlwaysReviewSupplierIds.Contains(sid));
        var eligible = result is { IsEligibleForAutoApproval: true }
                       && type is { AutoApprove: true }
                       && !open.Any(f => f.Severity == FlagSeverity.Blocking || f.Code == FlagCodes.LowConfidence)
                       && !alwaysReview;

        Timeline.Transition(document, DocumentStatus.Validated, new
        {
            flags = raised.Select(f => f.Code).ToList(),
            openFlags = open.Select(f => f.Code).Distinct(StringComparer.Ordinal).ToList(),
            eligibleForAutoApproval = eligible,
            alwaysReview,
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
            await queue.EnqueueAsync(new ProcessDocument(document.TenantId, document.Id, PipelineStage.Publish), cancellationToken).ConfigureAwait(false);
            return StageOutcome.Stopped;
        }

        var reason = open.Count > 0
            ? string.Join(", ", open.Select(f => f.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            : alwaysReview ? "ALWAYS_REVIEW" : "MANUAL_REVIEW";
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
