using System.Text.Json;
using System.Text.Json.Serialization;
using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence.Entities;

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
    /// the decision transaction), and claims the document for it: the document's <see cref="IVersioned.Version"/> is
    /// marked modified, so the save also updates the document under its concurrency token. The row's own token
    /// (<see cref="OutboxMessage.FannedOutAt"/>) guards only an update of an existing row, and nothing in the index
    /// keeps two publishers that both found no row from both inserting one; with the claim, only the first commits and
    /// the second gets a <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>, its row unsaved.
    /// </summary>
    public static OutboxMessage AddMissing(DocProcDbContext db, Document document, DateTimeOffset at)
    {
        var row = Add(db, document, at);
        db.Entry(document).Property(d => d.Version).IsModified = true;
        return row;
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
