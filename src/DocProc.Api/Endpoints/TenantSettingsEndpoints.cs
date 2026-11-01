using DocProc.Api.Auth;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

/// <summary>The read-only identity of the current tenant; platform staff change it through <c>/tenants/{id}</c>.</summary>
public sealed record TenantIdentity(Guid Id, string Name, string Slug, string LegalName, string? VatId, string DefaultCurrency, string CountryCode, string TimeZone)
{
    internal static TenantIdentity From(Tenant t) => new(t.Id, t.Name, t.Slug, t.LegalName, t.VatId, t.DefaultCurrency, t.CountryCode, t.TimeZone);
}

/// <summary>Review policy and retention (docs/02 §1 "settings"); the body of PUT and the <c>settings</c> part of GET.</summary>
public sealed record TenantSettingsDto(double AutoApproveConfidenceThreshold, IReadOnlyList<Guid> AlwaysReviewSupplierIds, int RetentionYears, bool ShareExtractorProfiles)
{
    internal static TenantSettingsDto From(TenantSettings s) => new(s.AutoApproveConfidenceThreshold, s.AlwaysReviewSupplierIds, s.RetentionYears, s.ShareExtractorProfiles);
}

public sealed record TenantSettingsResponse(TenantIdentity Tenant, TenantSettingsDto Settings);

/// <summary>Review policy, retention and profile sharing of the current tenant (docs/05 "settings: review policy, thresholds, retention").</summary>
public static class TenantSettingsEndpoints
{
    public const int MinRetentionYears = 1;
    public const int MaxRetentionYears = 30;

    public static RouteGroupBuilder MapTenantSettings(this RouteGroupBuilder group)
    {
        var settings = group.MapGroup("/settings").WithTags("Settings");

        settings.MapGet("/", async Task<Results<Ok<TenantSettingsResponse>, ProblemHttpResult>> ([FromServices] DocProcDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Settings") is { } problem)
            {
                return problem;
            }

            var current = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(ct);
            return current is null ? UnknownTenant() : TypedResults.Ok(new TenantSettingsResponse(TenantIdentity.From(current), TenantSettingsDto.From(current.Settings)));
        })
        .RequireAuthorization(Policies.Read)
        .WithName("GetTenantSettings")
        .WithSummary("Review policy and retention of the current tenant, with its read-only identity.");

        settings.MapPut("/", async Task<Results<Ok<TenantSettingsResponse>, ValidationProblem, ProblemHttpResult>> (TenantSettingsDto request, [FromServices] DocProcDbContext db, ITenantContext tenant, TimeProvider clock, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Settings") is { } problem)
            {
                return problem;
            }

            var errors = new Errors();
            if (double.IsNaN(request.AutoApproveConfidenceThreshold) || request.AutoApproveConfidenceThreshold is < 0 or > 1)
            {
                errors.Add("autoApproveConfidenceThreshold", "The threshold must be between 0 and 1.", ValidationCodes.Range, ValidationCodes.Params(("min", 0), ("max", 1)));
            }

            if (request.RetentionYears is < MinRetentionYears or > MaxRetentionYears)
            {
                errors.Add("retentionYears", $"Retention must be between {MinRetentionYears} and {MaxRetentionYears} years.", ValidationCodes.Range, ValidationCodes.Params(("min", MinRetentionYears), ("max", MaxRetentionYears)));
            }

            var supplierIds = (request.AlwaysReviewSupplierIds ?? []).Distinct().ToList();
            if (supplierIds.Count > 0)
            {
                var known = await db.Suppliers.Where(s => supplierIds.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct);
                var unknown = supplierIds.Except(known).ToList();
                if (unknown.Count > 0)
                {
                    errors.Add("alwaysReviewSupplierIds", $"Unknown supplier id(s): {string.Join(", ", unknown)}.");
                }
            }

            if (errors.ToProblem() is { } invalid)
            {
                return invalid;
            }

            var current = await CurrentTenantAsync(db, ct);
            if (current is null)
            {
                return UnknownTenant();
            }

            current.Settings.AutoApproveConfidenceThreshold = request.AutoApproveConfidenceThreshold;
            current.Settings.AlwaysReviewSupplierIds = supplierIds;
            current.Settings.RetentionYears = request.RetentionYears;
            current.Settings.ShareExtractorProfiles = request.ShareExtractorProfiles;
            current.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(new TenantSettingsResponse(TenantIdentity.From(current), TenantSettingsDto.From(current.Settings)));
        })
        .RequireAuthorization(Policies.TenantAdmin)
        .WithName("PutTenantSettings")
        .WithSummary("Replaces the review policy and retention of the current tenant.");

        return group;
    }
}
