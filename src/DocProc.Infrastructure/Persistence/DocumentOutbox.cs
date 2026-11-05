using System.Text.Json;
using System.Text.Json.Serialization;
using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Persistence;

/// <summary>
/// The outbox rules every writer shares (docs/11 §13.5): which event a settled state publishes, which row belongs to
/// the current settlement, and the row itself. Approve, reject and auto-approval add the row in the transaction that
/// settles the document; the publish stage hands it on (<see cref="OutboxMessage.FannedOutAt"/>), and the publish
/// sweeper finds settlements whose row was never handed on.
/// </summary>
public static class DocumentOutbox
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The settled states: the ones with an outbox event.</summary>
    public static readonly IReadOnlyList<DocumentStatus> SettledStatuses = [DocumentStatus.Approved, DocumentStatus.AutoApproved, DocumentStatus.Rejected];

    /// <summary>The outbox event a settled state publishes.</summary>
    public static string EventTypeOf(DocumentStatus status) =>
        status == DocumentStatus.Rejected ? DocumentEventTypes.Rejected : DocumentEventTypes.Approved;

    /// <summary>When the current settlement happened: the approval time, else the last change (a rejection).</summary>
    public static DateTimeOffset SettledAt(DateTimeOffset? approvedAt, DateTimeOffset updatedAt) => approvedAt ?? updatedAt;

    /// <summary>
    /// Whether a row belongs to the current settlement: its event type, written at or after it. A row of an earlier
    /// settlement (rejected, reprocessed, settled again) does not.
    /// </summary>
    public static bool IsOfSettlement(string rowEventType, DateTimeOffset rowCreatedAt, DocumentStatus status, DateTimeOffset? approvedAt, DateTimeOffset updatedAt) =>
        string.Equals(rowEventType, EventTypeOf(status), StringComparison.Ordinal) && rowCreatedAt >= SettledAt(approvedAt, updatedAt);

    /// <summary>
    /// Adds the outbox row of the document's current settlement to <paramref name="db"/>, unsaved, so it commits with
    /// whatever settles the document. The document must already be in its settled state.
    /// </summary>
    public static OutboxMessage Add(DocProcDbContext db, Document document, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(document);
        if (!DocumentStateMachine.IsSettled(document.Status))
        {
            throw new InvalidOperationException($"Document {document.Id} is {document.Status}; only a settled document has an outbox event.");
        }

        var eventType = EventTypeOf(document.Status);
        var row = new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            TenantId = document.TenantId,
            DocumentId = document.Id,
            EventType = eventType,
            PayloadJson = JsonSerializer.Serialize(new DocumentEventMessage(document.TenantId, document.Id, eventType, at), PayloadOptions),
            CreatedAt = at,
        };
        db.OutboxMessages.Add(row);
        return row;
    }

    /// <summary>
    /// Adds the row of a settlement whose decision did not write one (a decision from before the outbox was written in
    /// the decision transaction), after <see cref="ClaimMissingAsync"/> claimed the document for it. The row's own token
    /// (<see cref="OutboxMessage.FannedOutAt"/>) guards only an update of an existing row, and nothing in the index
    /// keeps two publishers that both found no row from both inserting one; with the claim, only the first goes on.
    /// </summary>
    /// <remarks>
    /// Correction (P5): the claim used to mark the version modified for the save that also inserts the deliveries, so the
    /// deliveries carried the version the document had before that save and the document ended one past them: a
    /// <c>rerender</c> of such a delivery was refused as "Document changed" (docs/11 §13.6). The claim is now a
    /// conditional update of the version made first (<see cref="ClaimMissingAsync"/>), and the row, the deliveries and
    /// the values view they render all see the claimed version.
    /// </remarks>
    public static OutboxMessage AddMissing(DocProcDbContext db, Document document, DateTimeOffset at) => Add(db, document, at);

    /// <summary>
    /// Claims the document for a settlement whose decision wrote no outbox row: bumps its <see cref="IVersioned.Version"/>
    /// in the database only if it is still the version loaded (one statement, first claim wins), and moves the tracked
    /// document to the new version as its original and current value, so the later save checks against it and does not
    /// bump it again. False when another writer changed the document first (a second publisher that also found no row):
    /// that publisher owns the settlement. Call it before anything reads the version (the fan-out, the values view).
    /// </summary>
    public static async Task<bool> ClaimMissingAsync(DocProcDbContext db, Document document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(document);
        var loaded = document.Version;
        var id = document.Id;
        var claimed = await db.Documents
            .Where(d => d.Id == id && d.Version == loaded)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Version, loaded + 1), cancellationToken).ConfigureAwait(false);
        if (claimed == 0)
        {
            return false;
        }

        // The tracked document takes the claimed version as its loaded one, unmodified: the later save neither bumps it
        // again nor checks the old token (setting the current value alone would mark it modified and bump it twice).
        var version = db.Entry(document).Property(d => d.Version);
        version.CurrentValue = loaded + 1;
        version.OriginalValue = loaded + 1;
        version.IsModified = false;
        return true;
    }

    /// <summary>
    /// Settled documents whose current settlement has no handed-on outbox row: the row is missing (an auto-approval or
    /// decision from before the outbox was written in the decision transaction) or was never handed on (the publish
    /// message was lost). Only settlements between <paramref name="settledSince"/> and <paramref name="settledBefore"/>
    /// count, oldest first. The same rule as <see cref="IsOfSettlement"/>, in SQL.
    /// </summary>
    public static IQueryable<UnpublishedSettlement> Unpublished(DocProcDbContext db, DateTimeOffset settledBefore, DateTimeOffset settledSince)
    {
        ArgumentNullException.ThrowIfNull(db);
        var settled = SettledStatuses.ToList();
        return db.Documents.AsQueryable()
            .Where(d => settled.Contains(d.Status))
            .Select(d => new { d.TenantId, d.Id, d.Status, SettledAt = d.ApprovedAt ?? d.UpdatedAt })
            .Where(d => d.SettledAt <= settledBefore && d.SettledAt >= settledSince)
            .Where(d => !db.OutboxMessages.Any(m =>
                m.DocumentId == d.Id
                && m.FannedOutAt != null
                && m.CreatedAt >= d.SettledAt
                && m.EventType == (d.Status == DocumentStatus.Rejected ? DocumentEventTypes.Rejected : DocumentEventTypes.Approved)))
            .OrderBy(d => d.SettledAt)
            .ThenBy(d => d.Id)
            .Select(d => new UnpublishedSettlement(d.TenantId, d.Id, d.Status, d.SettledAt));
    }
}

/// <summary>A settled document whose outbox row was not handed on, as <see cref="DocumentOutbox.Unpublished"/> finds it.</summary>
public sealed record UnpublishedSettlement(Guid TenantId, Guid DocumentId, DocumentStatus Status, DateTimeOffset SettledAt);
