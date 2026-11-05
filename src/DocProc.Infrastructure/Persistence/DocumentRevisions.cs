using System.Text.Json;
using DocProc.Application.Abstractions;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Persistence;

/// <summary>
/// When a <c>REVISION_OF</c> resolved as <c>supersedes</c> takes effect (docs/11 §11, Correction (P5) fix1).
/// <c>:resolve-revision</c> only records the intent on the new document (<see cref="Document.RevisionResolution"/> and
/// <see cref="Document.RevisionOfDocumentId"/>); the earlier document gets <see cref="Document.SupersededByDocumentId"/>
/// when the new one is approved (by a reviewer or automatically), and loses it again when the new one leaves its
/// approval (rejected, cancelled by a join or sent back through the pipeline). So an approved list never stays
/// superseded behind a list that was rejected, and stays the reference of the duplicate check and of
/// <c>deltaToPrevious</c> until its replacement is approved. Both helpers change tracked rows; the caller's
/// <c>SaveChanges</c> commits them with the decision.
/// </summary>
public static class DocumentRevisions
{
    private static readonly JsonSerializerOptions DetailsOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// On the approval of <paramref name="document"/>: supersede the earlier document its resolution names, when that is
    /// still a document of the same tenant and type and not superseded by another. False when nothing changed.
    /// </summary>
    public static async Task<bool> ApplyOnApprovalAsync(DocProcDbContext db, Document document, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(document);
        if (document.RevisionResolution != RevisionResolution.Supersedes || document.RevisionOfDocumentId is not { } earlierId)
        {
            return false;
        }

        var tenantId = document.TenantId;
        var type = document.Type;
        var earlier = await db.Documents
            .SingleOrDefaultAsync(d => d.TenantId == tenantId && d.Id == earlierId && d.Type == type && d.SupersededByDocumentId == null, cancellationToken)
            .ConfigureAwait(false);
        if (earlier is null)
        {
            return false;
        }

        earlier.SupersededByDocumentId = document.Id;
        earlier.UpdatedAt = now;
        AddEvent(db, earlier, actor, new { resolution = "supersedes", earlierDocumentId = earlier.Id, documentId = document.Id, applied = "approved" }, now);
        return true;
    }

    /// <summary>
    /// When <paramref name="document"/> leaves its approval (rejected, cancelled, reprocessed): the documents it
    /// superseded are no longer superseded. False when it superseded none.
    /// </summary>
    public static async Task<bool> ReleaseAsync(DocProcDbContext db, Document document, string actor, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(document);
        var tenantId = document.TenantId;
        var id = document.Id;
        var superseded = await db.Documents
            .Where(d => d.TenantId == tenantId && d.SupersededByDocumentId == id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var earlier in superseded)
        {
            earlier.SupersededByDocumentId = null;
            earlier.UpdatedAt = now;
            AddEvent(db, earlier, actor, new { resolution = "supersedes", earlierDocumentId = earlier.Id, documentId = id, released = reason }, now);
        }

        return superseded.Count > 0;
    }

    private static void AddEvent(DocProcDbContext db, Document earlier, string actor, object details, DateTimeOffset now) =>
        db.DocumentEvents.Add(new DocumentEvent
        {
            TenantId = earlier.TenantId,
            DocumentId = earlier.Id,
            EventType = DocumentEventTypes.RevisionResolved,
            Actor = actor,
            DetailsJson = JsonSerializer.Serialize(details, DetailsOptions),
            At = now,
        });
}
