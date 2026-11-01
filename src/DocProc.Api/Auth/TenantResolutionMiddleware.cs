using System.Security.Claims;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DocProc.Api.Auth;

/// <summary>
/// Derives the request's tenant scope from the validated token (docs/05 "Tenant isolation at the API boundary"):
/// customer users carry a <c>tenant_id</c> claim; platform staff run in platform scope, and a SuperAdmin may act
/// inside one tenant by sending <c>X-Tenant-Id</c>. The result lands in the scoped <see cref="TenantContext"/>
/// that the data layer filters by, so no endpoint ever picks a tenant itself. A tenant resolved from a bearer
/// token's claim is then checked against the tenant table (through <see cref="TenantStatusCache"/>): a suspended
/// or closed tenant's users are refused with 403, a tenant that does not exist with 404, so suspension bites for
/// people the way <see cref="ApiKeyAuthenticationHandler"/> already makes it bite for machine keys. Platform scope
/// and the SuperAdmin override are not checked: platform staff must be able to look into a suspended tenant.
/// </summary>
public sealed class TenantResolutionMiddleware(OidcOptions oidc, TenantStatusCache tenantStatus) : IMiddleware
{
    public const string TenantHeader = "X-Tenant-Id";

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var tenant = context.RequestServices.GetRequiredService<TenantContext>();
            var tenantHeader = context.Request.Headers[TenantHeader].FirstOrDefault();
            var problem = Resolve(user, tenantHeader, oidc.TenantClaimType, tenant);
            if (problem is not null)
            {
                await problem.ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            if (ClaimedTenantNeedsStatusCheck(user, tenantHeader, tenant))
            {
                var refused = await CheckStatusAsync(tenant.TenantId!.Value, context.RequestAborted).ConfigureAwait(false);
                if (refused is not null)
                {
                    await refused.ExecuteAsync(context).ConfigureAwait(false);
                    return;
                }
            }
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Only a tenant taken from the token's claim is checked here: platform scope has no tenant, the SuperAdmin
    /// override is platform staff at work, and an API key's tenant was already checked when the key was validated.
    /// An API key is recognised by the authentication scheme that produced the principal, never by a claim: a bearer
    /// token's claims come from the identity provider (or whoever minted the token), so a JWT that merely carries
    /// <c>auth_method=apikey</c> is still a person's token and is still checked.
    /// </summary>
    internal static bool ClaimedTenantNeedsStatusCheck(ClaimsPrincipal user, string? tenantHeader, TenantContext tenant) =>
        tenant.TenantId is not null
        && string.IsNullOrWhiteSpace(tenantHeader)
        && !string.Equals(user.Identity?.AuthenticationType, ApiKeyAuthenticationOptions.SchemeName, StringComparison.Ordinal);

    private async Task<ProblemHttpResult?> CheckStatusAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var status = await tenantStatus.GetStatusAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return status switch
        {
            null => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown tenant", detail: "The tenant the token belongs to does not exist."),
            TenantStatus.Active => null,
            _ => TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Tenant suspended", detail: "The tenant is suspended; its users and API keys are refused until it is activated again."),
        };
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
