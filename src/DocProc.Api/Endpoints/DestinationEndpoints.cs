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
        return new(d.Id, d.Name, d.Transport, config.RootElement.Clone(), d.SecretRef is not null, d.SecretSetAt, d.PreviousSecretRef is not null, d.Status.ToString(), d.LastTestAt, d.LastTestResult, d.CreatedAt, d.UpdatedAt);
    }
}

/// <summary><c>POST /destinations</c>: a name, a transport (<c>http</c>, <c>blob</c>, <c>pull</c>), its config and, write-only, the secret.</summary>
public sealed record CreateDestinationRequest(string Name, string Transport, JsonElement? Config, string? Secret);

/// <summary>Every field optional. A config replaces the stored one; <see cref="Status"/> is Active or Disabled.</summary>
public sealed record PatchDestinationRequest(string? Name, string? Status, JsonElement? Config);

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

        ValidateSecret(errors, request.Secret, "secret");
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
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict("Destination changed", "The destination changed while it was saved; read it again.");
        }

        return TypedResults.Ok(DestinationResponse.From(destination));
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

        var errors = new Errors();
        if (string.IsNullOrEmpty(request.Secret))
        {
            errors.Add("secret", "secret is required.");
        }

        ValidateSecret(errors, request.Secret, "secret");
        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        var destination = await db.OutputDestinations.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (destination is null)
        {
            return NotFound(id);
        }

        // Two slots alternate (…/1, …/2), so the replaced secret stays readable while a receiver moves over; the one
        // before it is deleted. References are computed, never taken from the row.
        var slot = destination.SecretRef == SecretReference(destination.TenantId, destination.Id, 1) ? 2 : 1;
        var reference = SecretReference(destination.TenantId, destination.Id, slot);
        var previous = destination.SecretRef is null ? null : SecretReference(destination.TenantId, destination.Id, slot == 1 ? 2 : 1);
        await secrets.SetAsync(reference, request.Secret, ct);
        destination.PreviousSecretRef = previous;
        destination.SecretRef = reference;
        destination.SecretSetAt = clock.GetUtcNow();
        destination.UpdatedAt = destination.SecretSetAt.Value;
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

        db.OutputDestinations.Remove(destination);
        await db.SaveChangesAsync(ct);
        foreach (var slot in new[] { 1, 2 })
        {
            await secrets.DeleteAsync(SecretReference(destination.TenantId, destination.Id, slot), ct);
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

    private static void ValidateSecret(Errors errors, string? secret, string field)
    {
        if (secret is { Length: > MaxSecretLength })
        {
            errors.Add(field, $"{field} is at most {MaxSecretLength} characters.");
        }
    }

    private static ProblemHttpResult NotFound(Guid id) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Destination not found", detail: $"The tenant has no destination {id}.");
}
