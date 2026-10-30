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
/// Authorization policies. Each lists the roles allowed; SuperAdmin is in every policy, Operator in the platform and
/// read/review ones, tenant roles only in their own scope. Endpoints reference these names, never roles directly.
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

    /// <summary>Read documents and exports.</summary>
    public const string Read = "Read";

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> RolesByPolicy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [PlatformAdmin] = [Roles.SuperAdmin],
        [Platform] = [Roles.SuperAdmin, Roles.Operator],
        [TenantAdmin] = [Roles.SuperAdmin, Roles.TenantAdmin],
        [Review] = [Roles.SuperAdmin, Roles.Operator, Roles.TenantAdmin, Roles.Reviewer],
        [Read] = [Roles.SuperAdmin, Roles.Operator, Roles.TenantAdmin, Roles.Reviewer, Roles.Viewer],
    };

    public static IEnumerable<string> All => RolesByPolicy.Keys;
}
