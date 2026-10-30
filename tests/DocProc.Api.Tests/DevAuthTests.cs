using System.Net;
using System.Net.Http.Json;
using DocProc.Api.Auth;
using DocProc.Api.Dev;
using DocProc.Api.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DocProc.Api.Tests;

/// <summary>The development token issuer (docs/09 "Running without Docker"): absent when off, Keycloak-shaped tokens when on, never in production.</summary>
public sealed class DevAuthTests(DevModeFactory factory) : IClassFixture<DevModeFactory>
{
    private static readonly Uri UsersUri = new("/dev/users", UriKind.Relative);
    private static readonly Uri TokenUri = new("/dev/token", UriKind.Relative);
    private static readonly Uri MeUri = new("/api/v1/me", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Dev_endpoints_do_not_exist_when_dev_auth_is_disabled()
    {
        using var oidcOnly = new ApiFactory();

        // Not mapped at all: even an authenticated SuperAdmin gets 404 (an anonymous caller gets the fallback policy's 401 for any unknown route).
        using var superadmin = oidcOnly.ClientFor(new TokenSpec("superadmin", [Roles.SuperAdmin]));
        (await superadmin.GetAsync(UsersUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await superadmin.PostAsJsonAsync(TokenUri, new DevTokenRequest("superadmin"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var anonymous = oidcOnly.CreateClient();
        (await anonymous.PostAsJsonAsync(TokenUri, new DevTokenRequest("superadmin"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Dev_users_mirror_the_keycloak_realm()
    {
        using var client = factory.CreateClient();

        var users = (await client.GetFromJsonAsync<List<DevUser>>(UsersUri, Ct))!;

        users.Count.ShouldBe(6);
        users.Select(u => u.Username).ShouldBe(["superadmin", "operator", "tenant-a-admin", "tenant-a-reviewer", "tenant-b-admin", "tenant-b-viewer"]);
        users.Single(u => u.Username == "superadmin").Roles.ShouldBe([Roles.SuperAdmin]);
        users.Single(u => u.Username == "superadmin").TenantId.ShouldBeNull();
        users.Single(u => u.Username == "operator").Roles.ShouldBe([Roles.Operator]);
        users.Single(u => u.Username == "tenant-a-admin").ShouldSatisfyAllConditions(
            u => u.Roles.ShouldBe([Roles.TenantAdmin]),
            u => u.TenantId.ShouldBe(DevUsers.TenantA),
            u => u.Email.ShouldBe("admin@tenant-a.local"),
            u => u.FirstName.ShouldBe("Anna"),
            u => u.LastName.ShouldBe("Admin"));
        users.Single(u => u.Username == "tenant-a-reviewer").ShouldSatisfyAllConditions(
            u => u.Roles.ShouldBe([Roles.Reviewer]),
            u => u.TenantId.ShouldBe(DevUsers.TenantA));
        users.Single(u => u.Username == "tenant-b-admin").ShouldSatisfyAllConditions(
            u => u.Roles.ShouldBe([Roles.TenantAdmin]),
            u => u.TenantId.ShouldBe(DevUsers.TenantB));
        users.Single(u => u.Username == "tenant-b-viewer").ShouldSatisfyAllConditions(
            u => u.Roles.ShouldBe([Roles.Viewer]),
            u => u.TenantId.ShouldBe(DevUsers.TenantB),
            u => u.Email.ShouldBe("viewer@tenant-b.local"));
    }

    [Fact]
    public async Task Token_response_has_the_oauth_shape()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(TokenUri, new DevTokenRequest("tenant-a-admin"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        json.GetProperty("token_type").GetString().ShouldBe("Bearer");
        json.GetProperty("expires_in").GetInt32().ShouldBeGreaterThan(0);
        json.GetProperty("access_token").GetString()!.Split('.').Length.ShouldBe(3);
        json.GetProperty("user").GetProperty("username").GetString().ShouldBe("tenant-a-admin");
    }

    [Fact]
    public async Task Tenant_admin_token_is_accepted_with_its_tenant_and_role()
    {
        using var client = await factory.ClientAsAsync("tenant-a-admin", Ct);

        var me = (await client.GetFromJsonAsync<MeResponse>(MeUri, Ct))!;

        me.Name.ShouldBe("tenant-a-admin");
        me.Email.ShouldBe("admin@tenant-a.local");
        me.Subject.ShouldBe(DevUsers.SubjectFor("tenant-a-admin"));
        me.AuthMethod.ShouldBe("oidc");
        me.Roles.ShouldBe([Roles.TenantAdmin]);
        me.TenantId.ShouldBe(DevUsers.TenantA);
        me.Scope.ShouldBe("tenant");
        me.Permissions.ShouldBe([Policies.TenantAdmin, Policies.Review, Policies.Ingest, Policies.Read], ignoreOrder: true);
    }

    [Fact]
    public async Task Superadmin_token_gets_platform_scope()
    {
        using var client = await factory.ClientAsAsync("superadmin", Ct);

        var me = (await client.GetFromJsonAsync<MeResponse>(MeUri, Ct))!;

        me.Roles.ShouldBe([Roles.SuperAdmin]);
        me.TenantId.ShouldBeNull();
        me.Scope.ShouldBe("platform");
        me.Permissions.ShouldContain(Policies.PlatformAdmin);
    }

    [Fact]
    public async Task Viewer_token_satisfies_read_only()
    {
        using var client = await factory.ClientAsAsync("tenant-b-viewer", Ct);

        var me = (await client.GetFromJsonAsync<MeResponse>(MeUri, Ct))!;

        me.TenantId.ShouldBe(DevUsers.TenantB);
        me.Permissions.ShouldBe([Policies.Read]);
    }

    [Fact]
    public async Task Unknown_user_and_missing_username_are_refused()
    {
        using var client = factory.CreateClient();

        (await client.PostAsJsonAsync(TokenUri, new DevTokenRequest("mallory"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync(TokenUri, new DevTokenRequest(string.Empty), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Tokens_from_another_issuer_or_key_are_rejected()
    {
        // Minted with the OIDC test key and issuer of ApiFactory, which this host does not trust.
        using var foreign = factory.ClientWithToken(ApiFactory.Mint(new TokenSpec("superadmin", [Roles.SuperAdmin])));

        (await foreign.GetAsync(MeUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Signing_key_from_pem_file_survives_a_restart()
    {
        var keyPath = Path.Combine(Path.GetTempPath(), "docproc-tests", Guid.NewGuid().ToString("N"), "dev-signing-key.pem");
        try
        {
            string token;
            using (var first = DevModeFactory.WithSigningKeyFile(keyPath))
            {
                token = (await first.TokenForAsync("operator", Ct)).AccessToken;
            }

            File.Exists(keyPath).ShouldBeTrue();

            using var second = DevModeFactory.WithSigningKeyFile(keyPath);
            using var client = second.ClientWithToken(token);
            var me = (await client.GetFromJsonAsync<MeResponse>(MeUri, Ct))!;
            me.Name.ShouldBe("operator");
            me.Scope.ShouldBe("platform");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(keyPath)!, recursive: true);
        }
    }

    [Fact]
    public void Dev_auth_enabled_in_production_refuses_to_start()
    {
        using var production = new SettingsFactory("Production", new Dictionary<string, string?>
        {
            ["Authentication:Dev:Enabled"] = "true",
            ["Authentication:Oidc:Authority"] = "https://login.example.test/realms/docproc",
        });

        var error = Should.Throw<Exception>(() => production.CreateClient());

        Flatten(error).ShouldContain(e => e is InvalidOperationException && e.Message.Contains("Production", StringComparison.Ordinal) && e.Message.Contains("Authentication:Dev", StringComparison.Ordinal));
    }

    [Fact]
    public void Sqlite_in_production_refuses_to_start()
    {
        using var production = new SettingsFactory("Production", new Dictionary<string, string?>
        {
            ["Authentication:Dev:Enabled"] = "false",
            ["Authentication:Oidc:Authority"] = "https://login.example.test/realms/docproc",
            ["Persistence:Provider"] = "Sqlite",
            ["Persistence:SqlitePath"] = Path.Combine(Path.GetTempPath(), "docproc-tests", "never-created.db"),
        });

        var error = Should.Throw<Exception>(() => production.CreateClient());

        Flatten(error).ShouldContain(e => e is InvalidOperationException && e.Message.Contains("Production", StringComparison.Ordinal) && e.Message.Contains("Sqlite", StringComparison.Ordinal));
    }

    private static IEnumerable<Exception> Flatten(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            yield return e;
            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Flatten))
                {
                    yield return inner;
                }
            }
        }
    }

    /// <summary>A bare host with the given environment and settings; used to prove that some combinations never start.</summary>
    private sealed class SettingsFactory(string environment, IReadOnlyDictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        }
    }
}
