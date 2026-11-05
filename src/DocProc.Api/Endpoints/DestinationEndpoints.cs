using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Outputs;
using DocProc.Infrastructure.Outputs;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

/// <summary>
/// A destination as the API shows it. The secret is write-only: <see cref="HasSecret"/> and <see cref="SecretSetAt"/>
/// are all a client learns, <see cref="HasPreviousSecret"/> whether the one it replaced is still kept for the overlap.
/// </summary>
public sealed record DestinationResponse(
    Guid Id,
    string Name,
    string Transport,
    JsonElement Config,
    bool HasSecret,
    DateTimeOffset? SecretSetAt,
    bool HasPreviousSecret,
    string Status,
    DateTimeOffset? LastTestAt,
    string? LastTestResult,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static DestinationResponse From(OutputDestination d)
    {
        using var config = JsonDocument.Parse(d.ConfigJson);
        JsonElement? acknowledgements = null;
        if (!string.IsNullOrWhiteSpace(d.CallbackConfigJson))
        {
            using var ack = JsonDocument.Parse(d.CallbackConfigJson);
            acknowledgements = ack.RootElement.Clone();
        }

        return new(d.Id, d.Name, d.Transport, config.RootElement.Clone(), d.SecretRef is not null, d.SecretSetAt, d.PreviousSecretRef is not null, d.Status.ToString(), d.LastTestAt, d.LastTestResult, d.CreatedAt, d.UpdatedAt)
        {
            Acknowledgements = acknowledgements,
            HasCallbackSecret = d.CallbackSecretRef is not null,
            HasPreviousCallbackSecret = d.PreviousCallbackSecretRef is not null,
        };
    }

    /// <summary>The destination's acknowledgements (docs/11 §13.8: <c>{callback?, statusPoll?}</c>); null when it reports nothing back.</summary>
    public JsonElement? Acknowledgements { get; init; }

    /// <summary>Whether a callback secret is set (it is never returned).</summary>
    public bool HasCallbackSecret { get; init; }

    /// <summary>Whether the previous callback secret is still accepted (the overlap of a rotation).</summary>
    public bool HasPreviousCallbackSecret { get; init; }
}

/// <summary><c>POST /destinations</c>: a name, a transport (<c>http</c>, <c>blob</c>, <c>pull</c>), its config and, write-only, the secret.</summary>
public sealed record CreateDestinationRequest(string Name, string Transport, JsonElement? Config, string? Secret);

/// <summary>
/// Every field optional. A config replaces the stored one; <see cref="Status"/> is Active or Disabled. A config that
/// would send the stored secret somewhere else (another host of a URL or of the token URL, another auth type or header
/// name) needs <c>secret</c> in the same request (400 under <c>secret</c> otherwise), which then replaces the stored
/// secret and drops the previous one; without such a change <c>secret</c> rotates the secret as
/// <c>PUT /destinations/{id}/secret</c> does.
/// </summary>
public sealed record PatchDestinationRequest(string? Name, string? Status, JsonElement? Config, string? Secret = null);

/// <summary><c>PUT /destinations/{id}/secret</c>: the new secret; the one it replaces is kept as the previous secret for the overlap of a rotation (docs/11 §16).</summary>
public sealed record SetDestinationSecretRequest(string Secret);

/// <summary>
/// Destinations (docs/11 §6.7, §13.3): where a tenant's outputs go. The transports of the first release are
/// <c>http</c>, <c>blob</c> and <c>pull</c> (decision 4 of §24); the config is checked per transport and never holds a
/// credential: the secret goes to <see cref="ISecretStore"/> under <c>tenant/{tenantId}/destination/{id}/{n}</c>, the
/// row keeps the reference. Everything needs the <c>outputs</c> switch and <see cref="Policies.IntegrationAdmin"/>, reads
/// too (a destination's URL and headers are the tenant's integration details). <c>:test</c> comes with the transports
/// (P3 part B).
/// </summary>
public static class DestinationEndpoints
{
    private const string What = "Destinations";

    public const int MaxSecretLength = 8192;

    /// <summary>The most destinations a tenant has (disabled ones count).</summary>
    public const int MaxDestinationsPerTenant = 25;

    public static RouteGroupBuilder MapDestinations(this RouteGroupBuilder group)
    {
        var destinations = group.MapGroup("/destinations").WithTags("Outputs");
        destinations.MapGet("/", ListAsync)
            .RequireAuthorization(Policies.IntegrationAdmin)
            .WithName("ListDestinations")
            .WithSummary("The tenant's destinations; secrets are never returned.");
        destinations.MapPost("/", CreateAsync)
            .RequireAuthorization(Policies.IntegrationAdmin)
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024))
            .WithName("CreateDestination")
            .WithSummary("Adds an http, blob or pull destination; the config is checked per transport, the secret is write-only.");
        destinations.MapGet("/{id:guid}", GetAsync)
            .RequireAuthorization(Policies.IntegrationAdmin)
            .WithName("GetDestination")
            .WithSummary("One destination.");
        destinations.MapPatch("/{id:guid}", PatchAsync)
            .RequireAuthorization(Policies.IntegrationAdmin)
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024))
            .WithName("UpdateDestination")
            .WithSummary("Renames, disables or reconfigures a destination.");
        destinations.MapPut("/{id:guid}/secret", SetSecretAsync)
            .RequireAuthorization(Policies.IntegrationAdmin)
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .WithName("SetDestinationSecret")
            .WithSummary("Sets or rotates the destination's secret; the replaced secret stays as the previous one until the next rotation.");
        destinations.MapDelete("/{id:guid}", DeleteAsync)
            .RequireAuthorization(Policies.IntegrationAdmin)
            .WithName("DeleteDestination")
            .WithSummary("Removes a destination no binding and no delivery refers to (409 otherwise: disable it instead), with its secrets.");
        return group;
    }

    private static async Task<Results<Ok<IReadOnlyList<DestinationResponse>>, ProblemHttpResult>> ListAsync(ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await OutputMappingEndpoints.GuardAsync(tenant, features, ct, What) is { } problem)
        {
            return problem;
        }

        var rows = await db.OutputDestinations.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
        return TypedResults.Ok<IReadOnlyList<DestinationResponse>>([.. rows.Select(DestinationResponse.From)]);
    }

    private static async Task<Results<Ok<DestinationResponse>, ProblemHttpResult>> GetAsync(Guid id, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await OutputMappingEndpoints.GuardAsync(tenant, features, ct, What) is { } problem)
        {
            return problem;
        }

        var row = await db.OutputDestinations.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        return row is null ? NotFound(id) : TypedResults.Ok(DestinationResponse.From(row));
    }

    private static async Task<Results<Created<DestinationResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateDestinationRequest request,
        ITenantContext tenant,
        ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] ISecretStore secrets,
        [FromServices] IOutputRenderer renderer,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await OutputMappingEndpoints.GuardAsync(tenant, features, ct, What) is { } problem)
        {
            return problem;
        }

        var errors = new Errors().Require("name", request.Name, 200);
        var config = ConfigOf(errors, request.Config);
        var transport = request.Transport?.Trim().ToLowerInvariant() ?? string.Empty;
        if (config is not null)
        {
            foreach (var (field, message) in DestinationConfig.Validate(transport, config))
            {
                errors.Add(field, message);
            }

            if (await CheckResponseMappingAsync(errors, config, renderer, ct) is { } unavailable)
            {
                return unavailable;
            }
        }

        ValidateSecret(errors, request.Secret, config is null ? HttpAuthTypes.None : DestinationConfig.AuthTypeOf(config));
        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        var tenantId = tenant.TenantId!.Value;
        var name = request.Name.Trim();
        if (await db.OutputDestinations.AnyAsync(d => d.Name == name, ct))
        {
            return Conflict("Destination exists", $"The tenant already has a destination named '{name}'.");
        }

        if (await db.OutputDestinations.CountAsync(ct) >= MaxDestinationsPerTenant)
        {
            return Conflict("Too many destinations", $"A tenant has at most {MaxDestinationsPerTenant} destinations. Remove one that no binding uses, or reconfigure one.");
        }

        var now = clock.GetUtcNow();
        var destination = new OutputDestination
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = name,
            Transport = transport,
            ConfigJson = config!.ToJsonString(),
            Status = OutputDestinationStatus.Active,
            CreatedAt = now,
            CreatedBy = DocumentQueries.ActorName(user),
            UpdatedAt = now,
        };

        // The secret first, the row second (as channels do): a failed save leaves an unreferenced secret, never a
        // destination whose reference points at nothing.
        if (!string.IsNullOrEmpty(request.Secret))
        {
            destination.SecretRef = SecretReference(tenantId, destination.Id, 1);
            destination.SecretSetAt = now;
            await secrets.SetAsync(destination.SecretRef, request.Secret, ct);
        }

        db.OutputDestinations.Add(destination);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/destinations/{destination.Id}", DestinationResponse.From(destination));
    }

    private static async Task<Results<Ok<DestinationResponse>, ValidationProblem, ProblemHttpResult>> PatchAsync(
        Guid id,
        PatchDestinationRequest request,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IOutputRenderer renderer,
        [FromServices] ISecretStore secrets,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await OutputMappingEndpoints.GuardAsync(tenant, features, ct, What) is { } problem)
        {
            return problem;
        }

        var destination = await db.OutputDestinations.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (destination is null)
        {
            return NotFound(id);
        }

        var errors = new Errors().Optional("name", request.Name, 200);
        if (request.Name is not null && string.IsNullOrWhiteSpace(request.Name))
        {
            errors.Add("name", "name is not empty.");
        }

        OutputDestinationStatus? status = null;
        if (request.Status is not null)
        {
            if (TryParseEnum<OutputDestinationStatus>(request.Status, out var parsed))
            {
                status = parsed;
            }
            else
            {
                errors.Add("status", $"status is one of {Names<OutputDestinationStatus>()}.");
            }
        }

        JsonObject? config = null;
        if (request.Config is not null)
        {
            config = ConfigOf(errors, request.Config);
            if (config is not null)
            {
                foreach (var (field, message) in DestinationConfig.Validate(destination.Transport, config))
                {
                    errors.Add(field, message);
                }

                if (await CheckResponseMappingAsync(errors, config, renderer, ct) is { } unavailable)
                {
                    return unavailable;
                }
            }
        }

        var current = JsonNode.Parse(destination.ConfigJson) as JsonObject ?? [];
        var next = config ?? current;
        var redirects = destination.Transport == OutputTransports.Http && destination.SecretRef is not null && config is not null
            ? DestinationConfig.SecretRedirects(current, config)
            : [];
        // The status poll sends the destination's headers and credentials to its urlTemplate, which must stay on the
        // destination's origin (docs/11 §13.8: scheme, host and port): a config that moves the destination elsewhere while
        // a poll names the old origin is refused, so the new place's secret never reaches the old one. Turn the poll off
        // (or set it again) first.
        if (config is not null
            && DocProc.Infrastructure.Outputs.Acknowledgements.AcknowledgementConfig.Of(destination.CallbackConfigJson).StatusPoll is { } poll
            && !DocProc.Infrastructure.Outputs.Acknowledgements.AcknowledgementConfig.SameOrigin(poll.UrlTemplate, config.ToJsonString()))
        {
            errors.Add("config", $"The destination's status poll asks {poll.UrlTemplate}, which would no longer be the destination's origin (scheme, host and port) and would get its credentials: turn the status poll off (PUT acknowledgements) before moving the destination, then set it again for the new address.");
        }

        if (redirects.Count > 0 && string.IsNullOrEmpty(request.Secret))
        {
            errors.Add("secret", $"This change sends the stored secret somewhere else ({string.Join(", ", redirects)} changed): give the secret again in the same request (secret).");
        }

        if (request.Secret is not null)
        {
            if (request.Secret.Length == 0)
            {
                errors.Add("secret", "secret is not empty when given (leave it out to keep the stored one).");
            }
            else
            {
                ValidateSecret(errors, request.Secret, DestinationConfig.AuthTypeOf(next));
            }
        }

        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        if (request.Name is { } newName && newName.Trim() != destination.Name && await db.OutputDestinations.AnyAsync(d => d.Id != id && d.Name == newName.Trim(), ct))
        {
            return Conflict("Destination exists", $"The tenant already has a destination named '{newName.Trim()}'.");
        }

        destination.Name = request.Name?.Trim() ?? destination.Name;
        destination.Status = status ?? destination.Status;
        destination.ConfigJson = config?.ToJsonString() ?? destination.ConfigJson;
        destination.UpdatedAt = clock.GetUtcNow();
        string? dropped = null;
        if (!string.IsNullOrEmpty(request.Secret))
        {
            // A redirect drops the previous secret too: it was set up for the old receiver as much as the current one.
            dropped = await StoreSecretAsync(destination, request.Secret, keepPrevious: redirects.Count == 0, secrets, destination.UpdatedAt, ct);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict("Destination changed", "The destination changed while it was saved; read it again.");
        }

        if (dropped is not null)
        {
            await secrets.DeleteAsync(dropped, ct);
        }

        return TypedResults.Ok(DestinationResponse.From(destination));
    }

    /// <summary>
    /// Writes <paramref name="secret"/> to the other of the destination's two slots (…/1, …/2) and points the row at it.
    /// With <paramref name="keepPrevious"/> the replaced secret stays as the previous one for the overlap of a rotation
    /// (the one before it is overwritten); without, no previous secret is kept and the caller deletes the reference
    /// returned once the row is saved. References are computed, never taken from the row.
    /// </summary>
    private static async Task<string?> StoreSecretAsync(OutputDestination destination, string secret, bool keepPrevious, ISecretStore secrets, DateTimeOffset now, CancellationToken ct)
    {
        var slot = destination.SecretRef == SecretReference(destination.TenantId, destination.Id, 1) ? 2 : 1;
        var reference = SecretReference(destination.TenantId, destination.Id, slot);
        var other = SecretReference(destination.TenantId, destination.Id, slot == 1 ? 2 : 1);
        await secrets.SetAsync(reference, secret, ct);
        destination.PreviousSecretRef = keepPrevious && destination.SecretRef is not null ? other : null;
        destination.SecretRef = reference;
        destination.SecretSetAt = now;
        destination.UpdatedAt = now;
        return keepPrevious ? null : other;
    }

    /// <summary>
    /// Compiles an http destination's <c>responseMapping</c> in the renderer, as a binding's condition is: one that does
    /// not compile (or nests too deeply) is refused now rather than failing after every delivery. Answers 503 when the
    /// renderer is unavailable.
    /// </summary>
    private static async Task<ProblemHttpResult?> CheckResponseMappingAsync(Errors errors, JsonObject config, IOutputRenderer renderer, CancellationToken ct)
    {
        if (errors.Has("config.responseMapping") || config["responseMapping"] is not JsonValue value || !value.TryGetValue<string>(out var expression) || string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        var check = await renderer.CheckAsync(expression, null, ct);
        if (check.Error is { Kind: RenderErrorKinds.Unavailable } unavailable)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Renderer unavailable", detail: unavailable.Message);
        }

        if (check.Error is { } error)
        {
            errors.Add("config.responseMapping", $"responseMapping is not a JSONata expression ({error.Kind}): {error.Message}");
        }

        return null;
    }

    private static async Task<Results<Ok<DestinationResponse>, ValidationProblem, ProblemHttpResult>> SetSecretAsync(
        Guid id,
        SetDestinationSecretRequest request,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] ISecretStore secrets,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await OutputMappingEndpoints.GuardAsync(tenant, features, ct, What) is { } problem)
        {
            return problem;
        }

        var destination = await db.OutputDestinations.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (destination is null)
        {
            return NotFound(id);
        }

        var errors = new Errors();
        if (string.IsNullOrEmpty(request.Secret))
        {
            errors.Add("secret", "secret is required.");
        }
        else
        {
            ValidateSecret(errors, request.Secret, destination.Transport == OutputTransports.Http ? DestinationConfig.AuthTypeOf(JsonNode.Parse(destination.ConfigJson) as JsonObject ?? []) : HttpAuthTypes.None);
        }

        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        // Two slots alternate (…/1, …/2), so the replaced secret stays readable while a receiver moves over; the one
        // before it is overwritten.
        await StoreSecretAsync(destination, request.Secret, keepPrevious: true, secrets, clock.GetUtcNow(), ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(DestinationResponse.From(destination));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] ISecretStore secrets,
        CancellationToken ct)
    {
        if (await OutputMappingEndpoints.GuardAsync(tenant, features, ct, What) is { } problem)
        {
            return problem;
        }

        var destination = await db.OutputDestinations.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (destination is null)
        {
            return NotFound(id);
        }

        if (await db.OutputBindings.AnyAsync(b => b.DestinationId == id, ct) || await db.OutboxDeliveries.AnyAsync(d => d.DestinationId == id, ct)
            || await db.DocumentExternalRefs.AnyAsync(r => r.DestinationId == id, ct))
        {
            return Conflict("Destination in use", "Bindings or deliveries refer to this destination; remove the bindings, or disable the destination to keep its delivery history.");
        }

        if (await db.MasterDataSources.Where(s => s.DestinationId == id).Select(s => s.Name).FirstOrDefaultAsync(ct) is { } source)
        {
            return Conflict("Destination in use", $"The master-data source '{source}' reads from this destination; remove the source, or point it at another destination, first.");
        }

        db.OutputDestinations.Remove(destination);
        await db.SaveChangesAsync(ct);
        foreach (var slot in new[] { 1, 2 })
        {
            await secrets.DeleteAsync(SecretReference(destination.TenantId, destination.Id, slot), ct);
            await secrets.DeleteAsync(IntegrationEndpoints.CallbackSecretReference(destination.TenantId, destination.Id, slot), ct);
        }

        return TypedResults.NoContent();
    }

    /// <summary>Where a destination's secret lives; computed from the ids, so a row can never point at another tenant's secret.</summary>
    internal static string SecretReference(Guid tenantId, Guid destinationId, int slot) => $"tenant/{tenantId:D}/destination/{destinationId:D}/{slot}";

    private static JsonObject? ConfigOf(Errors errors, JsonElement? config)
    {
        if (config is null or { ValueKind: JsonValueKind.Null })
        {
            return new JsonObject();
        }

        if (config.Value.ValueKind != JsonValueKind.Object)
        {
            errors.Add("config", "config is a JSON object.");
            return null;
        }

        return JsonNode.Parse(config.Value.GetRawText())!.AsObject();
    }

    /// <summary>At most <see cref="MaxSecretLength"/> characters, and what <see cref="DestinationConfig.SecretProblem"/> allows for the auth type it is used with.</summary>
    private static void ValidateSecret(Errors errors, string? secret, string authType)
    {
        if (secret is { Length: > MaxSecretLength })
        {
            errors.Add("secret", $"secret is at most {MaxSecretLength} characters.");
        }
        else if (!string.IsNullOrEmpty(secret) && DestinationConfig.SecretProblem(authType, secret) is { } problem)
        {
            errors.Add("secret", problem);
        }
    }

    private static ProblemHttpResult NotFound(Guid id) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Destination not found", detail: $"The tenant has no destination {id}.");
}
