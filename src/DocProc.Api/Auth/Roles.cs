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

    /// <summary>
    /// A grant held besides a tenant role (docs/11 §16): activating and retiring layouts, including a layout saved from a
    /// <c>NEW_LAYOUT</c> review, which then becomes Active at once (decision 2). A Reviewer without it saves Candidates.
    /// </summary>
    public const string MappingAuthor = "MappingAuthor";

    public static readonly IReadOnlyList<string> All = [SuperAdmin, Operator, TenantAdmin, Reviewer, Viewer];

    /// <summary>Grants that add to a role and give no access alone (<see cref="MappingAuthor"/>).</summary>
    public static readonly IReadOnlyList<string> Grants = [MappingAuthor];

    /// <summary>Roles that work in platform scope (no tenant of their own).</summary>
    public static readonly IReadOnlyList<string> PlatformRoles = [SuperAdmin, Operator];

    public static bool IsPlatformRole(string role) => PlatformRoles.Contains(role, StringComparer.Ordinal);
}

/// <summary>
/// Authorization policies. Human roles come from the token; machine clients hold API-key scopes instead and only ever
/// satisfy <see cref="Ingest"/>, <see cref="Read"/> and <see cref="DeliveriesRead"/>. Endpoints reference these names, never roles or scopes directly.
/// </summary>
public static class Policies
{
    /// <summary>Manage tenants, platform providers, prompts, profiles, queues.</summary>
    public const string PlatformAdmin = "PlatformAdmin";

    /// <summary>Global review queue, retries, read any document.</summary>
    public const string Platform = "Platform";

    /// <summary>Manage one tenant: members, suppliers, channels, policy, API keys, webhooks.</summary>
    public const string TenantAdmin = "TenantAdmin";

    /// <summary>
    /// Configure one tenant's document types, processing and lookups (docs/11 §16, <c>ConfigAdmin</c>). Implied by
    /// TenantAdmin; there is no separate role for it yet.
    /// </summary>
    public const string ConfigAdmin = "ConfigAdmin";

    /// <summary>
    /// Configure one tenant's destinations, output mappings and bindings (docs/11 §16, <c>IntegrationAdmin</c>). Implied
    /// by TenantAdmin; there is no separate role for it yet.
    /// </summary>
    public const string IntegrationAdmin = "IntegrationAdmin";

    /// <summary>
    /// Read a pull destination's feed and acknowledge it (docs/11 §13.3): integration admins, and API keys with the
    /// <c>deliveries:read</c> scope (restricted to one destination when the key names one).
    /// </summary>
    public const string DeliveriesRead = "DeliveriesRead";

    /// <summary>
    /// Activate and retire supplier layouts (docs/11 §16 <c>MappingAuthor</c>, decision 2 of §24): tenant admins, and
    /// members holding the <see cref="Roles.MappingAuthor"/> grant. Proposing a Candidate layout from review
    /// (<c>MappingEditor</c> of §16) is the <see cref="Review"/> policy.
    /// </summary>
    public const string MappingAuthor = "MappingAuthor";

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
        [ConfigAdmin] = [Roles.SuperAdmin, Roles.TenantAdmin],
        [IntegrationAdmin] = [Roles.SuperAdmin, Roles.TenantAdmin],
        [DeliveriesRead] = [Roles.SuperAdmin, Roles.TenantAdmin],
        [MappingAuthor] = [Roles.SuperAdmin, Roles.TenantAdmin, Roles.MappingAuthor],
        [Review] = [Roles.SuperAdmin, Roles.Operator, Roles.TenantAdmin, Roles.Reviewer],
        [Ingest] = [Roles.SuperAdmin, Roles.TenantAdmin, Roles.Reviewer],
        [Read] = [Roles.SuperAdmin, Roles.Operator, Roles.TenantAdmin, Roles.Reviewer, Roles.Viewer],
    };

    /// <summary>API-key scopes that satisfy a policy; policies absent here are never granted to a key.</summary>
    public static readonly IReadOnlyDictionary<string, string> ScopeByPolicy = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Ingest] = ApiKeyScopes.Ingest,
        [Read] = ApiKeyScopes.Read,
        [DeliveriesRead] = ApiKeyScopes.DeliveriesRead,
    };

    public static IEnumerable<string> All => RolesByPolicy.Keys;

    /// <summary>
    /// The single evaluation rule: a listed role, or (for machine clients) the listed scope. A listed grant
    /// (<see cref="Roles.Grants"/>) counts only together with a role that may review (<see cref="Review"/>): a token
    /// holding the grant alone, or with Viewer, gets no access from it.
    /// </summary>
    public static bool Satisfies(ClaimsPrincipal user, string policy)
    {
        ArgumentNullException.ThrowIfNull(user);
        foreach (var role in RolesByPolicy[policy].Where(user.IsInRole))
        {
            if (!Roles.Grants.Contains(role, StringComparer.Ordinal)
                || RolesByPolicy[Review].Any(r => !Roles.Grants.Contains(r, StringComparer.Ordinal) && user.IsInRole(r)))
            {
                return true;
            }
        }

        return ScopeByPolicy.TryGetValue(policy, out var scope)
            && user.HasClaim(ApiKeyAuthenticationHandler.ScopeClaimType, scope);
    }
}
