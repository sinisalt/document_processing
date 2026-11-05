using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Outputs;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Outputs.Acknowledgements;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Api.Endpoints;

/// <summary>The answer of a callback: how many acknowledgements were applied, and the ones naming no document of the destination.</summary>
/// <para><c>Replayed</c> counts the acknowledgements of a signed callback that is older than the last one applied to the
/// document, or that one again (a replay inside the timestamp window): they change nothing.</para>
public sealed record CallbackResponse(int Applied, int Pending, int Unknown, IReadOnlyList<string> Problems, int Replayed = 0);

/// <summary><c>PUT /destinations/{id}/callback-secret</c>: the HMAC key or bearer token the receiver signs its callbacks with.</summary>
public sealed record SetCallbackSecretRequest(string Secret);

/// <summary><c>POST /documents/{id}:resolve-integration-issue</c>: the reviewer's note (required).</summary>
public sealed record ResolveIntegrationIssueRequest(string? Note);

/// <summary>
/// Acknowledgements from the receiving systems (docs/11 §13.8): the anonymous, secret-authenticated callback
/// <c>POST /integrations/callbacks/{destinationId}</c>; a destination's acknowledgement settings (<c>PUT
/// /destinations/{id}/acknowledgements</c>, the callback and the status poll) and its callback secret (two active, for
/// rotation); and <c>POST /documents/{id}:resolve-integration-issue</c>, which closes an <c>EXTERNAL_REJECTED</c> with a
/// note. A rejection raises <c>EXTERNAL_REJECTED</c> and opens an integration-issue review task; the document stays
/// settled (<see cref="ExternalAcknowledgements"/>).
/// </summary>
public static class IntegrationEndpoints
{
    /// <summary>The largest callback body.</summary>
    public const int MaxCallbackBytes = 64 * 1024;

    /// <summary>The most acknowledgements one callback carries (a mapping may give a list).</summary>
    public const int MaxReportsPerCallback = 100;

    /// <summary>How far a signed callback's timestamp may be from now (replay window).</summary>
    public static readonly TimeSpan SignatureWindow = TimeSpan.FromMinutes(5);

    public const string SignatureHeader = "X-DocProc-Signature";
    public const string TimestampHeader = "X-DocProc-Timestamp";

    public static RouteGroupBuilder MapIntegrations(this RouteGroupBuilder group)
    {
        group.MapPost("/integrations/callbacks/{destinationId:guid}", CallbackAsync)
            .WithTags("Outputs")
            .AllowAnonymous()
            .WithMetadata(new RequestSizeLimitAttribute(MaxCallbackBytes))
            .WithName("ReceiveIntegrationCallback")
            .WithSummary("A receiving system reports the fate of delivered documents (docs/11 §13.8): authenticated by the destination's callback secret (X-DocProc-Signature: sha256=<hex HMAC of '<X-DocProc-Timestamp>.<body>'>, or Authorization: Bearer); the destination's callback mapping turns the body into {documentId|externalId, status, message}.");

        var destinations = group.MapGroup("/destinations").WithTags("Outputs");
        destinations.MapPut("/{id:guid}/acknowledgements", SetAcknowledgementsAsync)
            .RequireAuthorization(Policies.IntegrationAdmin)
            .WithMetadata(new RequestSizeLimitAttribute(32 * 1024))
            .WithName("SetDestinationAcknowledgements")
            .WithSummary("Sets how the destination reports back (docs/11 §13.8): callback {auth: hmacSha256|bearer, mapping?} and/or statusPoll {urlTemplate, intervalMinutes, mapping?, maxPolls?}; an empty object turns both off.");
        destinations.MapPut("/{id:guid}/callback-secret", SetCallbackSecretAsync)
            .RequireAuthorization(Policies.IntegrationAdmin)
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .WithName("SetDestinationCallbackSecret")
            .WithSummary("Sets or rotates the destination's callback secret; the replaced one is still accepted until the next rotation.");

        group.MapPost("/documents/{id:guid}:resolve-integration-issue", ResolveIssueAsync)
            .WithTags("Documents")
            .RequireAuthorization(Policies.Review)
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .WithName("ResolveDocumentIntegrationIssue")
            .WithSummary("Resolves the document's open EXTERNAL_REJECTED flags with a note and completes its integration-issue review tasks; the document stays settled.");
        return group;
    }

    /// <summary>Whether a status poll template and the destination's <c>urlTemplate</c> name the same host (placeholders filled with a dummy).</summary>
    internal static bool SameHost(string pollTemplate, string destinationConfigJson) => AcknowledgementConfig.SameHost(pollTemplate, destinationConfigJson);

    /// <summary>Where a destination's callback secret lives (two slots); computed from the ids.</summary>
    internal static string CallbackSecretReference(Guid tenantId, Guid destinationId, int slot) => $"tenant/{tenantId:D}/destination/{destinationId:D}/callback/{slot}";

    private static async Task<Results<Ok<CallbackResponse>, ProblemHttpResult>> CallbackAsync(
        Guid destinationId,
        HttpRequest request,
        [FromServices] ISystemDbContextFactory contexts,
        [FromServices] ISecretStore secrets,
        [FromServices] IOutputRenderer renderer,
        TimeProvider clock,
        CancellationToken ct)
    {
        // Anonymous: the destination's callback secret is the credential, so the row is read in platform scope and every
        // later read and write names its tenant.
        await using var db = contexts.CreatePlatformScoped();
        var destination = await db.OutputDestinations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == destinationId, ct);
        var config = AcknowledgementConfig.Of(destination?.CallbackConfigJson);
        if (destination is null || config.Callback is not { } callback || destination.CallbackSecretRef is null || destination.Status != OutputDestinationStatus.Active)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No callback", detail: "No destination with callbacks answers at this address.");
        }

        var body = await ReadBodyAsync(request, ct);
        if (body is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Body too large", detail: $"A callback is at most {MaxCallbackBytes / 1024} KB.");
        }

        var now = clock.GetUtcNow();
        var candidates = new List<string>();
        foreach (var reference in new[] { destination.CallbackSecretRef, destination.PreviousCallbackSecretRef })
        {
            if (reference is not null && await secrets.GetAsync(reference, ct) is { Length: > 0 } secret)
            {
                candidates.Add(secret);
            }
        }

        if (!Authenticated(callback.Auth, request, body, candidates, now))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Not authenticated", detail: "The callback's signature or token does not match the destination's callback secret.");
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not JSON", detail: "The callback body is not JSON.");
        }

        if (callback.Mapping is { Length: > 0 } mapping)
        {
            var rendered = await renderer.RenderAsync(new RenderRequest(mapping, parsed, DateOnly.FromDateTime(now.UtcDateTime)), ct);
            if (rendered.Error is { Kind: RenderErrorKinds.Unavailable } unavailable)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Renderer unavailable", detail: unavailable.Message);
            }

            if (rendered.Error is { } error)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Callback mapping failed", detail: $"The destination's callback mapping failed ({error.Kind}): {error.Message}");
            }

            parsed = rendered.Output;
        }

        var items = parsed is JsonArray array ? [.. array] : new List<JsonNode?> { parsed };
        if (items.Count > MaxReportsPerCallback)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Too many acknowledgements", detail: $"A callback carries at most {MaxReportsPerCallback} acknowledgements.");
        }

        int applied = 0, pending = 0, unknown = 0, replayed = 0;
        var stamp = CallbackStampOf(callback.Auth, request);
        var problems = new List<string>();
        foreach (var item in items)
        {
            if (AcknowledgementReport.Read(item, out var problem) is not { } report)
            {
                problems.Add(problem!);
                continue;
            }

            switch (await ExternalAcknowledgements.ApplyAsync(db, destination, report, AcknowledgementSources.Callback, now, ct, stamp))
            {
                case AcknowledgementOutcome.Applied:
                    applied++;
                    break;
                case AcknowledgementOutcome.StillPending:
                    pending++;
                    break;
                case AcknowledgementOutcome.Replayed:
                    replayed++;
                    break;
                default:
                    unknown++;
                    break;
            }
        }

        await db.SaveChangesAsync(ct);
        if (applied + pending + replayed == 0 && problems.Count > 0)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Not an acknowledgement", detail: string.Join(" ", problems.Take(5)));
        }

        return TypedResults.Ok(new CallbackResponse(applied, pending, unknown, problems, replayed));
    }

    /// <summary>
    /// The signed timestamp and signature of an authenticated <c>hmacSha256</c> callback, which order the callbacks of a
    /// reference (a replay inside the window, or a callback older than the last applied, is not applied). A bearer
    /// callback has none: its token is the whole credential, so it is applied as it comes (docs/11 §13.8).
    /// </summary>
    private static CallbackStamp? CallbackStampOf(string auth, HttpRequest request)
    {
        if (auth == CallbackAuthTypes.Bearer
            || !long.TryParse(request.Headers[TimestampHeader].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        var signature = request.Headers[SignatureHeader].ToString();
        return new CallbackStamp(DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(seconds, 0, 253402300799)), signature.StartsWith("sha256=", StringComparison.Ordinal) ? signature["sha256=".Length..].ToLowerInvariant() : signature);
    }

    /// <summary>
    /// The callback's credential against each active secret, in constant time: <c>hmacSha256</c> needs a timestamp
    /// (Unix seconds) within <see cref="SignatureWindow"/> and <c>X-DocProc-Signature: sha256=&lt;hex&gt;</c> of
    /// <c>"&lt;timestamp&gt;.&lt;body&gt;"</c>; <c>bearer</c> needs <c>Authorization: Bearer &lt;secret&gt;</c>.
    /// </summary>
    internal static bool Authenticated(string auth, HttpRequest request, byte[] body, IReadOnlyList<string> secrets, DateTimeOffset now)
    {
        if (secrets.Count == 0)
        {
            return false;
        }

        if (auth == CallbackAuthTypes.Bearer)
        {
            var header = request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                return false;
            }

            var presented = Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim());
            return secrets.Any(s => CryptographicOperations.FixedTimeEquals(presented, Encoding.UTF8.GetBytes(s)));
        }

        var timestamp = request.Headers[TimestampHeader].ToString();
        var signature = request.Headers[SignatureHeader].ToString();
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || Math.Abs((now - DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(seconds, 0, 253402300799))).TotalSeconds) > SignatureWindow.TotalSeconds
            || !signature.StartsWith("sha256=", StringComparison.Ordinal))
        {
            return false;
        }

        byte[] given;
        try
        {
            given = Convert.FromHexString(signature["sha256=".Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        return secrets.Any(s => CryptographicOperations.FixedTimeEquals(given, Sign(s, timestamp, body)));
    }

    /// <summary>The HMAC-SHA256 a receiver sends: over the timestamp, a dot and the raw body.</summary>
    public static byte[] Sign(string secret, string timestamp, byte[] body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentNullException.ThrowIfNull(body);
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var data = new byte[prefix.Length + body.Length];
        prefix.CopyTo(data, 0);
        body.CopyTo(data, prefix.Length);
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data);
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxCallbackBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task<Results<Ok<DestinationResponse>, ValidationProblem, ProblemHttpResult>> SetAcknowledgementsAsync(
        Guid id,
        JsonElement body,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IOutputRenderer renderer,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await OutputMappingEndpoints.GuardAsync(tenant, features, ct, "Destinations") is { } problem)
        {
            return problem;
        }

        var destination = await db.OutputDestinations.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (destination is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Destination not found", detail: $"The tenant has no destination {id}.");
        }

        if (body.ValueKind != JsonValueKind.Object)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["(body)"] = ["The acknowledgements are a JSON object {callback?, statusPoll?}."] });
        }

        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var config = AcknowledgementConfig.Parse(JsonNode.Parse(body.GetRawText())!.AsObject(), errors);
        if (config?.StatusPoll is not null && destination.Transport != OutputTransports.Http)
        {
            errors["statusPoll"] = "Only an http destination is polled (its headers and authentication are used).";
        }
        else if (config?.StatusPoll is { } poll && !SameHost(poll.UrlTemplate, destination.ConfigJson))
        {
            // The poll sends the destination's credentials, so it goes to the destination's own host only.
            errors["statusPoll.urlTemplate"] = "must name the host of the destination's urlTemplate (the poll sends the destination's credentials).";
        }

        foreach (var (field, expression) in new[] { ("callback.mapping", config?.Callback?.Mapping), ("statusPoll.mapping", config?.StatusPoll?.Mapping) })
        {
            if (expression is not { Length: > 0 })
            {
                continue;
            }

            var check = await renderer.CheckAsync(expression, null, ct);
            if (check.Error is { Kind: RenderErrorKinds.Unavailable } unavailable)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Renderer unavailable", detail: unavailable.Message);
            }

            if (check.Error is { } error)
            {
                errors[field] = $"is not a JSONata expression ({error.Kind}): {error.Message}";
            }
        }

        if (errors.Count > 0 || config is null)
        {
            return TypedResults.ValidationProblem(errors.ToDictionary(e => e.Key, e => new[] { e.Value }, StringComparer.Ordinal));
        }

        destination.CallbackConfigJson = config.Any ? config.ToJson().ToJsonString() : null;
        destination.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(DestinationResponse.From(destination));
    }

    private static async Task<Results<Ok<DestinationResponse>, ValidationProblem, ProblemHttpResult>> SetCallbackSecretAsync(
        Guid id,
        SetCallbackSecretRequest request,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] ISecretStore secrets,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await OutputMappingEndpoints.GuardAsync(tenant, features, ct, "Destinations") is { } problem)
        {
            return problem;
        }

        var destination = await db.OutputDestinations.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (destination is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Destination not found", detail: $"The tenant has no destination {id}.");
        }

        if (string.IsNullOrEmpty(request?.Secret) || request.Secret.Length is < 16 or > DestinationEndpoints.MaxSecretLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["secret"] = [$"secret is 16 to {DestinationEndpoints.MaxSecretLength} characters."] });
        }

        // Two slots alternate, so the replaced secret stays accepted while the receiver moves over.
        var slot = destination.CallbackSecretRef == CallbackSecretReference(destination.TenantId, destination.Id, 1) ? 2 : 1;
        var reference = CallbackSecretReference(destination.TenantId, destination.Id, slot);
        await secrets.SetAsync(reference, request.Secret, ct);
        destination.PreviousCallbackSecretRef = destination.CallbackSecretRef is null ? null : CallbackSecretReference(destination.TenantId, destination.Id, slot == 1 ? 2 : 1);
        destination.CallbackSecretRef = reference;
        destination.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(DestinationResponse.From(destination));
    }

    private static async Task<Results<Ok<DocumentDetail>, ValidationProblem, ProblemHttpResult>> ResolveIssueAsync(
        Guid id,
        ResolveIntegrationIssueRequest request,
        [FromServices] DocProcDbContext db,
        TimeProvider clock,
        ClaimsPrincipal user,
        [FromServices] IEffectiveConfigResolver configs,
        [FromServices] ITenantFeatures features,
        CancellationToken ct)
    {
        var note = request?.Note?.Trim();
        if (string.IsNullOrEmpty(note))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["note"] = ["A note is required."] });
        }

        note = note.Length <= 2000 ? note : note[..2000];
        var document = await db.Documents.Include(d => d.Flags).SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Document not found", detail: $"No document {id} is visible in the current scope.");
        }

        var open = document.Flags.Where(f => f.Code == FlagCodes.ExternalRejected && f.ResolvedAt is null).ToList();
        if (open.Count == 0)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "No integration issue", detail: $"Document {id} has no open EXTERNAL_REJECTED.");
        }

        var now = clock.GetUtcNow();
        var actor = DocumentQueries.ActorName(user);
        foreach (var flag in open)
        {
            flag.ResolvedAt = now;
            flag.ResolvedBy = actor;
        }

        await ExternalAcknowledgements.CloseIssuesAsync(db, id, now, ct);
        DocumentQueries.AddEvent(db, document, DocumentEventTypes.IntegrationIssueResolved, null, actor, new { note, flags = open.Count }, now);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await DocumentEndpoints.FreshDetailAsync(db, id, configs, features, ct));
    }
}
