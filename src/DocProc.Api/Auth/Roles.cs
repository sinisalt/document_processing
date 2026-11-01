using System.Security.Claims;
using DocProc.Application.Security;

namespace DocProc.Api.Auth;

/// <summary>Role names as issued by the identity provider (docs/05 "Roles"). Case-sensitive.</summary>
public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Operator = "Operator";
    public const string TenantAdmin = "TenantAdmin";
    public const string Reviewer = "Reviewer";
    public const string Viewer = "Viewer";

    public static readonly IReadOnlyList<string> All = [SuperAdmin, Operator, TenantAdmin, Reviewer, Viewer];

    /// <summary>Roles that work in platform scope (no tenant of their own).</summary>
    public static readonly IReadOnlyList<string> PlatformRoles = [SuperAdmin, Operator];

    public static bool IsPlatformRole(string role) => PlatformRoles.Contains(role, StringComparer.Ordinal);
}

/// <summary>
/// Authorization policies. Human roles come from the token; machine clients hold API-key scopes instead and only ever
/// satisfy <see cref="Ingest"/> and <see cref="Read"/>. Endpoints reference these names, never roles or scopes directly.
/// </summary>
public static class Policies
{
    /// <summary>Manage tenants, platform providers, prompts, profiles, queues.</summary>
    public const string PlatformAdmin = "PlatformAdmin";

    /// <summary>Global review queue, retries, read any document.</summary>
    public const string Platform = "Platform";

    /// <summary>Manage one tenant: members, suppliers, channels, policy, API keys, webhooks.</summary>
    public const string TenantAdmin = "TenantAdmin";

    /// <summary>Review, correct, approve, reject documents.</summary>
    public const string Review = "Review";

    /// <summary>Upload documents: portal users and ingest-scoped API keys.</summary>
    public const string Ingest = "Ingest";

    /// <summary>Read documents and exports: every human role and read-scoped API keys.</summary>
    public const string Read = "Read";

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> RolesByPolicy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [PlatformAdmin] = [Roles.SuperAdmin],
        [Platform] = [Roles.SuperAdmin, Roles.Operator],
        [TenantAdmin] = [Roles.SuperAdmin, Roles.TenantAdmin],
        [Review] = [Roles.SuperAdmin, Roles.Operator, Roles.TenantAdmin, Roles.Reviewer],
        [Ingest] = [Roles.SuperAdmin, Roles.TenantAdmin, Roles.Reviewer],
        [Read] = [Roles.SuperAdmin, Roles.Operator, Roles.TenantAdmin, Roles.Reviewer, Roles.Viewer],
    };

    /// <summary>API-key scopes that satisfy a policy; policies absent here are never granted to a key.</summary>
    public static readonly IReadOnlyDictionary<string, string> ScopeByPolicy = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Ingest] = ApiKeyScopes.Ingest,
        [Read] = ApiKeyScopes.Read,
    };

    public static IEnumerable<string> All => RolesByPolicy.Keys;

    /// <summary>The single evaluation rule: a listed role, or (for machine clients) the listed scope.</summary>
    public static bool Satisfies(ClaimsPrincipal user, string policy)
    {
        if (RolesByPolicy[policy].Any(user.IsInRole))
        {
            return true;
        }

        return ScopeByPolicy.TryGetValue(policy, out var scope)
            && user.HasClaim(ApiKeyAuthenticationHandler.ScopeClaimType, scope);
    }
}
