using DocProc.Api.Auth;
using DocProc.Application.Security;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Api.Endpoints;

public sealed record ApiKeySummary(Guid Id, string Name, string Prefix, IReadOnlyList<string> Scopes, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt)
{
    public bool IsActive => RevokedAt is null;
}

public sealed record CreateApiKeyRequest(string Name, IReadOnlyList<string> Scopes);

/// <summary>Returned once, on creation: <see cref="Key"/> is never retrievable again.</summary>
public sealed record CreatedApiKey(Guid Id, string Name, string Prefix, IReadOnlyList<string> Scopes, string Key, DateTimeOffset CreatedAt);

/// <summary>
/// API keys of the current tenant (docs/05: TenantAdmin manages API keys). The tenant is the resolved scope, so a
/// SuperAdmin manages another tenant's keys by sending X-Tenant-Id; platform scope without a tenant is refused.
/// </summary>
public static class ApiKeyEndpoints
{
    public static RouteGroupBuilder MapApiKeys(this RouteGroupBuilder group)
    {
        var keys = group.MapGroup("/api-keys").RequireAuthorization(Policies.TenantAdmin).WithTags("API keys");

        keys.MapGet("/", async Task<Results<Ok<IReadOnlyList<ApiKeySummary>>, ProblemHttpResult>> ([FromServices] DocProcDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (RequireTenant(tenant) is { } problem)
            {
                return problem;
            }

            var rows = await db.ApiKeys.AsNoTracking().ToListAsync(ct);
            var list = rows
                .OrderByDescending(k => k.CreatedAt)
                .Select(k => new ApiKeySummary(k.Id, k.Name, k.Prefix, k.Scopes.Select(s => s.ToString().ToLowerInvariant()).ToList(), k.CreatedAt, k.LastUsedAt, k.RevokedAt))
                .ToList();
            return TypedResults.Ok<IReadOnlyList<ApiKeySummary>>(list);
        })
        .WithName("ListApiKeys")
        .WithSummary("API keys of the current tenant; secrets are never returned.");

        keys.MapPost("/", async Task<Results<Created<CreatedApiKey>, ValidationProblem, ProblemHttpResult>> (CreateApiKeyRequest request, [FromServices] DocProcDbContext db, ITenantContext tenant, TimeProvider clock, CancellationToken ct) =>
        {
            if (RequireTenant(tenant) is { } problem)
            {
                return problem;
            }

            var errors = Validate(request);
            if (errors.Count > 0)
            {
                return TypedResults.ValidationProblem(errors);
            }

            var generated = ApiKeys.Generate();
            var now = clock.GetUtcNow();
            var entity = new ApiKey
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.TenantId!.Value,
                Name = request.Name.Trim(),
                Prefix = generated.Prefix,
                Hash = generated.Hash,
                Scopes = request.Scopes.Select(s => Enum.Parse<ApiKeyScope>(s, ignoreCase: true)).Distinct().ToList(),
                CreatedAt = now,
            };
            db.ApiKeys.Add(entity);
            await db.SaveChangesAsync(ct);

            var scopes = entity.Scopes.Select(s => s.ToString().ToLowerInvariant()).ToList();
            return TypedResults.Created($"/api/v1/api-keys/{entity.Id}", new CreatedApiKey(entity.Id, entity.Name, entity.Prefix, scopes, generated.Key, now));
        })
        .WithName("CreateApiKey")
        .WithSummary("Creates a key for the current tenant. The full key is returned once; store it now.");

        keys.MapDelete("/{id:guid}", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (Guid id, [FromServices] DocProcDbContext db, ITenantContext tenant, TimeProvider clock, CancellationToken ct) =>
        {
            if (RequireTenant(tenant) is { } problem)
            {
                return problem;
            }

            var key = await db.ApiKeys.SingleOrDefaultAsync(k => k.Id == id, ct);
            if (key is null)
            {
                return TypedResults.NotFound();
            }

            if (key.RevokedAt is null)
            {
                key.RevokedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
            }

            return TypedResults.NoContent();
        })
        .WithName("RevokeApiKey")
        .WithSummary("Revokes a key. Idempotent; revoked keys stay listed for audit.");

        return group;
    }

    private static ProblemHttpResult? RequireTenant(ITenantContext tenant) =>
        tenant.TenantId is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Tenant required", detail: "API keys belong to a tenant. Platform users must send X-Tenant-Id.")
            : null;

    private static Dictionary<string, string[]> Validate(CreateApiKeyRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
        {
            errors["name"] = ["Name is required and at most 100 characters."];
        }

        if (request.Scopes is null || request.Scopes.Count == 0)
        {
            errors["scopes"] = [$"At least one scope is required: {string.Join(", ", ApiKeyScopes.All)}."];
        }
        else if (request.Scopes.Any(s => !ApiKeyScopes.IsKnown(s)))
        {
            errors["scopes"] = [$"Unknown scope. Allowed: {string.Join(", ", ApiKeyScopes.All)}."];
        }

        return errors;
    }
}
