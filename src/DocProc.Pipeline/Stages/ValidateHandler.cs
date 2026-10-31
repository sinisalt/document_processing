using DocProc.Application.Abstractions;
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
/// Stage 7 (docs/03 "Validate and enrich") and the hand-over to stage 8 or 9: runs <see cref="InvoiceValidator.Default"/>
/// with the tenant's identity and confidence threshold, adds <c>DUPLICATE_SUSPECTED</c> when the same supplier
/// already sent the same number, persists the flags and decides: no blocking flag, nothing below threshold and no
/// always-review rule means <c>AutoApproved</c> and a publish message; everything else means <c>NeedsReview</c> with
/// a review task. A document without canonical data (unsupported type) always goes to review.
/// </summary>
internal sealed class ValidateHandler(
    DocProcDbContext db,
    DocumentTimeline timeline,
    DocumentFlagWriter flags,
    IPipelineQueue queue,
    ILogger<ValidateHandler> logger) : DocumentStageHandler(db, timeline, logger)
{
    public const string ApprovedBy = DocumentTimeline.Actor;

    private static readonly string[] OwnedFlags =
    [
        FlagCodes.MissingRequired, FlagCodes.InvalidValue, FlagCodes.TotalsMismatch, FlagCodes.TaxMismatch, FlagCodes.LineArithmetic,
        FlagCodes.DateImplausible, FlagCodes.CustomerMismatch, FlagCodes.LowConfidence, FlagCodes.CurrencyInferred, FlagCodes.DuplicateSuspected,
    ];

    public override PipelineStage Stage => PipelineStage.Validate;

    protected override bool Accepts(DocumentStatus status) => status == DocumentStatus.Extracted;

    protected override async Task<StageOutcome> RunAsync(Document document, ProcessDocument message, CancellationToken cancellationToken)
    {
        var tenant = await LoadTenantAsync(document, cancellationToken).ConfigureAwait(false);
        var supplier = document.SupplierId is { } supplierId
            ? await Db.Suppliers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == supplierId, cancellationToken).ConfigureAwait(false)
            : null;

        ValidationResult? result = null;
        var raised = new List<DocumentFlag>();
        if (document.CanonicalJson is { } json && document.Type is DocumentType.Invoice or DocumentType.CreditNote)
        {
            var invoice = CanonicalDocumentSerializer.DeserializeInvoice(json);
            var context = new ValidationContext(
                Today(tenant),
                ValidationOptions.Default with { ConfidenceThreshold = tenant.Settings.AutoApproveConfidenceThreshold },
                new Domain.Validation.TenantContext(tenant.LegalName, tenant.VatId, tenant.DefaultCurrency, tenant.NameAliases));
            result = InvoiceValidator.Default.Validate(invoice, context);
            raised.AddRange(result.Flags);

            if (await IsDuplicateAsync(document, cancellationToken).ConfigureAwait(false))
            {
                raised.Add(FlagCodes.Create(FlagCodes.DuplicateSuspected, $"Another document of this supplier carries the number {document.DocumentNumber}.", "invoiceNumber", FlagParams.Of(("number", document.DocumentNumber))));
            }
        }

        flags.Replace(document, OwnedFlags, raised);
        foreach (var flag in raised)
        {
            Timeline.Record(document, DocumentEventTypes.Flagged, null, null, new { code = flag.Code, severity = flag.Severity, field = flag.Field, message = flag.Message });
        }

        var open = DocumentFlagWriter.Unresolved(document);
        var alwaysReview = supplier?.AlwaysReview == true || (document.SupplierId is { } sid && tenant.Settings.AlwaysReviewSupplierIds.Contains(sid));
        var eligible = result is { IsEligibleForAutoApproval: true }
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
            document.ApprovedAt = Timeline.Now;
            document.ApprovedBy = ApprovedBy;
            Timeline.Transition(document, DocumentStatus.AutoApproved, new { by = ApprovedBy }, DocumentEventTypes.AutoApproved);
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
            CreatedAt = Timeline.Now,
        });
        Timeline.Transition(document, DocumentStatus.NeedsReview, new { reason }, DocumentEventTypes.ReviewRequested);
        await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return StageOutcome.Stopped;
    }

    /// <summary>
    /// Same tenant, same matched supplier, same normalised number, on a document that is still alive. Unmatched
    /// documents are never compared with each other: "no supplier" is not a supplier, and two unrelated vendors can
    /// both number their first invoice 1.
    /// </summary>
    private Task<bool> IsDuplicateAsync(Document document, CancellationToken cancellationToken)
    {
        if (document.SupplierId is not { } supplierId || document.DocumentNumberNormalized is not { Length: > 0 } number)
        {
            return Task.FromResult(false);
        }

        return Db.Documents.AsNoTracking().AnyAsync(
            d => d.Id != document.Id
                 && d.SupplierId == supplierId
                 && d.DocumentNumberNormalized == number
                 && d.Status != DocumentStatus.Failed
                 && d.Status != DocumentStatus.Rejected,
            cancellationToken);
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
