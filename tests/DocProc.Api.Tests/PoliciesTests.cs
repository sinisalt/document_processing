using System.Security.Claims;
using DocProc.Api.Auth;
using DocProc.Infrastructure.Persistence.Tenancy;

namespace DocProc.Api.Tests;

public class PoliciesTests
{
    [Fact]
    public void Super_admin_is_in_every_policy_and_every_role_can_read()
    {
        foreach (var (_, roles) in Policies.RolesByPolicy)
        {
            roles.ShouldContain(Roles.SuperAdmin);
        }

        Policies.RolesByPolicy[Policies.Read].ShouldBe(Roles.All, ignoreOrder: true);
        Policies.RolesByPolicy[Policies.PlatformAdmin].ShouldBe([Roles.SuperAdmin]);
        Policies.RolesByPolicy[Policies.TenantAdmin].ShouldNotContain(Roles.Operator);
    }

    [Fact]
    public void Tenant_claim_that_is_not_a_guid_is_refused()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", "42"), new Claim("roles", Roles.Reviewer)], "test", "preferred_username", "roles"));
        var tenant = TenantContext.Anonymous();

        var problem = TenantResolutionMiddleware.Resolve(user, null, "tenant_id", tenant);

        problem.ShouldNotBeNull();
        problem.StatusCode.ShouldBe(403);
        tenant.TenantId.ShouldBeNull();
        tenant.IsPlatformScope.ShouldBeFalse();
    }
}
