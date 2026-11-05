using System.Text.Json;
using System.Text.Json.Serialization;
using DocProc.Application.Abstractions;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Outputs.Delivery;

/// <summary>
/// What a delivery leaves on its document (docs/11 §13.6): the timeline events (<c>document.delivery_succeeded</c>,
/// <c>document.delivery_failed</c>, <c>document.delivery_skipped</c>, <c>document.redelivery_requested</c>) and the
/// <c>DELIVERY_FAILED</c> flag of a dead letter, resolved again once the document has no dead letter left (a newer
/// revision delivered for the same binding and destination supersedes an older dead letter). Nothing here touches the
/// document row itself: a delivery never changes the document's version or its settled time. Rows are added to the
/// context, unsaved.
/// </summary>
public static class DeliveryJournal
{
    public const string Actor = "delivery";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Record(DocProcDbContext db, OutboxDelivery delivery, string eventType, object details, DateTimeOffset at, string actor = Actor)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(delivery);
        db.DocumentEvents.Add(new DocumentEvent
        {
            TenantId = delivery.TenantId,
            DocumentId = delivery.DocumentId,
            EventType = eventType,
            Actor = actor,
            DetailsJson = JsonSerializer.Serialize(details, Json),
            At = at,
        });
    }

    /// <summary>Raises <c>DELIVERY_FAILED</c> on the delivery's document unless it already has an open one.</summary>
    public static async Task RaiseFailedAsync(DocProcDbContext db, OutboxDelivery delivery, string destination, string error, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(delivery);
        var open = db.DocumentFlags.Local.Any(f => f.DocumentId == delivery.DocumentId && f.Code == FlagCodes.DeliveryFailed && f.ResolvedAt == null)
            || await db.DocumentFlags.AnyAsync(f => f.DocumentId == delivery.DocumentId && f.Code == FlagCodes.DeliveryFailed && f.ResolvedAt == null, cancellationToken).ConfigureAwait(false);
        if (open)
        {
            return;
        }

        var flag = FlagCodes.Create(
            FlagCodes.DeliveryFailed,
            $"Delivery to {destination} failed: {error}",
            parameters: FlagParams.Of(("destination", destination), ("deliveryId", delivery.Id.ToString("D")), ("error", error)));
        db.DocumentFlags.Add(DocumentFlagRecord.FromDomain(delivery.TenantId, delivery.DocumentId, flag, at));
    }

    /// <summary>
    /// After a delivery of the document reached its receiver: cancels the dead letters it supersedes (older revisions
    /// of the document for the same binding and destination, which the receiver now has a newer one of), then resolves
    /// the open <c>DELIVERY_FAILED</c> flags once none of the document's deliveries is dead-lettered any more (a
    /// redelivery cancels the dead letter it replaces). The deliveries delivered in this unit of work (an
    /// acknowledgement of several feed entries) all count.
    /// </summary>
    public static async Task ResolveFailedAsync(DocProcDbContext db, OutboxDelivery delivery, DateTimeOffset at, string resolvedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(delivery);

        // Tracked rows come back with their unsaved changes (this delivery, and the others delivered beside it).
        var deliveries = await db.OutboxDeliveries.Where(d => d.DocumentId == delivery.DocumentId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var delivered = deliveries.Where(d => d.Status == DeliveryStatus.Delivered).ToList();
        var stillDead = false;
        foreach (var dead in deliveries.Where(d => d.Status == DeliveryStatus.DeadLettered && d.Id != delivery.Id))
        {
            var newer = delivered
                .Where(d => d.BindingId == dead.BindingId && d.DestinationId == dead.DestinationId && d.DocumentVersion > dead.DocumentVersion)
                .OrderByDescending(d => d.DocumentVersion)
                .FirstOrDefault();
            if (newer is null)
            {
                stillDead = true;
                continue;
            }

            dead.Status = DeliveryStatus.Cancelled;
            dead.NextAttemptAt = null;
            dead.LastError = $"Superseded by revision {newer.DocumentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)}, which was delivered.";
            Record(db, dead, DocumentEventTypes.DeliverySkipped, new { deliveryId = dead.Id, revision = dead.DocumentVersion, supersededBy = newer.Id, reason = dead.LastError }, at, resolvedBy);
        }

        if (stillDead)
        {
            return;
        }

        var flags = await db.DocumentFlags.Where(f => f.DocumentId == delivery.DocumentId && f.Code == FlagCodes.DeliveryFailed && f.ResolvedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var flag in flags)
        {
            flag.ResolvedAt = at;
            flag.ResolvedBy = resolvedBy;
        }
    }
}
