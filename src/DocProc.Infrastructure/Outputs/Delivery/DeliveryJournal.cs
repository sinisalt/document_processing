using System.Text.Json;
using System.Text.Json.Serialization;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Outputs.Delivery;

/// <summary>
/// What a delivery leaves on its document (docs/11 §13.6): the timeline events (<c>document.delivery_succeeded</c>,
/// <c>document.delivery_failed</c>, <c>document.delivery_skipped</c>, <c>document.redelivery_requested</c>) and the
/// <c>DELIVERY_FAILED</c> flag of a dead letter, resolved again once the document has no dead letter left. Nothing
/// here touches the document row itself: a delivery never changes the document's version or its settled time.
/// Rows are added to the context, unsaved.
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
    /// Resolves the open <c>DELIVERY_FAILED</c> flags of the delivery's document once none of its deliveries is
    /// dead-lettered any more (a redelivery cancels the dead letter it replaces).
    /// </summary>
    public static async Task ResolveFailedAsync(DocProcDbContext db, OutboxDelivery delivery, DateTimeOffset at, string resolvedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(delivery);
        var stillDead = await db.OutboxDeliveries.AnyAsync(d => d.DocumentId == delivery.DocumentId && d.Id != delivery.Id && d.Status == DeliveryStatus.DeadLettered, cancellationToken).ConfigureAwait(false);
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
