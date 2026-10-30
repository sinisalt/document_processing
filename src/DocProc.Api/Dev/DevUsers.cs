using System.Security.Cryptography;
using System.Text;
using DocProc.Api.Auth;

namespace DocProc.Api.Dev;

/// <summary>A user of the development identity provider; the same fields the Keycloak realm holds for it.</summary>
public sealed record DevUser(string Username, string Email, string FirstName, string LastName, IReadOnlyList<string> Roles, Guid? TenantId)
{
    public string DisplayName => $"{FirstName} {LastName}";

    /// <summary>Stable <c>sub</c> derived from the username, so a user keeps its identity across restarts and key rotations.</summary>
    public string Subject => DevUsers.SubjectFor(Username);
}

/// <summary>
/// The users of <c>src/DocProc.AppHost/realms/docproc-realm.json</c>, mirrored exactly (usernames, roles,
/// <c>tenant_id</c> attributes, e-mails and names), so a front end behaves the same against the dev issuer and
/// against Keycloak (docs/09 "Identity").
/// </summary>
public static class DevUsers
{
    public static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    public static readonly IReadOnlyList<DevUser> All =
    [
        new("superadmin", "superadmin@docproc.local", "Sam", "Super", [Roles.SuperAdmin], null),
        new("operator", "operator@docproc.local", "Olga", "Operator", [Roles.Operator], null),
        new("tenant-a-admin", "admin@tenant-a.local", "Anna", "Admin", [Roles.TenantAdmin], TenantA),
        new("tenant-a-reviewer", "reviewer@tenant-a.local", "Rea", "Reviewer", [Roles.Reviewer], TenantA),
        new("tenant-b-admin", "admin@tenant-b.local", "Bo", "Admin", [Roles.TenantAdmin], TenantB),
        new("tenant-b-viewer", "viewer@tenant-b.local", "Vic", "Viewer", [Roles.Viewer], TenantB),
    ];

    public static DevUser? Find(string? username) =>
        string.IsNullOrWhiteSpace(username) ? null : All.FirstOrDefault(u => string.Equals(u.Username, username.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>A GUID in the shape Keycloak would issue, computed from the username so it never changes.</summary>
    public static string SubjectFor(string username)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("docproc-dev:" + username.ToLowerInvariant()));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }
}
