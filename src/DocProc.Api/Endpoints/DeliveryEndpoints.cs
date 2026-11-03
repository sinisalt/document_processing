using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Outputs;
using DocProc.Application.Pipeline;
using DocProc.Infrastructure.Outputs.Delivery;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Api.Endpoints;

/// <summary>One delivery as the API shows it (docs/11 §13.6): what was rendered with what, for which binding and destination, and where it stands.</summary>
public sealed record DeliverySummary(
    Guid Id,
    Guid DocumentId,
    int DocumentVersion,
    string Event,
    Guid BindingId,
    string? BindingName,
    Guid DestinationId,
    string? DestinationName,
    string? Transport,
    Guid MappingVersionId,
    int? MappingVersion,
    string IdempotencyKey,
    string Status,
    int Attempts,
    DateTimeOffset? NextAttemptAt,
    int? LastStatusCode,
    string? LastError,
    string? ContentType,
    string? PayloadSha256,
    long? FeedSequence,
    Guid? RedeliveryOfId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeliveredAt);

/// <summary>One attempt at a delivery: when, how long, the status code, the error class and the response excerpt (4 KB, secrets masked).</summary>
public sealed record DeliveryAttemptDto(DateTimeOffset At, int DurationMs, int? StatusCode, string? ErrorClass, string? ResponseExcerpt, string? Step);

/// <summary>The receiving system's id of the document at the delivery's destination (<c>responseMapping</c>).</summary>
public sealed record ExternalRefDto(string ExternalId, string Status, string? Message, DateTimeOffset UpdatedAt);

/// <summary>A delivery with its attempts and the document's external id at its destination.</summary>
public sealed record DeliveryDetail(DeliverySummary Delivery, IReadOnlyList<DeliveryAttemptDto> Attempts, ExternalRefDto? ExternalRef);

/// <summary>
/// <c>POST /deliveries/{id}:redeliver</c> (docs/11 §13.6): <c>snapshot</c> (the default) sends the stored payload
/// again; <c>rerender</c> renders the document again, with <see cref="MappingVersion"/> of the binding's mapping or the
/// version the binding follows now.
/// </summary>
public sealed record RedeliverRequest(string? Mode, int? MappingVersion);

/// <summary>
/// One entry of a pull feed (docs/11 §13.3): its <see cref="Sequence"/> (the <c>after</c> cursor), the delivery, and
/// the payload, as JSON in <see cref="Payload"/> or, for any other content type, base64 in <see cref="PayloadBase64"/>.
/// </summary>
public sealed record FeedEntry(
    long Sequence,
    Guid DeliveryId,
    Guid DocumentId,
    int DocumentVersion,
    string Event,
    string IdempotencyKey,
    string? ContentType,
    string? PayloadSha256,
    DateTimeOffset CreatedAt,
    bool Acknowledged,
    JsonElement? Payload,
    string? PayloadBase64);

/// <summary>A page of a feed: the entries after the cursor, <see cref="Next"/> to pass as <c>after</c> next time, and whether more are waiting.</summary>
public sealed record FeedPage(IReadOnlyList<FeedEntry> Items, long Next, bool HasMore);

/// <summary>
/// <c>POST /feeds/{destinationId}:ack</c>: acknowledges every entry up to and including <see cref="UpTo"/>, and/or the
/// listed <see cref="DeliveryIds"/>. Acknowledging an entry twice is not an error.
/// </summary>
public sealed record FeedAckRequest(long? UpTo, IReadOnlyList<Guid>? DeliveryIds);

/// <summary>How many entries this call acknowledged, and whether entries up to <c>upTo</c> are left for another call.</summary>
public sealed record FeedAckResponse(int Acknowledged, bool More);

/// <summary>
/// Deliveries and pull feeds (docs/11 §13.3, §13.6, §18 "Outputs"). Everything needs the tenant's <c>outputs</c>
/// switch. Reading deliveries is <see cref="Policies.Read"/> (a document's Deliveries tab), redelivering is
/// <see cref="Policies.IntegrationAdmin"/>. The feed is <see cref="Policies.DeliveriesRead"/>: integration admins and
/// API keys with <c>deliveries:read</c>, a key restricted to one destination reading only that one.
/// </summary>
public static class DeliveryEndpoints
{
    private const string What = "Deliveries";

    public const int MaxAckIds = 500;

    /// <summary>The most entries one acknowledgement moves on; the rest are for the next call (<see cref="FeedAckResponse.More"/>).</summary>
    public const int MaxAckBatch = 1000;

    /// <summary>The payload bytes one feed page carries at most (always at least one entry).</summary>
    public const int MaxFeedPageBytes = 8 * 1024 * 1024;

    public static RouteGroupBuilder MapDeliveries(this RouteGroupBuilder group)
    {
        var deliveries = group.MapGroup("/deliveries").WithTags("Outputs");
        deliveries.MapGet("/", ListAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("ListDeliveries")
            .WithSummary("The tenant's deliveries, newest first, filtered by document, destination and status.");
        deliveries.MapGet("/{id:guid}", GetAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("GetDelivery")
            .WithSummary("One delivery with its attempts and the external id.");
        deliveries.MapGet("/{id:guid}/payload", PayloadAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("GetDeliveryPayload")
            .WithSummary("The payload snapshot the delivery sends.");
        deliveries.MapPost("/{id:guid}:redeliver", RedeliverAsync)
            .RequireAuthorization(Policies.IntegrationAdmin)
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .WithName("RedeliverDelivery")
            .WithSummary("Sends a delivery again as a new delivery, from its snapshot or rendered again; a dead letter it replaces is cancelled.");

        var feeds = group.MapGroup("/feeds").WithTags("Outputs");
        feeds.MapGet("/{destinationId:guid}", FeedAsync)
            .RequireAuthorization(Policies.DeliveriesRead)
            .WithName("ReadFeed")
            .WithSummary("The entries of a pull destination's feed after the cursor (without one: from the oldest entry not acknowledged).");
        feeds.MapPost("/{destinationId:guid}:ack", AckAsync)
            .RequireAuthorization(Policies.DeliveriesRead)
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024))
            .WithName("AcknowledgeFeed")
            .WithSummary("Acknowledges feed entries, which marks their deliveries delivered.");
        return group;
    }

    private static async Task<Results<Ok<Page<DeliverySummary>>, ValidationProblem, ProblemHttpResult>> ListAsync(
        Guid? documentId,
        Guid? destinationId,
        string? status,
        string? cursor,
        int? limit,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        CancellationToken ct)
    {
        if (await OutputMappingEndpoints.GuardAsync(tenant, features, ct, What) is { } problem)
        {
            return problem;
        }

        var query = db.OutboxDeliveries.AsNoTracking();
        if (documentId is { } document)
        {
            query = query.Where(d => d.DocumentId == document);
        }

        if (destinationId is { } destination)
        {
            query = query.Where(d => d.DestinationId == destination);
        }

        if (status is not null)
        {
            if (!Enum.TryParse<DeliveryStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || int.TryParse(status, CultureInfo.InvariantCulture, out _))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["status"] = [$"status is one of {string.Join(", ", Enum.GetNames<DeliveryStatus>())}."] });
            }

            query = query.Where(d => d.Status == parsed);
        }

        if (!string.IsNullOrEmpty(cursor))
        {
            if (!PageCursor.TryDecode(cursor, out var c))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["cursor"] = ["The cursor is not one this API issued."] });
            }

            query = query.Where(d => d.CreatedAt < c.At || (d.CreatedAt == c.At && d.Id.CompareTo(c.Id) < 0));
        }

        var size = PageCursor.ClampLimit(limit);
        var rows = await query.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id).Take(size + 1)
            .Include(d => d.Binding).Include(d => d.Destination).Include(d => d.MappingVersion)
            .ToListAsync(ct);
        var hasMore = rows.Count > size;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return TypedResults.Ok(new Page<DeliverySummary>([.. rows.Select(Summary)], hasMore ? new PageCursor(rows[^1].CreatedAt, rows[^1].Id).Encode() : null));
    }

    private static async Task<Results<Ok<DeliveryDetail>, ProblemHttpResult>> GetAsync(Guid id, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await OutputMappingEndpoints.GuardAsync(tenant, features, ct, What) is { } problem)
        {
            return problem;
        }

        var row = await db.OutboxDeliveries.AsNoTracking()
            .Include(d => d.Binding).Include(d => d.Destination).Include(d => d.MappingVersion).Include(d => d.AttemptLog)
            .SingleOrDefaultAsync(d => d.Id == id, ct);
        if (row is null)
        {
            return NotFound(id);
        }

        var reference = await db.DocumentExternalRefs.AsNoTracking().SingleOrDefaultAsync(r => r.DocumentId == row.DocumentId && r.DestinationId == row.DestinationId, ct);
        var attempts = row.AttemptLog.OrderBy(a => a.At).ThenBy(a => a.Id)
            .Select(a => new DeliveryAttemptDto(a.At, a.DurationMs, a.StatusCode, a.ErrorClass, a.ResponseExcerpt, a.Step)).ToList();
        return TypedResults.Ok(new DeliveryDetail(
            Summary(row),
            attempts,
            reference is null ? null : new ExternalRefDto(reference.ExternalId, reference.Status.ToString(), reference.Message, reference.UpdatedAt)));
    }

    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> PayloadAsync(Guid id, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, [FromServices] IContainerBlobStore blobs, CancellationToken ct)
    {
        if (await OutputMappingEndpoints.GuardAsync(tenant, features, ct, What) is { } problem)
        {
            return problem;
        }

        var row = await db.OutboxDeliveries.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        if (row is null)
        {
            return NotFound(id);
        }

        var bytes = await ReadPayloadAsync(blobs, row, ct);
        return bytes is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No payload", detail: "The delivery has no payload snapshot (it was skipped, or its render failed).")
            : TypedResults.File(bytes, row.ContentType ?? "application/octet-stream");
    }

    private static async Task<Results<Accepted<DeliverySummary>, ValidationProblem, ProblemHttpResult>> RedeliverAsync(
        Guid id,
        RedeliverRequest? request,
        ITenantContext tenant,
        ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] DeliveryFanOut fanOut,
        [FromServices] IDocumentDataViewBuilder views,
        [FromServices] IPipelineQueue queue,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await OutputMappingEndpoints.GuardAsync(tenant, features, ct, What) is { } problem)
        {
            return problem;
        }

        var mode = request?.Mode?.Trim().ToLowerInvariant() ?? "snapshot";
        if (mode is not ("snapshot" or "rerender"))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["mode"] = ["mode is snapshot or rerender."] });
        }

        if (mode == "snapshot" && request?.MappingVersion is not null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["mappingVersion"] = ["mappingVersion goes with mode rerender."] });
        }

        var original = await db.OutboxDeliveries.Include(d => d.Binding).Include(d => d.Destination).Include(d => d.MappingVersion).SingleOrDefaultAsync(d => d.Id == id, ct);
        if (original is null)
        {
            return NotFound(id);
        }

        var now = clock.GetUtcNow();
        if (original.Status == DeliveryStatus.Sending && original.Destination?.Transport != OutputTransports.Pull && original.NextAttemptAt > now)
        {
            return EndpointSupport.Conflict("Delivery in flight", "The delivery is being sent right now; wait for the attempt to end.");
        }

        var count = await db.OutboxDeliveries.CountAsync(d => d.RedeliveryOfId == original.Id, ct);
        var redelivery = new OutboxDelivery
        {
            Id = Guid.CreateVersion7(),
            TenantId = original.TenantId,
            OutboxMessageId = original.OutboxMessageId,
            DocumentId = original.DocumentId,
            DocumentVersion = original.DocumentVersion,
            BindingId = original.BindingId,
            DestinationId = original.DestinationId,
            MappingVersionId = original.MappingVersionId,
            IdempotencyKey = $"{original.IdempotencyKey}:r{(count + 1).ToString(CultureInfo.InvariantCulture)}",
            Event = original.Event,
            RedeliveryOfId = original.Id,
            PayloadBlobKey = original.PayloadBlobKey,
            PayloadSha256 = original.PayloadSha256,
            ContentType = original.ContentType,
            Status = DeliveryStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
        };
        if (redelivery.IdempotencyKey.Length > 200)
        {
            return EndpointSupport.Conflict("Too many redeliveries", "This delivery was redelivered too often to chain another; redeliver the original one.");
        }

        if (mode == "snapshot" && original.PayloadBlobKey is null)
        {
            return EndpointSupport.Conflict("No snapshot", "The delivery has no payload snapshot (it was skipped, or its render failed); redeliver with mode rerender.");
        }

        if (mode == "rerender")
        {
            var binding = original.Binding!;
            var mappingId = original.Event == OutputEvents.Updated ? binding.UpdateMappingId ?? binding.MappingId : binding.MappingId;
            var version = await fanOut.MappingVersionAsync(mappingId, request?.MappingVersion ?? (mappingId == binding.MappingId ? binding.PinnedMappingVersion : null), ct);
            if (version is null)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["mappingVersion"] = ["The binding's mapping has no such published version."] });
            }

            string? failure;
            try
            {
                failure = await fanOut.RenderAsync(redelivery, version, original.Event, async (forEvent, fields) =>
                    await views.BuildAsync(new DataViewRequest(original.DocumentId) { Event = forEvent, EventAt = now, IncludeFields = fields }, ct)
                    ?? throw new InvalidOperationException("The document is not visible."), ct);
            }
            catch (RendererUnavailableException exception)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Renderer unavailable", detail: exception.Message);
            }

            if (failure is not null)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Render failed", detail: failure);
            }
        }

        db.OutboxDeliveries.Add(redelivery);
        if (original.Status is DeliveryStatus.DeadLettered or DeliveryStatus.Failed or DeliveryStatus.Pending)
        {
            original.Status = DeliveryStatus.Cancelled;
            original.NextAttemptAt = null;
            original.LastError = $"Replaced by redelivery {redelivery.Id:D}.";
        }

        var actor = DocumentQueries.ActorName(user);
        DeliveryJournal.Record(db, redelivery, DocumentEventTypes.RedeliveryRequested, new { deliveryId = redelivery.Id, redeliveryOf = original.Id, mode, binding = original.Binding?.Name, destination = original.Destination?.Name, revision = original.DocumentVersion }, now, actor);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return EndpointSupport.Conflict("Delivery changed", "The delivery changed while it was being redelivered; read it again.");
        }

        await queue.EnqueueAsync(new ProcessDelivery(redelivery.TenantId, redelivery.Id, redelivery.DestinationId, redelivery.DocumentId), TimeSpan.Zero, ct);
        redelivery.Binding = original.Binding;
        redelivery.Destination = original.Destination;
        return TypedResults.Accepted($"/api/v1/deliveries/{redelivery.Id}", Summary(redelivery));
    }

    private static async Task<Results<Ok<FeedPage>, ValidationProblem, ProblemHttpResult>> FeedAsync(
        Guid destinationId,
        long? after,
        int? limit,
        ITenantContext tenant,
        ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IContainerBlobStore blobs,
        CancellationToken ct)
    {
        if (await FeedGuardAsync(destinationId, tenant, user, db, features, ct) is { } problem)
        {
            return problem;
        }

        if (after is < 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["after"] = ["after is a sequence number a feed page returned (0 or more)."] });
        }

        var entries = db.OutboxDeliveries.AsNoTracking()
            .Where(d => d.DestinationId == destinationId && d.FeedSequence != null && (d.Status == DeliveryStatus.Sending || d.Status == DeliveryStatus.Delivered));
        var from = after ?? (await entries.Where(d => d.Status == DeliveryStatus.Sending).MinAsync(d => d.FeedSequence, ct) is { } oldest ? oldest - 1 : null);
        if (from is null)
        {
            // Nothing waits: the caller polls again after the last sequence handed out.
            var last = await db.OutputDestinations.AsNoTracking().Where(d => d.Id == destinationId).Select(d => d.FeedSequence).SingleAsync(ct);
            return TypedResults.Ok(new FeedPage([], last, false));
        }

        var size = PageCursor.ClampLimit(limit);
        var rows = await entries.Where(d => d.FeedSequence > from).OrderBy(d => d.FeedSequence).Take(size + 1).ToListAsync(ct);
        var items = new List<FeedEntry>();
        long bytes = 0;
        foreach (var row in rows.Take(size))
        {
            var payload = await ReadPayloadAsync(blobs, row, ct) ?? [];
            if (items.Count > 0 && bytes + payload.Length > MaxFeedPageBytes)
            {
                break;
            }

            bytes += payload.Length;
            items.Add(Entry(row, payload));
        }

        var hasMore = rows.Count > items.Count;
        return TypedResults.Ok(new FeedPage(items, items.Count > 0 ? items[^1].Sequence : from.Value, hasMore));
    }

    private static async Task<Results<Ok<FeedAckResponse>, ValidationProblem, ProblemHttpResult>> AckAsync(
        Guid destinationId,
        FeedAckRequest request,
        ITenantContext tenant,
        ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await FeedGuardAsync(destinationId, tenant, user, db, features, ct) is { } problem)
        {
            return problem;
        }

        var ids = request.DeliveryIds ?? [];
        if (request.UpTo is null && ids.Count == 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["upTo"] = ["Send upTo (a sequence number) or deliveryIds."] });
        }

        if (ids.Count > MaxAckIds)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["deliveryIds"] = [$"At most {MaxAckIds} delivery ids per call."] });
        }

        var upTo = request.UpTo;
        var waiting = db.OutboxDeliveries
            .Where(d => d.DestinationId == destinationId && d.FeedSequence != null && d.Status == DeliveryStatus.Sending)
            .Where(d => (upTo != null && d.FeedSequence <= upTo) || ids.Contains(d.Id));
        var rows = await waiting.OrderBy(d => d.FeedSequence).Take(MaxAckBatch + 1).ToListAsync(ct);
        var more = rows.Count > MaxAckBatch;
        if (more)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var now = clock.GetUtcNow();
        var destination = await db.OutputDestinations.AsNoTracking().Where(d => d.Id == destinationId).Select(d => d.Name).SingleAsync(ct);
        var actor = DocumentQueries.ActorName(user);
        foreach (var row in rows)
        {
            row.Status = DeliveryStatus.Delivered;
            row.DeliveredAt = now;
            row.NextAttemptAt = null;
            db.DeliveryAttempts.Add(new DeliveryAttempt
            {
                TenantId = row.TenantId,
                DeliveryId = row.Id,
                At = now,
                ErrorClass = "delivered",
                ResponseExcerpt = $"Acknowledged by {actor}.",
            });
            DeliveryJournal.Record(db, row, DocumentEventTypes.DeliverySucceeded, new { deliveryId = row.Id, destination, transport = OutputTransports.Pull, revision = row.DocumentVersion, @event = row.Event, sequence = row.FeedSequence, acknowledgedBy = actor }, now, actor);
        }

        foreach (var row in rows.DistinctBy(r => r.DocumentId))
        {
            await DeliveryJournal.ResolveFailedAsync(db, row, now, actor, ct);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return EndpointSupport.Conflict("Feed changed", "Some entries changed while they were acknowledged (a redelivery); acknowledge again.");
        }

        return TypedResults.Ok(new FeedAckResponse(rows.Count, more));
    }

    /// <summary>The tenant and <c>outputs</c> guard, the destination (a pull destination of the tenant), and a key's destination restriction.</summary>
    private static async Task<ProblemHttpResult?> FeedGuardAsync(Guid destinationId, ITenantContext tenant, ClaimsPrincipal user, DocProcDbContext db, ITenantFeatures features, CancellationToken ct)
    {
        if (await OutputMappingEndpoints.GuardAsync(tenant, features, ct, "Feeds") is { } problem)
        {
            return problem;
        }

        if (user.FindFirst(ApiKeyAuthenticationHandler.DestinationClaimType)?.Value is { } restricted
            && !string.Equals(restricted, destinationId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Other destination", detail: "This API key reads the feed of another destination only.");
        }

        var transport = await db.OutputDestinations.AsNoTracking().Where(d => d.Id == destinationId).Select(d => d.Transport).SingleOrDefaultAsync(ct);
        return transport == OutputTransports.Pull
            ? null
            : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Feed not found", detail: $"The tenant has no pull destination {destinationId}.");
    }

    private static FeedEntry Entry(OutboxDelivery row, byte[] payload)
    {
        JsonElement? json = null;
        string? base64 = null;
        if (row.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                json = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                base64 = Convert.ToBase64String(payload);
            }
        }
        else
        {
            base64 = Convert.ToBase64String(payload);
        }

        return new FeedEntry(row.FeedSequence!.Value, row.Id, row.DocumentId, row.DocumentVersion, row.Event, row.IdempotencyKey, row.ContentType, row.PayloadSha256, row.CreatedAt, row.Status == DeliveryStatus.Delivered, json, base64);
    }

    private static async Task<byte[]?> ReadPayloadAsync(IContainerBlobStore blobs, OutboxDelivery row, CancellationToken ct)
    {
        if (row.PayloadBlobKey is not { } key || await blobs.FindAsync(BlobContainers.Exports, key, ct) is not { } reference)
        {
            return null;
        }

        await using var stream = await blobs.OpenReadAsync(reference, ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private static DeliverySummary Summary(OutboxDelivery d) => new(
        d.Id,
        d.DocumentId,
        d.DocumentVersion,
        d.Event,
        d.BindingId,
        d.Binding?.Name,
        d.DestinationId,
        d.Destination?.Name,
        d.Destination?.Transport,
        d.MappingVersionId,
        d.MappingVersion?.Version,
        d.IdempotencyKey,
        d.Status.ToString(),
        d.Attempts,
        d.NextAttemptAt,
        d.LastStatusCode,
        d.LastError,
        d.ContentType,
        d.PayloadSha256,
        d.FeedSequence,
        d.RedeliveryOfId,
        d.CreatedAt,
        d.DeliveredAt);

    private static ProblemHttpResult NotFound(Guid id) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Delivery not found", detail: $"The tenant has no delivery {id}.");
}
