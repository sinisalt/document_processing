using System.Security.Claims;
using DocProc.Api.Auth;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Authorization;

namespace DocProc.Api.Endpoints;

/// <summary>Who am I: the identity, roles, resolved tenant scope and the policies this caller satisfies.</summary>
public sealed record MeResponse(
    string Subject,
    string? Name,
    string? Email,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Scopes,
    string AuthMethod,
    Guid? TenantId,
    string Scope,
    IReadOnlyList<string> Permissions);

public static class MeEndpoints
{
    public static RouteGroupBuilder MapMe(this RouteGroupBuilder group)
    {
        group.MapGet("/me", async (ClaimsPrincipal user, ITenantContext tenant, OidcOptions oidc, IAuthorizationService authorization) =>
        {
            var permissions = new List<string>();
            foreach (var policy in Policies.All)
            {
                if ((await authorization.AuthorizeAsync(user, policy).ConfigureAwait(false)).Succeeded)
                {
                    permissions.Add(policy);
                }
            }

            return TypedResults.Ok(new MeResponse(
                Subject: user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
                Name: user.Identity?.Name,
                Email: user.FindFirstValue("email"),
                Roles: user.FindAll(oidc.RoleClaimType).Select(c => c.Value).Distinct(StringComparer.Ordinal).ToList(),
                Scopes: user.FindAll(ApiKeyAuthenticationHandler.ScopeClaimType).Select(c => c.Value).Distinct(StringComparer.Ordinal).ToList(),
                AuthMethod: user.FindFirstValue(ApiKeyAuthenticationHandler.AuthMethodClaimType) ?? "oidc",
                TenantId: tenant.TenantId,
                Scope: tenant.IsPlatformScope ? "platform" : "tenant",
                Permissions: permissions));
        })
        .WithName("GetMe")
        .WithSummary("The caller's identity, roles, tenant scope and satisfied policies.");

        return group;
    }
}
