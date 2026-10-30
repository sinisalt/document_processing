using System.Net;
using System.Net.Http.Json;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;

namespace DocProc.Api.Tests;

public sealed class AuthenticationTests : IClassFixture<ApiFactory>
{
    private const string TenantA = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string TenantB = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    private readonly ApiFactory _factory;

    public AuthenticationTests(ApiFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/alive")]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    [InlineData("/api/v1/version")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/scalar/v1")]
    public async Task Probes_docs_and_version_are_anonymous(string path)
    {
        using var client = _factory.ClientFor(null);

        var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
    }

    [Fact]
    public async Task Everything_else_requires_a_token_and_answers_with_problem_details()
    {
        using var client = _factory.ClientFor(null);

        var response = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("Bearer");
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Tenant_reviewer_is_scoped_to_their_tenant_with_review_and_read_permissions()
    {
        using var client = _factory.ClientFor(new TokenSpec("tenant-a-reviewer", [Roles.Reviewer], TenantA));

        var me = await client.GetFromJsonAsync<MeResponse>(new Uri("/api/v1/me", UriKind.Relative), Ct);

        me!.Subject.ShouldBe("tenant-a-reviewer");
        me.Name.ShouldBe("tenant-a-reviewer");
        me.Email.ShouldBe("tenant-a-reviewer@example.test");
        me.Roles.ShouldBe([Roles.Reviewer]);
        me.TenantId.ShouldBe(Guid.Parse(TenantA));
        me.Scope.ShouldBe("tenant");
        me.Permissions.ShouldBe([Policies.Review, Policies.Read], ignoreOrder: true);
    }

    [Fact]
    public async Task Super_admin_runs_in_platform_scope_with_every_permission()
    {
        using var client = _factory.ClientFor(new TokenSpec("superadmin", [Roles.SuperAdmin]));

        var me = await client.GetFromJsonAsync<MeResponse>(new Uri("/api/v1/me", UriKind.Relative), Ct);

        me!.Scope.ShouldBe("platform");
        me.TenantId.ShouldBeNull();
        me.Permissions.ShouldBe(Policies.All.ToList(), ignoreOrder: true);
    }

    [Fact]
    public async Task Super_admin_can_act_inside_a_tenant_with_the_header()
    {
        using var client = _factory.ClientFor(new TokenSpec("superadmin", [Roles.SuperAdmin]), tenantHeader: TenantB);

        var me = await client.GetFromJsonAsync<MeResponse>(new Uri("/api/v1/me", UriKind.Relative), Ct);

        me!.Scope.ShouldBe("tenant");
        me.TenantId.ShouldBe(Guid.Parse(TenantB));
    }

    [Fact]
    public async Task Operator_has_platform_read_and_review_but_may_not_override_the_tenant()
    {
        using var platform = _factory.ClientFor(new TokenSpec("operator", [Roles.Operator]));
        var me = await platform.GetFromJsonAsync<MeResponse>(new Uri("/api/v1/me", UriKind.Relative), Ct);
        me!.Scope.ShouldBe("platform");
        me.Permissions.ShouldBe([Policies.Platform, Policies.Review, Policies.Read], ignoreOrder: true);

        using var overriding = _factory.ClientFor(new TokenSpec("operator", [Roles.Operator]), tenantHeader: TenantA);
        var response = await overriding.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Only a SuperAdmin");
    }

    [Fact]
    public async Task Tenant_role_without_a_tenant_claim_is_refused()
    {
        using var client = _factory.ClientFor(new TokenSpec("orphan", [Roles.Viewer]));

        var response = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("tenant_id");
    }

    [Fact]
    public async Task Malformed_tenant_header_is_a_bad_request()
    {
        using var client = _factory.ClientFor(new TokenSpec("superadmin", [Roles.SuperAdmin]), tenantHeader: "not-a-guid");

        var response = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Wrong_audience_wrong_issuer_and_expired_tokens_are_rejected()
    {
        foreach (var spec in new[]
        {
            new TokenSpec("x", [Roles.SuperAdmin], Audience: "someone-else"),
            new TokenSpec("x", [Roles.SuperAdmin], Issuer: "https://evil.example"),
            new TokenSpec("x", [Roles.SuperAdmin], Expires: DateTime.UtcNow.AddMinutes(-5)),
        })
        {
            using var client = _factory.ClientFor(spec);
            var response = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, spec.ToString());
        }
    }

    [Fact]
    public async Task OpenApi_document_declares_the_bearer_scheme()
    {
        using var client = _factory.ClientFor(null);

        var json = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct);

        json.ShouldContain("\"securitySchemes\"");
        json.ShouldContain("\"bearerFormat\": \"JWT\"");
    }
}
