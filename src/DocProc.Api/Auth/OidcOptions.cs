namespace DocProc.Api.Auth;

/// <summary>
/// Bearer-token validation settings (docs/05 "Roles"). Locally the authority is the Keycloak realm started by the
/// AppHost; in Azure it is Entra External ID (customers) or Entra ID (staff). The API only knows authority + audience.
/// </summary>
public sealed class OidcOptions
{
    public const string SectionName = "Authentication:Oidc";

    /// <summary>Issuer URL, e.g. <c>http://localhost:8080/realms/docproc</c>. Empty disables metadata discovery (tests supply keys directly).</summary>
    public string? Authority { get; set; }

    /// <summary>Expected <c>aud</c>; the Keycloak realm adds it through the audience mapper on the <c>docproc</c> scope.</summary>
    public string Audience { get; set; } = "docproc-api";

    /// <summary>False only for the local Keycloak over plain HTTP.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    public string RoleClaimType { get; set; } = "roles";

    public string NameClaimType { get; set; } = "preferred_username";

    /// <summary>Claim that binds a customer user to a tenant; absent for platform staff.</summary>
    public string TenantClaimType { get; set; } = "tenant_id";
}
