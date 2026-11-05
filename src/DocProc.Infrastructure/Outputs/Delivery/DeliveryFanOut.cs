using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Outputs;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Outputs.Delivery;

/// <summary>The renderer could not be reached; the caller retries the whole step (nothing was saved).</summary>
public sealed class RendererUnavailableException(string message) : Exception(message);

/// <summary>What one binding did in a fan-out, for the publish stage's timeline entry.</summary>
public sealed record FanOutEntry(Guid BindingId, string Binding, string Outcome, Guid? DeliveryId = null, string? Destination = null, string? Reason = null);

/// <summary>The fan-out of one settlement: the deliveries to nudge (Pending), and what every binding did.</summary>
public sealed record FanOutResult(IReadOnlyList<OutboxDelivery> ToSend, IReadOnlyList<FanOutEntry> Entries)
{
    public static FanOutResult None { get; } = new([], []);
}

/// <summary>
/// The fan-out of docs/11 §13.5, run by the publish stage for a tenant with the <c>outputs</c> switch: the bindings
/// that match the settlement's event (<c>document.approved</c> for approvals and auto-approvals,
/// <c>document.rejected</c>) and the document's scope (type, supplier, channel; group and receiving entity bindings
/// match nothing until P4), whose <c>Condition</c> holds; within an exclusive group only the most specific (supplier,
/// group, channel, entity, type, tenant, then the higher <c>Priority</c>, then the name). For each it renders the
/// payload now, from the mapping version the binding follows (pinned, else published) and the document's values view
/// for the event, checks it against the target schema, stores the snapshot in <c>BlobContainers.Exports</c> and adds
/// an <see cref="OutboxDelivery"/> (key <c>{documentId}:{version}:{bindingId}</c>), unsaved, so it commits in the
/// publish stage's save together with <c>OutboxMessage.FannedOutAt</c>.
/// <list type="bullet">
/// <item><description>A permanent render failure (<c>$required</c>, a schema violation, an empty output) adds the
/// delivery dead-lettered, with its attempt, <c>DELIVERY_FAILED</c> and a timeline event.</description></item>
/// <item><description>A binding whose condition is false, or whose re-approval it skips, adds a <c>Skipped</c> delivery
/// with the reason, so the document's deliveries list it as not applicable.</description></item>
/// <item><description>Re-approval (an earlier version of the document was delivered for the binding) follows
/// <see cref="OutputBinding.OnReapproval"/>: <c>Resend</c> renders as usual (new revision, new key); <c>Skip</c> adds
/// it skipped; <c>Update</c> renders with <see cref="OutputBinding.UpdateMappingId"/> (else the binding's mapping)
/// and <c>meta.event = document.updated</c>, which an http destination sends with its update method and URL. Without
/// an earlier delivery that reached the receiver, <c>Update</c> sends a plain approval. A redelivery stands for the
/// delivery it replaced (whose row is <c>Cancelled</c>); a dead letter was not sent and will not be.</description></item>
/// <item><description>Older revisions still waiting (Pending, Failed) for the same binding and destination are set
/// to <c>Skipped</c>: the receiver gets the newest (docs/11 §13.6 "Ordering").</description></item>
/// <item><description>The renderer being unavailable throws <see cref="RendererUnavailableException"/>: nothing is
/// saved and the publish stage is retried, by the runner and then the publish sweeper.</description></item>
/// </list>
/// </summary>
public sealed class DeliveryFanOut(
    DocProcDbContext db,
    ITenantFeatures features,
    IDocumentDataViewBuilder views,
    OutputRenderService render,
    IContainerBlobStore blobs,
    TimeProvider clock)
{
    public async Task<FanOutResult> FanOutAsync(Document document, OutboxMessage outbox, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(outbox);
        if (!await features.IsEnabledAsync(document.TenantId, TenantFeature.Outputs, cancellationToken).ConfigureAwait(false))
        {
            return FanOutResult.None;
        }

        var eventType = outbox.EventType;
        var bindings = (await db.OutputBindings
                .Include(b => b.Destination)
                .Where(b => b.Enabled && b.Mode == OutputBindingMode.PerDocument)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(b => b.Events.Contains(eventType, StringComparer.Ordinal))
            .OrderBy(b => b.Name, StringComparer.Ordinal)
            .ToList();
        if (bindings.Count == 0)
        {
            return FanOutResult.None;
        }

        var now = clock.GetUtcNow();
        var channelId = document.IngestionItem?.Ingestion?.ChannelId
            ?? await db.IngestionItems.Where(i => i.Id == document.IngestionItemId).Select(i => i.Ingestion!.ChannelId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<FanOutEntry>();
        var viewCache = new Dictionary<string, DocumentDataViewResult>(StringComparer.Ordinal);

        async Task<DocumentDataViewResult> ViewAsync(string forEvent, bool fields)
        {
            var key = forEvent + (fields ? "+fields" : string.Empty);
            if (!viewCache.TryGetValue(key, out var view))
            {
                view = await views.BuildAsync(new DataViewRequest(document.Id) { Event = forEvent, EventAt = outbox.CreatedAt, IncludeFields = fields }, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Document {document.Id} is not visible to its own tenant scope.");
                viewCache[key] = view;
            }

            return view;
        }

        // Scope, then condition: a binding whose condition is false gets a Skipped delivery ("not applicable").
        var applicable = new List<OutputBinding>();
        var conditionFalse = new HashSet<Guid>();
        foreach (var binding in bindings)
        {
            var mismatch = ScopeMismatch(binding, document, channelId);
            if (mismatch is not null)
            {
                continue; // out of scope: not this document's binding at all
            }

            if (binding.Destination is not { Status: OutputDestinationStatus.Active } destination || !OutputTransports.Available.Contains(destination.Transport, StringComparer.Ordinal))
            {
                entries.Add(new FanOutEntry(binding.Id, binding.Name, "notApplicable", Reason: "The destination is disabled."));
                continue;
            }

            if (binding.Condition is { Length: > 0 } condition)
            {
                var view = await ViewAsync(eventType, OutputRenderService.UsesField(condition)).ConfigureAwait(false);
                var result = await render.RenderAsync(OutputEngines.Jsonata, condition, null, view.View, view.Fields, view.Today, cancellationToken).ConfigureAwait(false);
                if (result.Error is { Kind: RenderErrorKinds.Unavailable } unavailable)
                {
                    throw new RendererUnavailableException(unavailable.Message);
                }

                if (result.Error is { } error)
                {
                    entries.Add(new FanOutEntry(binding.Id, binding.Name, "notApplicable", Reason: RenderLimits.Shorten($"The condition failed ({error.Kind}): {error.Message}")));
                    continue;
                }

                if (result.Output is not JsonValue value || value.GetValueKind() != JsonValueKind.True)
                {
                    conditionFalse.Add(binding.Id);
                    applicable.Add(binding);
                    continue;
                }
            }

            applicable.Add(binding);
        }

        var winners = Exclusive(applicable.Where(b => !conditionFalse.Contains(b.Id)).ToList(), entries);
        var existing = await db.OutboxDeliveries.Where(d => d.DocumentId == document.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var toSend = new List<OutboxDelivery>();
        foreach (var binding in applicable.Where(b => conditionFalse.Contains(b.Id) || winners.Contains(b)))
        {
            var key = IdempotencyKey(document.Id, document.Version, binding.Id);
            if (existing.Any(d => d.IdempotencyKey == key))
            {
                entries.Add(new FanOutEntry(binding.Id, binding.Name, "exists", Reason: "This revision was fanned out for the binding already."));
                continue;
            }

            var destination = binding.Destination!;
            // The re-approval mode counts redeliveries (one stands for the delivery it replaced); superseding does not
            // touch them: an operator's redelivery is never skipped.
            var earlier = existing.Where(d => d.BindingId == binding.Id && d.DocumentVersion < document.Version).ToList();
            var mode = ReapprovalMode(binding, eventType, earlier);
            var mappingId = mode == OnReapproval.Update ? binding.UpdateMappingId ?? binding.MappingId : binding.MappingId;
            var version = await MappingVersionAsync(mappingId, mappingId == binding.MappingId ? binding.PinnedMappingVersion : null, cancellationToken).ConfigureAwait(false);
            if (version is null)
            {
                entries.Add(new FanOutEntry(binding.Id, binding.Name, "notApplicable", Reason: "The binding's mapping has no published version."));
                continue;
            }

            var deliveryEvent = mode == OnReapproval.Update ? OutputEvents.Updated : eventType;
            var delivery = new OutboxDelivery
            {
                Id = Guid.CreateVersion7(),
                TenantId = document.TenantId,
                OutboxMessageId = outbox.Id,
                DocumentId = document.Id,
                DocumentVersion = document.Version,
                BindingId = binding.Id,
                DestinationId = destination.Id,
                MappingVersionId = version.Id,
                IdempotencyKey = key,
                Event = deliveryEvent,
                ContentType = version.ContentType,
                CreatedAt = now,
            };
            db.OutboxDeliveries.Add(delivery);

            if (conditionFalse.Contains(binding.Id))
            {
                Skip(delivery, "notApplicable: the binding's condition is false for this document.", binding, destination, now);
                entries.Add(new FanOutEntry(binding.Id, binding.Name, "notApplicable", delivery.Id, destination.Name, "The condition is false."));
                continue;
            }

            if (mode == OnReapproval.Skip)
            {
                Skip(delivery, "onReapproval: Skip (an earlier revision was delivered or is being sent).", binding, destination, now);
                entries.Add(new FanOutEntry(binding.Id, binding.Name, "skipped", delivery.Id, destination.Name, "OnReapproval Skip."));
                continue;
            }

            // The newest revision goes; older ones still waiting for this binding and destination do not.
            foreach (var older in earlier.Where(d => d.RedeliveryOfId == null && d.DestinationId == destination.Id && d.Status is DeliveryStatus.Pending or DeliveryStatus.Failed))
            {
                older.Status = DeliveryStatus.Skipped;
                older.NextAttemptAt = null;
                older.LastError = $"Superseded by revision {document.Version.ToString(CultureInfo.InvariantCulture)} before it was delivered.";
                DeliveryJournal.Record(db, older, DocumentEventTypes.DeliverySkipped, new { deliveryId = older.Id, binding = binding.Name, destination = destination.Name, revision = older.DocumentVersion, reason = older.LastError }, now);
            }

            var outcome = await RenderAsync(delivery, version, deliveryEvent, ViewAsync, cancellationToken).ConfigureAwait(false);
            if (outcome is { } failure)
            {
                await DeadLetterAsync(delivery, failure, binding, destination, now, cancellationToken).ConfigureAwait(false);
                entries.Add(new FanOutEntry(binding.Id, binding.Name, "failed", delivery.Id, destination.Name, failure));
                continue;
            }

            delivery.Status = DeliveryStatus.Pending;
            delivery.NextAttemptAt = now;
            toSend.Add(delivery);
            entries.Add(new FanOutEntry(binding.Id, binding.Name, mode == OnReapproval.Update ? "update" : "queued", delivery.Id, destination.Name));
        }

        return new FanOutResult(toSend, entries);
    }

    /// <summary>The idempotency key of a fan-out's delivery (docs/11 §6.7).</summary>
    public static string IdempotencyKey(Guid documentId, int version, Guid bindingId) =>
        $"{documentId:D}:{version.ToString(CultureInfo.InvariantCulture)}:{bindingId:D}";

    /// <summary>
    /// Renders the payload of <paramref name="delivery"/> with <paramref name="version"/> and stores the snapshot;
    /// returns the permanent failure (at most <see cref="RenderLimits.MaxErrorMessageLength"/> characters), or null when
    /// the delivery has its payload. Throws when the renderer is unavailable (it cannot be started or take the request).
    /// </summary>
    public async Task<string?> RenderAsync(OutboxDelivery delivery, OutputMappingVersion version, string deliveryEvent, Func<string, bool, Task<DocumentDataViewResult>> view, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(view);
        var values = await view(deliveryEvent, OutputRenderService.UsesField(version.Expression)).ConfigureAwait(false);
        JsonObject? schema = null;
        if (version.TargetSchemaJson is { Length: > 0 } schemaJson)
        {
            schema = JsonNode.Parse(schemaJson) as JsonObject;
        }

        var result = await render.RenderAsync(version.Engine, version.Expression, schema, values.View, values.Fields, values.Today, cancellationToken).ConfigureAwait(false);
        if (result.Error is { Kind: RenderErrorKinds.Unavailable } unavailable)
        {
            throw new RendererUnavailableException(unavailable.Message);
        }

        if (result.Error is { } error)
        {
            return RenderLimits.Shorten($"The mapping failed ({error.Kind}): {error.Message}");
        }

        if (result.SchemaErrors.Count > 0)
        {
            var first = result.SchemaErrors.Take(3).Select(e => $"{e.Path}: {e.Message}");
            return RenderLimits.Shorten($"The output does not fit the target schema ({result.SchemaErrors.Count.ToString(CultureInfo.InvariantCulture)} errors): {string.Join("; ", first)}");
        }

        if (result.Undefined || result.Output is null)
        {
            return "The mapping rendered nothing for this document.";
        }

        var bytes = Encoding.UTF8.GetBytes(result.OutputText ?? result.Output.ToJsonString());
        await using var stream = new MemoryStream(bytes, writable: false);
        var stored = await blobs.PutAsync(BlobContainers.Exports, delivery.TenantId, stream, version.ContentType, cancellationToken).ConfigureAwait(false);
        delivery.PayloadBlobKey = stored.Key;
        delivery.PayloadSha256 = stored.Sha256;
        delivery.ContentType = version.ContentType;
        delivery.MappingVersionId = version.Id;
        return null;
    }

    /// <summary>The published version of a mapping, or the pinned one (published or deprecated); null when there is none.</summary>
    public async Task<OutputMappingVersion?> MappingVersionAsync(Guid mappingId, int? pinned, CancellationToken cancellationToken)
    {
        var mapping = await db.OutputMappings.AsNoTracking().SingleOrDefaultAsync(m => m.Id == mappingId, cancellationToken).ConfigureAwait(false);
        if (mapping is null || mapping.Status != OutputMappingStatus.Active || (pinned ?? mapping.PublishedVersion) is not { } number)
        {
            return null;
        }

        return await db.OutputMappingVersions.AsNoTracking()
            .SingleOrDefaultAsync(v => v.MappingId == mappingId && v.Version == number && v.State != OutputMappingVersionState.Draft, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DeadLetterAsync(OutboxDelivery delivery, string error, OutputBinding binding, OutputDestination destination, DateTimeOffset now, CancellationToken cancellationToken)
    {
        error = RenderLimits.Shorten(error);
        delivery.Status = DeliveryStatus.DeadLettered;
        delivery.LastError = Truncate(error, 2000);
        delivery.NextAttemptAt = null;
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            TenantId = delivery.TenantId,
            DeliveryId = delivery.Id,
            At = now,
            ErrorClass = "permanent:render",
            ResponseExcerpt = Truncate(error, DeliveryAttempt.MaxExcerptLength),
        });
        DeliveryJournal.Record(db, delivery, DocumentEventTypes.DeliveryFailed, new { deliveryId = delivery.Id, binding = binding.Name, destination = destination.Name, revision = delivery.DocumentVersion, errorClass = "permanent:render", error, deadLettered = true }, now);
        await DeliveryJournal.RaiseFailedAsync(db, delivery, destination.Name, error, now, cancellationToken).ConfigureAwait(false);
    }

    private void Skip(OutboxDelivery delivery, string reason, OutputBinding binding, OutputDestination destination, DateTimeOffset now)
    {
        delivery.Status = DeliveryStatus.Skipped;
        delivery.LastError = reason;
        DeliveryJournal.Record(db, delivery, DocumentEventTypes.DeliverySkipped, new { deliveryId = delivery.Id, binding = binding.Name, destination = destination.Name, revision = delivery.DocumentVersion, reason }, now);
    }

    /// <summary>Why a binding's scope does not cover the document, or null when it does.</summary>
    internal static string? ScopeMismatch(OutputBinding binding, Document document, Guid channelId)
    {
        if (binding.DocumentTypeKey is { } type && !string.Equals(type, document.Type.Value, StringComparison.Ordinal))
        {
            return "type";
        }

        if (binding.SupplierId is { } supplier && supplier != document.SupplierId)
        {
            return "supplier";
        }

        if (binding.ChannelId is { } channel && channel != channelId)
        {
            return "channel";
        }

        // Supplier groups and receiving entities come with P4: a binding scoped to one matches nothing until then.
        return binding.GroupId is not null || binding.ReceivingEntityId is not null ? "group or receiving entity (P4)" : null;
    }

    /// <summary>
    /// The bindings that fire: all without an exclusive group, and per group the most specific one (supplier, group,
    /// channel, entity, type, tenant), then the higher priority, then the name.
    /// </summary>
    internal static HashSet<OutputBinding> Exclusive(IReadOnlyList<OutputBinding> applicable, List<FanOutEntry> entries)
    {
        var winners = new HashSet<OutputBinding>(applicable.Where(b => b.ExclusiveGroup is null));
        foreach (var group in applicable.Where(b => b.ExclusiveGroup is not null).GroupBy(b => b.ExclusiveGroup, StringComparer.Ordinal))
        {
            var ranked = group
                .OrderByDescending(Specificity)
                .ThenByDescending(b => b.Priority)
                .ThenBy(b => b.Name, StringComparer.Ordinal)
                .ToList();
            winners.Add(ranked[0]);
            foreach (var loser in ranked.Skip(1))
            {
                entries.Add(new FanOutEntry(loser.Id, loser.Name, "notApplicable", Reason: $"Exclusive group '{group.Key}': '{ranked[0].Name}' is more specific."));
            }
        }

        return winners;
    }

    /// <summary>Supplier, group, channel, entity, type, as bits from the most specific down; zero is the tenant.</summary>
    internal static int Specificity(OutputBinding binding) =>
        (binding.SupplierId is not null ? 16 : 0)
        | (binding.GroupId is not null ? 8 : 0)
        | (binding.ChannelId is not null ? 4 : 0)
        | (binding.ReceivingEntityId is not null ? 2 : 0)
        | (binding.DocumentTypeKey is not null ? 1 : 0);

    /// <summary>
    /// How a re-approval of the binding goes: <see cref="OnReapproval.Resend"/> for a first approval and for a
    /// rejection. <c>Skip</c> needs an earlier revision that was or will be sent (delivered, being sent, waiting or
    /// waiting for its retry; not a dead letter, which the receiver never got); <c>Update</c> one that reached the
    /// receiver (or is being sent), else the receiver has nothing to update and gets a plain approval.
    /// <paramref name="earlier"/> holds redeliveries too: a redelivered dead letter is <c>Cancelled</c>, and its
    /// redelivery is what reached the receiver.
    /// </summary>
    internal static OnReapproval ReapprovalMode(OutputBinding binding, string eventType, IReadOnlyList<OutboxDelivery> earlier)
    {
        if (eventType != OutputEvents.Approved || earlier.Count == 0)
        {
            return OnReapproval.Resend;
        }

        var approvals = earlier.Where(d => d.Event is OutputEvents.Approved or OutputEvents.Updated).ToList();
        return binding.OnReapproval switch
        {
            OnReapproval.Skip when approvals.Any(d => d.Status is DeliveryStatus.Delivered or DeliveryStatus.Sending or DeliveryStatus.Pending or DeliveryStatus.Failed) => OnReapproval.Skip,
            OnReapproval.Update when approvals.Any(d => d.Status is DeliveryStatus.Delivered or DeliveryStatus.Sending) => OnReapproval.Update,
            _ => OnReapproval.Resend,
        };
    }

    private static string Truncate(string text, int length) => RenderLimits.Shorten(text, length);
}
