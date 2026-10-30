using DocProc.Api.Auth;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

public sealed record TenantResponse(
    Guid Id,
    string Name,
    string Slug,
    string LegalName,
    string? VatId,
    IReadOnlyList<string> NameAliases,
    string Status,
    string DefaultCurrency,
    string CountryCode,
    string TimeZone,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static TenantResponse From(Tenant t) =>
        new(t.Id, t.Name, t.Slug, t.LegalName, t.VatId, t.NameAliases, t.Status.ToString(), t.DefaultCurrency, t.CountryCode, t.TimeZone, t.CreatedAt, t.UpdatedAt);
}

public sealed record CreateTenantRequest(string Name, string Slug, string LegalName, string? VatId, string DefaultCurrency, string CountryCode, string TimeZone);

/// <summary>Every field optional; absent fields are left as they are. <see cref="VatId"/> set to an empty string clears it.</summary>
public sealed record PatchTenantRequest(string? Name, string? LegalName, string? VatId, IReadOnlyList<string>? NameAliases, string? Status);

/// <summary>
/// Platform management of tenants (docs/05 "Resource model": <c>/tenants</c> is SuperAdmin; Operators may list and read).
/// Every handler runs on a platform-scoped context from <see cref="ISystemDbContextFactory"/>, never on the request
/// context, so a SuperAdmin needs no X-Tenant-Id here; rows written for the new tenant carry its id explicitly.
/// </summary>
public static class TenantEndpoints
{
    public static RouteGroupBuilder MapTenants(this RouteGroupBuilder group)
    {
        var tenants = group.MapGroup("/tenants").WithTags("Tenants");

        tenants.MapGet("/", async Task<Results<Ok<Page<TenantResponse>>, ValidationProblem>> (string? status, string? q, string? cursor, int? limit, [FromServices] ISystemDbContextFactory factory, CancellationToken ct) =>
        {
            TenantStatus? statusFilter = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!TryParseEnum<TenantStatus>(status, out var parsed))
                {
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["status"] = [$"Unknown status. Allowed: {Names<TenantStatus>()}."] });
                }

                statusFilter = parsed;
            }

            var after = DecodeCursor(cursor, 1);
            if (after is { Length: 0 })
            {
                return InvalidCursor();
            }

            var take = ClampLimit(limit);
            await using var db = factory.CreatePlatformScoped();
            var query = db.Tenants.AsNoTracking();
            if (statusFilter is { } s)
            {
                query = query.Where(t => t.Status == s);
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                var pattern = LikeContains(q.Trim());
                query = query.Where(t => EF.Functions.Like(t.Name, pattern, "\\") || EF.Functions.Like(t.Slug, pattern, "\\") || EF.Functions.Like(t.LegalName, pattern, "\\"));
            }

            if (after is not null)
            {
                var afterSlug = after[0];
                query = query.Where(t => t.Slug.CompareTo(afterSlug) > 0);
            }

            var rows = await query.OrderBy(t => t.Slug).Take(take + 1).ToListAsync(ct);
            var items = rows.Take(take).Select(TenantResponse.From).ToList();
            var next = rows.Count > take ? EncodeCursor(items[^1].Slug) : null;
            return TypedResults.Ok(new Page<TenantResponse>(items, next));
        })
        .RequireAuthorization(Policies.Platform)
        .WithName("ListTenants")
        .WithSummary("Tenants of the platform, by slug, with status and text filters. Operators may list; only SuperAdmins change anything.");

        tenants.MapPost("/", async Task<Results<Created<TenantResponse>, ValidationProblem, ProblemHttpResult>> (CreateTenantRequest request, [FromServices] ISystemDbContextFactory factory, TimeProvider clock, CancellationToken ct) =>
        {
            var errors = new Errors()
                .Require("name", request.Name, 200)
                .Require("legalName", request.LegalName, 300)
                .Optional("vatId", request.VatId, 32);
            if (!IsSlug(request.Slug))
            {
                errors.Add("slug", "Slug must be lowercase letters, digits and single dashes, at most 64 characters.");
            }

            if (!IsCurrency(request.DefaultCurrency))
            {
                errors.Add("defaultCurrency", "Currency must be a three-letter upper-case ISO 4217 code.");
            }

            if (!IsCountry(request.CountryCode))
            {
                errors.Add("countryCode", "Country must be a two-letter upper-case ISO 3166-1 code.");
            }

            if (!IsTimeZone(request.TimeZone))
            {
                errors.Add("timeZone", "Time zone must be an IANA id such as Europe/Berlin.");
            }

            if (errors.ToProblem() is { } problem)
            {
                return problem;
            }

            await using var db = factory.CreatePlatformScoped();
            if (await db.Tenants.AnyAsync(t => t.Slug == request.Slug, ct))
            {
                return Conflict("Slug taken", $"A tenant with slug '{request.Slug}' already exists.");
            }

            var now = clock.GetUtcNow();
            var tenant = new Tenant
            {
                Id = Guid.CreateVersion7(),
                Name = request.Name.Trim(),
                Slug = request.Slug,
                LegalName = request.LegalName.Trim(),
                VatId = TrimToNull(request.VatId),
                DefaultCurrency = request.DefaultCurrency,
                CountryCode = request.CountryCode,
                TimeZone = request.TimeZone,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Tenants.Add(tenant);

            // Every tenant can receive documents from the start: API uploads for integrations, portal uploads for people.
            db.IngestionChannels.Add(DefaultChannel(tenant.Id, "API upload", ChannelType.ApiUpload, now));
            db.IngestionChannels.Add(DefaultChannel(tenant.Id, "Portal upload", ChannelType.PortalUpload, now));
            await db.SaveChangesAsync(ct);

            return TypedResults.Created($"/api/v1/tenants/{tenant.Id}", TenantResponse.From(tenant));
        })
        .RequireAuthorization(Policies.PlatformAdmin)
        .WithName("CreateTenant")
        .WithSummary("Creates a tenant with its default API-upload and portal-upload channels.");

        tenants.MapGet("/{id:guid}", async Task<Results<Ok<TenantResponse>, NotFound>> (Guid id, [FromServices] ISystemDbContextFactory factory, CancellationToken ct) =>
        {
            await using var db = factory.CreatePlatformScoped();
            var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct);
            return tenant is null ? TypedResults.NotFound() : TypedResults.Ok(TenantResponse.From(tenant));
        })
        .RequireAuthorization(Policies.Platform)
        .WithName("GetTenant")
        .WithSummary("One tenant.");

        tenants.MapPatch("/{id:guid}", async Task<Results<Ok<TenantResponse>, NotFound, ValidationProblem>> (Guid id, PatchTenantRequest request, [FromServices] ISystemDbContextFactory factory, TimeProvider clock, CancellationToken ct) =>
        {
            var errors = new Errors().Optional("name", request.Name, 200).Optional("legalName", request.LegalName, 300).Optional("vatId", request.VatId, 32);
            if (request.Name is not null && string.IsNullOrWhiteSpace(request.Name))
            {
                errors.Add("name", "name cannot be blank.");
            }

            if (request.LegalName is not null && string.IsNullOrWhiteSpace(request.LegalName))
            {
                errors.Add("legalName", "legalName cannot be blank.");
            }

            TenantStatus? status = null;
            if (request.Status is not null)
            {
                if (!TryParseEnum<TenantStatus>(request.Status, out var parsed))
                {
                    errors.Add("status", $"Unknown status. Allowed: {Names<TenantStatus>()}.");
                }

                status = parsed;
            }

            if (errors.ToProblem() is { } problem)
            {
                return problem;
            }

            await using var db = factory.CreatePlatformScoped();
            var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == id, ct);
            if (tenant is null)
            {
                return TypedResults.NotFound();
            }

            if (request.Name is not null)
            {
                tenant.Name = request.Name.Trim();
            }

            if (request.LegalName is not null)
            {
                tenant.LegalName = request.LegalName.Trim();
            }

            if (request.VatId is not null)
            {
                tenant.VatId = TrimToNull(request.VatId);
            }

            if (request.NameAliases is not null)
            {
                tenant.NameAliases = CleanList(request.NameAliases);
            }

            if (status is { } newStatus)
            {
                tenant.Status = newStatus;
            }

            tenant.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(TenantResponse.From(tenant));
        })
        .RequireAuthorization(Policies.PlatformAdmin)
        .WithName("UpdateTenant")
        .WithSummary("Changes name, legal name, VAT id, aliases or status of a tenant.");

        tenants.MapPost("/{id:guid}:suspend", (Guid id, [FromServices] ISystemDbContextFactory factory, TimeProvider clock, CancellationToken ct) =>
                SetStatusAsync(id, TenantStatus.Suspended, factory, clock, ct))
            .RequireAuthorization(Policies.PlatformAdmin)
            .WithName("SuspendTenant")
            .WithSummary("Suspends a tenant: its users and API keys stop working until it is activated again.");

        tenants.MapPost("/{id:guid}:activate", (Guid id, [FromServices] ISystemDbContextFactory factory, TimeProvider clock, CancellationToken ct) =>
                SetStatusAsync(id, TenantStatus.Active, factory, clock, ct))
            .RequireAuthorization(Policies.PlatformAdmin)
            .WithName("ActivateTenant")
            .WithSummary("Activates a suspended tenant.");

        return group;
    }

    private static async Task<Results<Ok<TenantResponse>, NotFound>> SetStatusAsync(Guid id, TenantStatus status, ISystemDbContextFactory factory, TimeProvider clock, CancellationToken ct)
    {
        await using var db = factory.CreatePlatformScoped();
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null)
        {
            return TypedResults.NotFound();
        }

        if (tenant.Status != status)
        {
            tenant.Status = status;
            tenant.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }

        return TypedResults.Ok(TenantResponse.From(tenant));
    }

    private static IngestionChannel DefaultChannel(Guid tenantId, string name, ChannelType type, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Name = name,
        Type = type,
        Status = ChannelStatus.Active,
        CreatedAt = now,
    };
}
