using System.Net;
using System.Net.Http.Json;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Api.Tests;

public sealed class MemberEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Uri MembersUri = new("/api/v1/members", UriKind.Relative);
    private static readonly string TenantA = ApiFactory.TenantA.ToString();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient TenantAdminClient() => factory.ClientFor(new TokenSpec("tenant-a-admin", [Roles.TenantAdmin], TenantA));

    private static Uri MemberUri(string userId) => new($"/api/v1/members/{Uri.EscapeDataString(userId)}", UriKind.Relative);

    [Fact]
    public async Task Tenant_admin_manages_members_and_cannot_remove_the_last_admin()
    {
        // A tenant of its own: the list and the final count are exact, so no other test's members may be in them.
        var tenant = factory.CreateTenant("Members");
        using var admin = factory.TenantAdminOf(tenant);

        var first = await admin.PostAsJsonAsync(MembersUri, new AddMemberRequest("user|admin-1", "admin1@example.test", "Admin One", "tenantadmin"), Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await first.Content.ReadFromJsonAsync<MemberResponse>(Ct))!;
        created.Role.ShouldBe(nameof(TenantRole.TenantAdmin));
        created.CreatedAt.ShouldBe(factory.Clock.GetUtcNow());
        first.Headers.Location!.ToString().ShouldEndWith(Uri.EscapeDataString("user|admin-1"));

        (await admin.PostAsJsonAsync(MembersUri, new AddMemberRequest("user|admin-1", null, null, "viewer"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await admin.PostAsJsonAsync(MembersUri, new AddMemberRequest("user|rev-1", null, "Reviewer", "Reviewer"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var list = (await admin.GetFromJsonAsync<List<MemberResponse>>(MembersUri, Ct))!;
        list.Select(m => m.UserId).ShouldBe(["user|admin-1", "user|rev-1"]);

        // The only admin can be neither demoted nor removed.
        (await admin.PatchAsJsonAsync(MemberUri("user|admin-1"), new ChangeMemberRoleRequest("Viewer"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await admin.DeleteAsync(MemberUri("user|admin-1"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // With a second admin the first one can go.
        var promoted = await admin.PatchAsJsonAsync(MemberUri("user|rev-1"), new ChangeMemberRoleRequest("TenantAdmin"), Ct);
        promoted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await promoted.Content.ReadFromJsonAsync<MemberResponse>(Ct))!.Role.ShouldBe(nameof(TenantRole.TenantAdmin));
        (await admin.DeleteAsync(MemberUri("user|admin-1"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.DeleteAsync(MemberUri("user|admin-1"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.DeleteAsync(MemberUri("user|rev-1"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "now the last admin");

        factory.Query(db => db.TenantMembers.Count(m => m.TenantId == tenant)).ShouldBe(1);
    }

    [Fact]
    public async Task Roles_are_validated_and_only_admins_write()
    {
        using var admin = TenantAdminClient();
        var bad = await admin.PostAsJsonAsync(MembersUri, new AddMemberRequest("", null, null, "Owner"), Ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await bad.Content.ReadAsStringAsync(Ct);
        body.ShouldContain("\"userId\"");
        body.ShouldContain("\"role\"");

        // A role is a name, never its numeric value ("0" would otherwise be TenantAdmin).
        var numeric = await admin.PostAsJsonAsync(MembersUri, new AddMemberRequest("user|numeric-role", null, null, "0"), Ct);
        numeric.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await numeric.Content.ReadAsStringAsync(Ct)).ShouldContain("\"role\"");
        factory.Query(db => db.TenantMembers.Any(m => m.UserId == "user|numeric-role")).ShouldBeFalse();

        using var reviewer = factory.ClientFor(new TokenSpec("rev", [Roles.Reviewer], TenantA));
        (await reviewer.GetAsync(MembersUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await reviewer.PostAsJsonAsync(MembersUri, new AddMemberRequest("user|x", null, null, "Viewer"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var platform = factory.ClientFor(new TokenSpec("superadmin", [Roles.SuperAdmin]));
        (await platform.GetAsync(MembersUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "platform scope without a tenant");

        using var acting = factory.ClientFor(new TokenSpec("superadmin", [Roles.SuperAdmin]), tenantHeader: Guid.NewGuid().ToString());
        (await acting.PostAsJsonAsync(MembersUri, new AddMemberRequest("user|x", null, null, "Viewer"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "the overridden tenant does not exist");
    }

    [Theory]
    [InlineData("auth0/abc")]
    [InlineData("user id")]
    [InlineData("user\tid")]
    [InlineData("user\u0001id")]
    [InlineData("/leading")]
    public async Task A_user_id_that_could_not_be_addressed_again_is_refused(string userId)
    {
        using var admin = TenantAdminClient();

        var response = await admin.PostAsJsonAsync(MembersUri, new AddMemberRequest(userId, null, null, "Viewer"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("\"userId\"");
        factory.Query(db => db.TenantMembers.Any(m => m.UserId == userId)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_user_id_with_pipes_colons_and_at_signs_round_trips_through_the_route()
    {
        var tenant = factory.CreateTenant("Odd subjects");
        using var admin = factory.TenantAdminOf(tenant);
        const string userId = "oidc|user:name@idp.example";

        (await admin.PostAsJsonAsync(MembersUri, new AddMemberRequest(userId, null, null, "Viewer"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await admin.PatchAsJsonAsync(MemberUri(userId), new ChangeMemberRoleRequest("Reviewer"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.DeleteAsync(MemberUri(userId), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Members_are_scoped_to_their_tenant()
    {
        using var adminA = TenantAdminClient();
        (await adminA.PostAsJsonAsync(MembersUri, new AddMemberRequest("user|only-in-a", null, null, "Viewer"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        using var adminB = factory.ClientFor(new TokenSpec("tenant-b-admin", [Roles.TenantAdmin], ApiFactory.TenantB.ToString()));
        (await adminB.GetFromJsonAsync<List<MemberResponse>>(MembersUri, Ct))!.ShouldNotContain(m => m.UserId == "user|only-in-a");
        (await adminB.DeleteAsync(MemberUri("user|only-in-a"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminB.PostAsJsonAsync(MembersUri, new AddMemberRequest("user|only-in-a", null, null, "Viewer"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created, "the same subject can be a member of both tenants");

        factory.Query(db => db.TenantMembers.Count(m => m.UserId == "user|only-in-a")).ShouldBe(2);
    }
}
