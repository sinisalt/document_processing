using System.Security.Claims;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DocProc.Api.Auth;

/// <summary>
/// Derives the request's tenant scope from the validated token (docs/05 "Tenant isolation at the API boundary"):
/// customer users carry a <c>tenant_id</c> claim; platform staff run in platform scope, and a SuperAdmin may act
/// inside one tenant by sending <c>X-Tenant-Id</c>. The result lands in the scoped <see cref="TenantContext"/>
/// that the data layer filters by, so no endpoint ever picks a tenant itself.
/// </summary>
public sealed class TenantResolutionMiddleware(OidcOptions oidc) : IMiddleware
{
    public const string TenantHeader = "X-Tenant-Id";

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var tenant = context.RequestServices.GetRequiredService<TenantContext>();
            var problem = Resolve(user, context.Request.Headers[TenantHeader].FirstOrDefault(), oidc.TenantClaimType, tenant);
            if (problem is not null)
            {
                await problem.ExecuteAsync(context).ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>Pure resolution so it can be unit-tested; returns a problem result when the request must be refused.</summary>
    internal static ProblemHttpResult? Resolve(ClaimsPrincipal user, string? tenantHeader, string tenantClaimType, TenantContext tenant)
    {
        var claimedTenant = user.FindFirst(tenantClaimType)?.Value;
        var isPlatform = Roles.PlatformRoles.Any(user.IsInRole);

        if (!string.IsNullOrWhiteSpace(tenantHeader))
        {
            if (!user.IsInRole(Roles.SuperAdmin))
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Tenant override not allowed", detail: $"Only a SuperAdmin may send {TenantHeader}.");
            }

            if (!Guid.TryParse(tenantHeader, out var overrideId))
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid tenant id", detail: $"{TenantHeader} must be a GUID.");
            }

            tenant.SetTenant(overrideId);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(claimedTenant))
        {
            if (!Guid.TryParse(claimedTenant, out var tenantId))
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Invalid tenant claim", detail: $"The {tenantClaimType} claim is not a GUID.");
            }

            tenant.SetTenant(tenantId);
            return null;
        }

        if (isPlatform)
        {
            tenant.SetPlatformScope();
            return null;
        }

        return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "No tenant", detail: $"The token carries no {tenantClaimType} claim and no platform role.");
    }
}
