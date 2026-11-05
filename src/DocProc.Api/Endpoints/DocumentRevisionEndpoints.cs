using System.Security.Claims;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Api.Endpoints;

/// <summary>
/// <c>POST /documents/{id}:resolve-revision {resolution}</c> (docs/11 §11, §18): a reviewer decides what a document
/// with an open <c>REVISION_OF</c> is to the earlier document with the same duplicate key. <c>supersedes</c> gives the
/// earlier document <see cref="Document.SupersededByDocumentId"/> and this one <c>meta.supersedes</c>; <c>additional</c>
/// lets both stand and keeps the pair from being flagged again; <c>duplicate</c> rejects this document. The flag is
/// resolved by the reviewer; supersedes and additional leave the document in review for its approval.
/// </summary>
public static partial class DocumentEndpoints
{
    /// <summary>The <c>resolution</c> values of <c>:resolve-revision</c>.</summary>
    public static class RevisionResolutions
    {
        public const string Supersedes = "supersedes";
        public const string Additional = "additional";
        public const string Duplicate = "duplicate";
    }

    private static void MapRevisionActions(RouteGroupBuilder documents)
    {
        documents.MapPost("/{id:guid}:resolve-revision", ResolveRevisionAsync)
            .RequireAuthorization(Policies.Review)
            .WithName("ResolveDocumentRevision")
            .WithSummary("Resolves an open REVISION_OF (docs/11 §11): supersedes (the earlier document is superseded and this one carries meta.supersedes), additional (both stand; the pair is not flagged again) or duplicate (this document is rejected).");
    }

    private static async Task<Results<Ok<DocumentDetail>, ValidationProblem, ProblemHttpResult>> ResolveRevisionAsync(
        Guid id,
        ResolveRevisionRequest request,
        [FromServices] DocProcDbContext db,
        IPipelineQueue queue,
        TimeProvider clock,
        ClaimsPrincipal user,
        [FromServices] IEffectiveConfigResolver configs,
        [FromServices] ITenantFeatures features,
        CancellationToken ct)
    {
        var resolution = request?.Resolution?.Trim() switch
        {
            RevisionResolutions.Supersedes => RevisionResolution.Supersedes,
            RevisionResolutions.Additional => RevisionResolution.Additional,
            RevisionResolutions.Duplicate => RevisionResolution.Duplicate,
            _ => (RevisionResolution?)null,
        };
        if (resolution is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["resolution"] = [$"Must be {RevisionResolutions.Supersedes}, {RevisionResolutions.Additional} or {RevisionResolutions.Duplicate}."],
            });
        }

        var note = Clean(request!.Note, 2000);
        var document = await db.Documents.Include(d => d.Flags).SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
        {
            return NotFound(id);
        }

        if (document.Status is not (DocumentStatus.NeedsReview or DocumentStatus.Validated))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Not in review", detail: $"Document {id} is {document.Status}; a revision is resolved while the document waits for review.");
        }

        var flag = document.Flags.FirstOrDefault(f => f.Code == FlagCodes.RevisionOf && f.ResolvedAt is null);
        if (flag is null || flag.Params is null || !flag.Params.TryGetValue("documentId", out var earlierText) || !Guid.TryParse(earlierText, out var earlierId))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "No open revision", detail: $"Document {id} has no open REVISION_OF to resolve.");
        }

        var tenantId = document.TenantId;
        var earlier = await db.Documents.SingleOrDefaultAsync(d => d.Id == earlierId && d.TenantId == tenantId, ct);
        if (earlier is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Earlier document not found", detail: $"The earlier document {earlierId} the flag names is not visible; revalidate the document.");
        }

        if (resolution == RevisionResolution.Supersedes && earlier.SupersededByDocumentId is { } other && other != document.Id)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Already superseded", detail: $"Document {earlierId} is already superseded by {other}.");
        }

        var now = clock.GetUtcNow();
        var actor = DocumentQueries.ActorName(user);
        document.RevisionOfDocumentId = earlier.Id;
        document.RevisionResolution = resolution;
        document.UpdatedAt = now;
        flag.ResolvedAt = now;
        flag.ResolvedBy = actor;
        var details = new { resolution = request.Resolution!.Trim(), earlierDocumentId = earlier.Id, documentId = document.Id, note };
        DocumentQueries.AddEvent(db, document, DocumentEventTypes.RevisionResolved, null, actor, details, now);
        if (resolution == RevisionResolution.Supersedes)
        {
            earlier.SupersededByDocumentId = document.Id;
            earlier.UpdatedAt = now;
            DocumentQueries.AddEvent(db, earlier, DocumentEventTypes.RevisionResolved, null, actor, details, now);
        }

        var rejected = false;
        if (resolution == RevisionResolution.Duplicate)
        {
            if (!DocumentStateMachine.CanTransition(document.Status, DocumentStatus.Rejected))
            {
                return InvalidTransition(document, DocumentStatus.Rejected);
            }

            var change = new DocumentStatusChange(document.Status, DocumentStatus.Rejected);
            document.Status = DocumentStatus.Rejected;
            await CloseOpenTasksAsync(db, document.Id, ReviewTaskStatus.Done, now, ct);
            DocumentQueries.AddEvent(db, document, DocumentEventTypes.Rejected, change, actor, new { reason = note ?? $"Duplicate of document {earlier.Id}." }, now);
            DocumentOutbox.Add(db, document, now);
            rejected = true;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrentChange(id);
        }

        if (rejected)
        {
            await queue.EnqueueAsync(new ProcessDocument(document.TenantId, document.Id, PipelineStage.Publish), ct);
        }

        return TypedResults.Ok(await FreshDetailAsync(db, id, configs, features, ct));
    }
}
