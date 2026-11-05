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
    /// Whether a status poll template and the destination's <c>urlTemplate</c> (in its config JSON) name the same origin:
    /// scheme, host and effective port (the scheme's default when none is written), placeholders filled with a dummy.
    /// The poll sends the destination's headers and credentials, so it may only go where the destination itself sends
    /// them, the same rule as a change of the destination's own URL (<see cref="DestinationConfig.SecretRedirects"/>):
    /// another port of the same host is another receiver (docs/11 §13.8, Correction (P5) fix 3).
    /// </summary>
    public static bool SameOrigin(string pollTemplate, string? destinationConfigJson)
    {
        ArgumentNullException.ThrowIfNull(pollTemplate);
        string? template;
        try
        {
            template = destinationConfigJson is null ? null : JsonNode.Parse(destinationConfigJson)?["urlTemplate"]?.GetValue<string>();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            template = null;
        }

        var destination = OriginOf(template);
        return destination is not null && OriginOf(pollTemplate) is { } poll
            && string.Equals(poll.Scheme, destination.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(poll.IdnHost, destination.IdnHost, StringComparison.OrdinalIgnoreCase)
            && poll.Port == destination.Port;

        static Uri? OriginOf(string? url)
        {
            if (url is null)
            {
                return null;
            }

            var filled = System.Text.RegularExpressions.Regex.Replace(url, @"\{\{[^}]*\}\}|\{[^}]*\}", "x", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return Uri.TryCreate(filled, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri : null;
        }
    }

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

    /// <summary>A signed callback older than the last one applied to the reference, or that one again: nothing changed.</summary>
    Replayed,
}

/// <summary>
/// The signed timestamp and signature of a callback (<c>X-DocProc-Timestamp</c>, <c>X-DocProc-Signature</c>), which order
/// the callbacks of one reference (docs/11 §13.8): a replay inside the timestamp window, or a callback that arrives after
/// a later one, is not applied.
/// </summary>
public sealed record CallbackStamp(DateTimeOffset At, string Signature);

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

    public static Task<AcknowledgementOutcome> ApplyAsync(DocProcDbContext db, OutputDestination destination, AcknowledgementReport report, string source, DateTimeOffset now, CancellationToken cancellationToken, CallbackStamp? stamp = null) =>
        ApplyAsync(db, destination, report, source, now, stamp, null, cancellationToken);

    /// <summary>
    /// Applies one report of a callback (docs/11 §13.8, Correction (P5) fix 4). The reference is looked up among those
    /// the context tracks first: one an earlier report of the same callback created or renamed is not in the database
    /// yet. <paramref name="stamped"/> holds the references the callback's <paramref name="stamp"/> was recorded on
    /// already: a later report of the same callback for one of them is applied after the earlier one, in list order,
    /// and is not a replay of it.
    /// </summary>
    private static async Task<AcknowledgementOutcome> ApplyAsync(DocProcDbContext db, OutputDestination destination, AcknowledgementReport report, string source, DateTimeOffset now, CallbackStamp? stamp, HashSet<Guid>? stamped, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(report);
        var tenantId = destination.TenantId;
        var destinationId = destination.Id;
        DocumentExternalRef? reference = null;
        if (report.ExternalId is { } externalId)
        {
            reference = db.DocumentExternalRefs.Local.FirstOrDefault(r => r.TenantId == tenantId && r.DestinationId == destinationId && r.ExternalId == externalId)
                ?? await db.DocumentExternalRefs.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.DestinationId == destinationId && r.ExternalId == externalId, cancellationToken).ConfigureAwait(false);
        }

        if (reference is null && report.DocumentId is { } named)
        {
            reference = db.DocumentExternalRefs.Local.FirstOrDefault(r => r.TenantId == tenantId && r.DestinationId == destinationId && r.DocumentId == named)
                ?? await db.DocumentExternalRefs.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.DestinationId == destinationId && r.DocumentId == named, cancellationToken).ConfigureAwait(false);
        }

        return await ApplyCoreAsync(db, destination, reference, report, source, now, stamp, stamped, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a status answer to the reference that was polled (docs/11 §13.8): the answer is about this reference, so the
    /// ids it names do not choose another document; a receiver id is learned only while the reference still holds the
    /// document-id placeholder.
    /// </summary>
    public static Task<AcknowledgementOutcome> ApplyToReferenceAsync(DocProcDbContext db, OutputDestination destination, DocumentExternalRef reference, AcknowledgementReport report, string source, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(report);
        if (reference.DestinationId != destination.Id || reference.TenantId != destination.TenantId)
        {
            throw new ArgumentException("The reference is not one of the destination's.", nameof(reference));
        }

        return ApplyCoreAsync(db, destination, reference, report with { DocumentId = reference.DocumentId }, source, now, null, null, cancellationToken);
    }

    /// <summary>The signature as kept on the reference: at most <see cref="DocumentExternalRef.CallbackSignatureLength"/> characters, without spaces.</summary>
    private static string SignatureKey(string signature)
    {
        var key = signature.Replace(" ", string.Empty, StringComparison.Ordinal);
        return key.Length <= DocumentExternalRef.CallbackSignatureLength ? key : key[..DocumentExternalRef.CallbackSignatureLength];
    }

    /// <summary>
    /// Whether a callback signed in the second of the last one applied may be applied: its signature is not among those
    /// applied in that second, and fewer than <see cref="DocumentExternalRef.MaxCallbackSignatures"/> were (more in one
    /// second are refused as replays rather than forgetting one that could then be replayed).
    /// </summary>
    private static bool CanApplyInSameSecond(string? applied, string signature)
    {
        var seen = (applied ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return seen.Length < DocumentExternalRef.MaxCallbackSignatures && !seen.Contains(SignatureKey(signature), StringComparer.Ordinal);
    }

    private static async Task<AcknowledgementOutcome> ApplyCoreAsync(DocProcDbContext db, OutputDestination destination, DocumentExternalRef? reference, AcknowledgementReport report, string source, DateTimeOffset now, CallbackStamp? stamp, HashSet<Guid>? stamped, CancellationToken cancellationToken)
    {
        var tenantId = destination.TenantId;
        var destinationId = destination.Id;
        var documentId = reference?.DocumentId ?? report.DocumentId;
        if (documentId is not { } id)
        {
            return AcknowledgementOutcome.UnknownDocument;
        }

        // A signed callback is ordered by its own timestamp against the last one applied to the reference: an older one
        // (a late or replayed "rejected" after an "accepted"), or one of the same second whose signature was applied
        // already (every one of that second is kept), changes nothing. A later report of the same callback for a reference
        // an earlier report was applied to comes after it in the list: the guard ran, and the stamp was recorded, once.
        var stampedByThisCallback = stamp is not null && reference is not null && stamped?.Contains(reference.Id) == true;
        if (stamp is not null && !stampedByThisCallback && reference?.LastCallbackAt is { } last
            && (stamp.At < last || (stamp.At == last && !CanApplyInSameSecond(reference.LastCallbackSignatures, stamp.Signature))))
        {
            return AcknowledgementOutcome.Replayed;
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
        else if (report.ExternalId is { } newId && reference.ExternalId == id.ToString()
                 && !db.DocumentExternalRefs.Local.Any(r => r.TenantId == tenantId && r.DestinationId == destinationId && r.Id != reference.Id && r.ExternalId == newId)
                 && !await db.DocumentExternalRefs.AnyAsync(r => r.TenantId == tenantId && r.DestinationId == destinationId && r.Id != reference.Id && r.ExternalId == newId, cancellationToken).ConfigureAwait(false))
        {
            reference.ExternalId = newId; // the receiver's own id, learned from its first answer (never another reference's)
        }

        reference.UpdatedAt = now;
        reference.Message = report.Message;
        if (stamp is not null && !stampedByThisCallback)
        {
            stamped?.Add(reference.Id);
            var signature = SignatureKey(stamp.Signature);
            reference.LastCallbackSignatures = reference.LastCallbackAt == stamp.At && reference.LastCallbackSignatures is { Length: > 0 } seen
                ? $"{seen} {signature}"
                : signature;
            reference.LastCallbackAt = stamp.At;
        }

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

            if ((await OpenIssuesAsync(db, id, cancellationToken).ConfigureAwait(false)).Count == 0)
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

    /// <summary>How often the acknowledgements of one callback are applied again after another acknowledgement of one of their references won the race.</summary>
    public const int MaxApplyAttempts = 5;

    /// <summary>
    /// Applies the acknowledgements of one callback and saves them, ordered per reference (docs/11 §13.8): each attempt
    /// reads, guards and writes on a fresh context and is saved by <see cref="TrySaveAsync"/>; when another
    /// acknowledgement of one of the references committed first, the attempt is dropped and the reports are applied again
    /// on what is there now (a replay then answers <see cref="AcknowledgementOutcome.Replayed"/>, and an older callback
    /// changes nothing). The outcomes in the order of the reports, or null when every one of
    /// <see cref="MaxApplyAttempts"/> attempts lost.
    /// </summary>
    public static async Task<IReadOnlyList<AcknowledgementOutcome>?> ApplyInOrderAsync(ISystemDbContextFactory contexts, OutputDestination destination, IReadOnlyList<AcknowledgementReport> reports, string source, DateTimeOffset now, CallbackStamp? stamp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(reports);
        for (var attempt = 1; attempt <= MaxApplyAttempts; attempt++)
        {
            var db = contexts.CreatePlatformScoped();
            await using (db.ConfigureAwait(false))
            {
                var outcomes = new List<AcknowledgementOutcome>(reports.Count);
                var stamped = new HashSet<Guid>();
                foreach (var report in reports)
                {
                    outcomes.Add(await ApplyAsync(db, destination, report, source, now, stamp, stamped, cancellationToken).ConfigureAwait(false));
                }

                if (await TrySaveAsync(db, cancellationToken).ConfigureAwait(false))
                {
                    return outcomes;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Saves the tracked writes of the acknowledgements applied on <paramref name="db"/>, ordered per reference (docs/11
    /// §13.8, Correction (P5) fix 3). In one transaction, every reference that was changed has its
    /// <see cref="DocumentExternalRef.AcknowledgementVersion"/> moved from the value read to the next, and only then are
    /// the writes saved. When another acknowledgement of the same reference committed since it was read (the version
    /// moved, or a reference both created was inserted first, or a review task both changed was changed first), nothing
    /// is written and false comes back: the caller drops the context's changes and applies the acknowledgement again on
    /// what is there now, so the order guard sees the other one (a replay answers "replayed", an older callback changes
    /// nothing) and a rejection never opens a second flag or a second integration issue.
    /// </summary>
    public static async Task<bool> TrySaveAsync(DocProcDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            async ct =>
            {
                var changed = db.ChangeTracker.Entries<DocumentExternalRef>()
                    .Where(e => e.State == EntityState.Modified)
                    .Select(e => (Entry: e, Read: e.Property(r => r.AcknowledgementVersion).OriginalValue))
                    .ToList();
                await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                foreach (var (entry, read) in changed)
                {
                    var id = entry.Entity.Id;
                    var moved = await db.DocumentExternalRefs
                        .Where(r => r.Id == id && r.AcknowledgementVersion == read)
                        .ExecuteUpdateAsync(u => u.SetProperty(r => r.AcknowledgementVersion, read + 1), ct).ConfigureAwait(false);
                    if (moved == 0)
                    {
                        await transaction.RollbackAsync(ct).ConfigureAwait(false);
                        return false;
                    }
                }

                try
                {
                    await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, ct).ConfigureAwait(false);
                }
                catch (DbUpdateException exception) when (IsConflict(exception))
                {
                    await transaction.RollbackAsync(ct).ConfigureAwait(false);
                    return false;
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                foreach (var (entry, read) in changed)
                {
                    entry.Property(r => r.AcknowledgementVersion).CurrentValue = read + 1;
                }

                db.ChangeTracker.AcceptAllChanges();
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A save that lost to another acknowledgement of the same reference: a concurrency token of a row both changed (a
    /// review task), or the unique (document, destination) index of a reference both created.
    /// </summary>
    private static bool IsConflict(DbUpdateException exception)
    {
        if (exception is DbUpdateConcurrencyException)
        {
            return true;
        }

        for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner.Message.Contains("IX_DocumentExternalRefs_DocumentId_DestinationId", StringComparison.OrdinalIgnoreCase)
                || inner.Message.Contains("DocumentExternalRefs.DocumentId", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Completes the open integration-issue tasks of a document.</summary>
    public static async Task CloseIssuesAsync(DocProcDbContext db, Guid documentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        foreach (var task in await OpenIssuesAsync(db, documentId, cancellationToken).ConfigureAwait(false))
        {
            task.Status = ReviewTaskStatus.Done;
            task.CompletedAt = now;
        }
    }

    /// <summary>
    /// The open integration-issue tasks of a document as the context has them: those of the database and those an earlier
    /// acknowledgement of the same callback opened (not saved yet), without any it completed already.
    /// </summary>
    private static async Task<List<ReviewTask>> OpenIssuesAsync(DocProcDbContext db, Guid documentId, CancellationToken cancellationToken)
    {
        await db.ReviewTasks.Where(t => t.DocumentId == documentId && t.Kind == ReviewTaskKind.IntegrationIssue && (t.Status == ReviewTaskStatus.Open || t.Status == ReviewTaskStatus.InProgress)).LoadAsync(cancellationToken).ConfigureAwait(false);
        return [.. db.ReviewTasks.Local.Where(t => t.DocumentId == documentId && t.Kind == ReviewTaskKind.IntegrationIssue && t.Status is ReviewTaskStatus.Open or ReviewTaskStatus.InProgress)];
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
