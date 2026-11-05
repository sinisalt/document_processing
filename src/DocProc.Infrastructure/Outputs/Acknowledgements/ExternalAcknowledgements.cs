using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Abstractions;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Outputs.Acknowledgements;

/// <summary>How a receiving system reports a delivered document's fate (docs/11 §13.8): its callback or the platform's status poll.</summary>
public static class AcknowledgementSources
{
    public const string Callback = "callback";
    public const string StatusPoll = "statusPoll";
}

/// <summary>The callback authentication of a destination (docs/11 §13.8): an HMAC of the body, or a bearer token, with the callback secret.</summary>
public static class CallbackAuthTypes
{
    public const string HmacSha256 = "hmacSha256";
    public const string Bearer = "bearer";

    public static readonly IReadOnlyList<string> All = [HmacSha256, Bearer];
}

/// <summary>
/// A destination's acknowledgements (docs/11 §13.8, stored in <c>OutputDestination.CallbackConfigJson</c>):
/// <c>callback {auth, mapping?}</c>, where the receiver posts the fate of a document to
/// <c>POST /api/v1/integrations/callbacks/{destinationId}</c>, and <c>statusPoll {urlTemplate, intervalMinutes,
/// mapping?, maxPolls?}</c>, where the platform asks. Each <c>mapping</c> is JSONata (run in the out-of-process renderer)
/// that turns the body into <c>{documentId | externalId, status, message}</c>; without one the body must be that shape.
/// </summary>
public sealed record AcknowledgementConfig(CallbackConfig? Callback, StatusPollConfig? StatusPoll)
{
    public const int MaxMappingLength = 4000;
    public const int MinIntervalMinutes = 1;
    public const int MaxIntervalMinutes = 1440;
    public const int DefaultMaxPolls = 288;
    public const int MaxMaxPolls = 2000;

    public bool Any => Callback is not null || StatusPoll is not null;

    public static AcknowledgementConfig None { get; } = new(null, null);

    /// <summary>The config stored on the destination; <see cref="None"/> when there is none or it does not read.</summary>
    public static AcknowledgementConfig Of(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return None;
        }

        try
        {
            return JsonNode.Parse(json) is JsonObject root ? Parse(root, new Dictionary<string, string>(StringComparer.Ordinal)) ?? None : None;
        }
        catch (JsonException)
        {
            return None;
        }
    }

    /// <summary>Reads and checks a config; the problems go to <paramref name="errors"/> by field, and null comes back when there are any.</summary>
    public static AcknowledgementConfig? Parse(JsonObject root, IDictionary<string, string> errors)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(errors);
        foreach (var (key, _) in root)
        {
            if (key is not ("callback" or "statusPoll"))
            {
                errors[key] = "is not a setting of acknowledgements (callback, statusPoll).";
            }
        }

        CallbackConfig? callback = null;
        if (root["callback"] is JsonObject c)
        {
            var auth = Text(c, "auth") ?? CallbackAuthTypes.HmacSha256;
            if (!CallbackAuthTypes.All.Contains(auth, StringComparer.Ordinal))
            {
                errors["callback.auth"] = $"must be {string.Join(" or ", CallbackAuthTypes.All)}.";
            }

            var mapping = Text(c, "mapping");
            if (mapping is { Length: > MaxMappingLength })
            {
                errors["callback.mapping"] = $"is longer than {MaxMappingLength} characters.";
            }

            callback = new CallbackConfig(auth, mapping);
        }
        else if (root["callback"] is not null)
        {
            errors["callback"] = "must be an object.";
        }

        StatusPollConfig? poll = null;
        if (root["statusPoll"] is JsonObject p)
        {
            var template = Text(p, "urlTemplate");
            if (string.IsNullOrWhiteSpace(template))
            {
                errors["statusPoll.urlTemplate"] = "is required.";
            }
            else if (template.Length > DestinationConfig.MaxTemplateLength)
            {
                errors["statusPoll.urlTemplate"] = $"is longer than {DestinationConfig.MaxTemplateLength} characters.";
            }
            else if (!PollTemplateIsValid(template))
            {
                errors["statusPoll.urlTemplate"] = "must be an absolute http(s) URL; {externalId} and {documentId} may appear in its path or query only.";
            }

            var interval = p["intervalMinutes"] is JsonValue iv && iv.TryGetValue<int>(out var minutes) ? minutes : 15;
            if (interval is < MinIntervalMinutes or > MaxIntervalMinutes)
            {
                errors["statusPoll.intervalMinutes"] = $"must be {MinIntervalMinutes} to {MaxIntervalMinutes}.";
            }

            var maxPolls = p["maxPolls"] is JsonValue mv && mv.TryGetValue<int>(out var most) ? most : DefaultMaxPolls;
            if (maxPolls is < 1 or > MaxMaxPolls)
            {
                errors["statusPoll.maxPolls"] = $"must be 1 to {MaxMaxPolls}.";
            }

            var mapping = Text(p, "mapping");
            if (mapping is { Length: > MaxMappingLength })
            {
                errors["statusPoll.mapping"] = $"is longer than {MaxMappingLength} characters.";
            }

            poll = new StatusPollConfig(template ?? string.Empty, interval, mapping, maxPolls);
        }
        else if (root["statusPoll"] is not null)
        {
            errors["statusPoll"] = "must be an object.";
        }

        return errors.Count == 0 ? new AcknowledgementConfig(callback, poll) : null;
    }

    public JsonObject ToJson()
    {
        var root = new JsonObject();
        if (Callback is { } c)
        {
            root["callback"] = new JsonObject { ["auth"] = c.Auth, ["mapping"] = c.Mapping };
        }

        if (StatusPoll is { } p)
        {
            root["statusPoll"] = new JsonObject { ["urlTemplate"] = p.UrlTemplate, ["intervalMinutes"] = p.IntervalMinutes, ["mapping"] = p.Mapping, ["maxPolls"] = p.MaxPolls };
        }

        return root;
    }

    private static string? Text(JsonObject node, string name) =>
        node[name] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// An absolute http(s) URL whose only placeholders are <c>{externalId}</c> and <c>{documentId}</c>, and only after the
    /// host (a placeholder may never choose where the request goes).
    /// </summary>
    public static bool PollTemplateIsValid(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var filled = template.Replace("{externalId}", "x", StringComparison.Ordinal).Replace("{documentId}", "x", StringComparison.Ordinal);
        if (filled.Contains('{', StringComparison.Ordinal) || filled.Contains('}', StringComparison.Ordinal)
            || !Uri.TryCreate(filled, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        var authorityEnd = template.IndexOf('/', template.IndexOf("://", StringComparison.Ordinal) + 3);
        var firstBrace = template.IndexOf('{', StringComparison.Ordinal);
        return firstBrace < 0 || (authorityEnd >= 0 && firstBrace > authorityEnd);
    }
}

public sealed record CallbackConfig(string Auth, string? Mapping);

public sealed record StatusPollConfig(string UrlTemplate, int IntervalMinutes, string? Mapping, int MaxPolls);

/// <summary>What a callback or a status answer says about one delivered document, after its mapping.</summary>
public sealed record AcknowledgementReport(Guid? DocumentId, string? ExternalId, ExternalRefStatus Status, string? Message)
{
    public const int MaxMessageLength = 1000;

    /// <summary>
    /// Reads <c>{documentId | externalId, status, message}</c>: <c>status</c> is accepted, posted, rejected or pending (case
    /// ignored). Null with the reason when it does not fit.
    /// </summary>
    public static AcknowledgementReport? Read(JsonNode? node, out string? problem)
    {
        problem = null;
        if (node is not JsonObject obj)
        {
            problem = "The acknowledgement is not an object {documentId|externalId, status, message}.";
            return null;
        }

        Guid? documentId = obj["documentId"] is JsonValue d && d.TryGetValue<string>(out var dt) && Guid.TryParse(dt, out var parsed) ? parsed : null;
        var externalId = obj["externalId"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) => s.Trim(),
            JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.ToJsonString(),
            _ => null,
        };
        if (documentId is null && externalId is null)
        {
            problem = "The acknowledgement names neither documentId nor externalId.";
            return null;
        }

        if (externalId is { Length: > 200 })
        {
            problem = "externalId is longer than 200 characters.";
            return null;
        }

        var statusText = obj["status"] is JsonValue sv && sv.TryGetValue<string>(out var st) ? st.Trim().ToLowerInvariant() : null;
        ExternalRefStatus? status = statusText switch
        {
            "accepted" => ExternalRefStatus.Accepted,
            "posted" => ExternalRefStatus.Posted,
            "rejected" => ExternalRefStatus.Rejected,
            "pending" => ExternalRefStatus.Pending,
            _ => null,
        };
        if (status is null)
        {
            problem = "status must be accepted, posted, rejected or pending.";
            return null;
        }

        var message = obj["message"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m : null;
        if (message is { Length: > MaxMessageLength })
        {
            message = message[..MaxMessageLength];
        }

        return new AcknowledgementReport(documentId, externalId, status.Value, message);
    }
}

/// <summary>The outcome of applying an acknowledgement.</summary>
public enum AcknowledgementOutcome
{
    Applied,
    UnknownDocument,
    StillPending,
}

/// <summary>
/// Applies a receiving system's acknowledgement to the document it names (docs/11 §13.8): the
/// <see cref="DocumentExternalRef"/> takes the status; a rejection raises <c>EXTERNAL_REJECTED {destination, message}</c>
/// (once per destination while open) and opens a review task of kind <see cref="ReviewTaskKind.IntegrationIssue"/>, and
/// the document stays settled; an acceptance resolves an open <c>EXTERNAL_REJECTED</c> of the destination and, when none
/// is left, completes the integration issue. Writes are tracked; the caller saves.
/// </summary>
public static class ExternalAcknowledgements
{
    public const string Actor = "integration";

    public static async Task<AcknowledgementOutcome> ApplyAsync(DocProcDbContext db, OutputDestination destination, AcknowledgementReport report, string source, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(report);
        var tenantId = destination.TenantId;
        var destinationId = destination.Id;
        DocumentExternalRef? reference = null;
        if (report.ExternalId is { } externalId)
        {
            reference = await db.DocumentExternalRefs.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.DestinationId == destinationId && r.ExternalId == externalId, cancellationToken).ConfigureAwait(false);
        }

        if (reference is null && report.DocumentId is { } named)
        {
            reference = await db.DocumentExternalRefs.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.DestinationId == destinationId && r.DocumentId == named, cancellationToken).ConfigureAwait(false);
        }

        var documentId = reference?.DocumentId ?? report.DocumentId;
        if (documentId is not { } id)
        {
            return AcknowledgementOutcome.UnknownDocument;
        }

        // Only a document this destination was sent: a delivery of it to the destination exists.
        var document = await db.Documents.Include(d => d.Flags).FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        if (document is null || (reference is null && !await db.OutboxDeliveries.AnyAsync(d => d.DocumentId == id && d.DestinationId == destinationId, cancellationToken).ConfigureAwait(false)))
        {
            return AcknowledgementOutcome.UnknownDocument;
        }

        if (reference is null)
        {
            reference = new DocumentExternalRef
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                DocumentId = id,
                DestinationId = destinationId,
                ExternalId = report.ExternalId ?? id.ToString(),
            };
            db.DocumentExternalRefs.Add(reference);
        }
        else if (report.ExternalId is { } newId && reference.ExternalId == id.ToString())
        {
            reference.ExternalId = newId; // the receiver's own id, learned from its first answer
        }

        reference.UpdatedAt = now;
        reference.Message = report.Message;
        if (report.Status == ExternalRefStatus.Pending)
        {
            reference.Status = ExternalRefStatus.Pending;
            return AcknowledgementOutcome.StillPending;
        }

        reference.Status = report.Status;
        reference.NextPollAt = null;
        var details = new { destinationId, destination = destination.Name, status = report.Status.ToString(), externalId = reference.ExternalId, message = report.Message, source };
        var open = document.Flags.Where(f => f.Code == FlagCodes.ExternalRejected && f.ResolvedAt is null).ToList();
        var ofThis = open.Where(f => f.Params is { } p && p.TryGetValue("destinationId", out var d) && d == destinationId.ToString()).ToList();
        if (report.Status == ExternalRefStatus.Rejected)
        {
            if (ofThis.Count == 0)
            {
                var flag = FlagCodes.Create(
                    FlagCodes.ExternalRejected,
                    $"{destination.Name} rejected the document{(string.IsNullOrWhiteSpace(report.Message) ? "." : $": {report.Message}")}",
                    null,
                    FlagParams.Of(("destination", destination.Name), ("destinationId", destinationId.ToString()), ("message", report.Message), ("externalId", reference.ExternalId), ("source", source)));
                var record = DocumentFlagRecord.FromDomain(tenantId, id, flag, now);
                db.DocumentFlags.Add(record);
                if (!document.Flags.Contains(record))
                {
                    document.Flags.Add(record);
                }
            }

            var hasIssue = await db.ReviewTasks.AnyAsync(t => t.DocumentId == id && t.Kind == ReviewTaskKind.IntegrationIssue && (t.Status == ReviewTaskStatus.Open || t.Status == ReviewTaskStatus.InProgress), cancellationToken).ConfigureAwait(false);
            if (!hasIssue)
            {
                var reason = $"{FlagCodes.ExternalRejected}: {destination.Name}{(string.IsNullOrWhiteSpace(report.Message) ? string.Empty : $" ({report.Message})")}";
                db.ReviewTasks.Add(new ReviewTask
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    DocumentId = id,
                    Reason = reason.Length <= 1000 ? reason : reason[..1000],
                    Status = ReviewTaskStatus.Open,
                    Priority = ReviewPriority.High,
                    Kind = ReviewTaskKind.IntegrationIssue,
                    CreatedAt = now,
                });
            }

            AddEvent(db, document, DocumentEventTypes.ExternallyRejected, details, now);
            return AcknowledgementOutcome.Applied;
        }

        foreach (var flag in ofThis)
        {
            flag.ResolvedAt = now;
            flag.ResolvedBy = $"{Actor}:{source}";
        }

        if (open.Count == ofThis.Count)
        {
            await CloseIssuesAsync(db, id, now, cancellationToken).ConfigureAwait(false);
        }

        AddEvent(db, document, DocumentEventTypes.ExternallyAccepted, details, now);
        return AcknowledgementOutcome.Applied;
    }

    /// <summary>Completes the open integration-issue tasks of a document.</summary>
    public static async Task CloseIssuesAsync(DocProcDbContext db, Guid documentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        foreach (var task in await db.ReviewTasks.Where(t => t.DocumentId == documentId && t.Kind == ReviewTaskKind.IntegrationIssue && (t.Status == ReviewTaskStatus.Open || t.Status == ReviewTaskStatus.InProgress)).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            task.Status = ReviewTaskStatus.Done;
            task.CompletedAt = now;
        }
    }

    /// <summary>When the first status poll of a newly delivered document is due, or null when the destination does not poll.</summary>
    public static DateTimeOffset? FirstPollAt(AcknowledgementConfig config, DateTimeOffset now) =>
        config.StatusPoll is { } poll ? now.AddMinutes(poll.IntervalMinutes) : null;

    private static void AddEvent(DocProcDbContext db, Document document, string eventType, object details, DateTimeOffset at) =>
        db.DocumentEvents.Add(new DocumentEvent
        {
            TenantId = document.TenantId,
            DocumentId = document.Id,
            EventType = eventType,
            FromStatus = document.Status,
            ToStatus = document.Status,
            Actor = Actor,
            DetailsJson = JsonSerializer.Serialize(details),
            At = at,
        });

    /// <summary>The status poll URL of one reference: <c>{externalId}</c> and <c>{documentId}</c> replaced, URL-encoded.</summary>
    public static string PollUrl(StatusPollConfig poll, DocumentExternalRef reference)
    {
        ArgumentNullException.ThrowIfNull(poll);
        ArgumentNullException.ThrowIfNull(reference);
        return poll.UrlTemplate
            .Replace("{externalId}", Uri.EscapeDataString(reference.ExternalId), StringComparison.Ordinal)
            .Replace("{documentId}", reference.DocumentId.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
