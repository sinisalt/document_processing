using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Api.Dev;
using DocProc.Api.Endpoints;
using DocProc.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

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
        // The realm the AppHost imports into Keycloak (copied to the test output by the project file), so a change to
        // either side fails here until the other follows.
        var realmPath = Path.Combine(AppContext.BaseDirectory, "realms", "docproc-realm.json");
        File.Exists(realmPath).ShouldBeTrue($"realm file not copied to output: {realmPath}");
        using var realm = JsonDocument.Parse(await File.ReadAllTextAsync(realmPath, Ct));
        var expected = realm.RootElement.GetProperty("users").EnumerateArray()
            .Select(u => RealmUser(
                u.GetProperty("username").GetString()!,
                u.GetProperty("email").GetString()!,
                u.GetProperty("firstName").GetString()!,
                u.GetProperty("lastName").GetString()!,
                u.GetProperty("realmRoles").EnumerateArray().Select(r => r.GetString()!),
                u.TryGetProperty("attributes", out var attributes) && attributes.TryGetProperty("tenant_id", out var tenant) ? Guid.Parse(tenant[0].GetString()!) : null))
            .OrderBy(u => u.Username, StringComparer.Ordinal)
            .ToList();
        expected.Count.ShouldBe(6);

        using var client = factory.CreateClient();
        var users = (await client.GetFromJsonAsync<List<DevUser>>(UsersUri, Ct))!;

        users.Select(u => RealmUser(u.Username, u.Email, u.FirstName, u.LastName, u.Roles, u.TenantId))
            .OrderBy(u => u.Username, StringComparer.Ordinal)
            .ShouldBe(expected);
        users.Select(u => u.Username).ShouldBe(DevUsers.All.Select(u => u.Username), "the endpoint lists DevUsers.All as is");

        // The sign-in page names the tenant instead of printing a fragment of its id.
        var names = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync(UsersUri, Ct)).RootElement.EnumerateArray()
            .ToDictionary(u => u.GetProperty("username").GetString()!, u => u.GetProperty("tenantName").GetString());
        names["tenant-a-admin"].ShouldBe("Gasthaus zur Post GmbH");
        names["tenant-b-viewer"].ShouldBe("Pizzeria Napoli d.o.o.");
        names["superadmin"].ShouldBeNull();
    }

    private static (string Username, string Email, string FirstName, string LastName, string Roles, Guid? TenantId) RealmUser(
        string username, string email, string firstName, string lastName, IEnumerable<string> roles, Guid? tenantId) =>
        (username, email, firstName, lastName, string.Join(",", roles.Order(StringComparer.Ordinal)), tenantId);

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
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(keyPath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite, "the private key is readable by its owner only");
            }

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
    public void Corrupt_signing_key_file_fails_at_start_up()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docproc-tests", Guid.NewGuid().ToString("N"));
        var keyPath = Path.Combine(directory, "dev-signing-key.pem");
        Directory.CreateDirectory(directory);
        File.WriteAllText(keyPath, "-----BEGIN RSA PRIVATE KEY-----\nnot a key\n-----END RSA PRIVATE KEY-----\n");
        try
        {
            using var broken = DevModeFactory.WithSigningKeyFile(keyPath);

            // The issuer is resolved while the host is built, so nothing answers a request with a 500 later.
            var error = Should.Throw<Exception>(() => broken.CreateClient());

            Flatten(error).ShouldContain(e => e is InvalidOperationException && e.Message.Contains(keyPath, StringComparison.Ordinal) && e.Message.Contains("SigningKeyPath", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Relative_database_and_key_paths_are_anchored_to_the_content_root()
    {
        // Not the process working directory: an IDE run configuration or `dotnet <dll>` from elsewhere must find the same files.
        var contentRoot = Path.Combine(Path.GetTempPath(), "docproc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        try
        {
            using var host = new SettingsFactory("Testing", new Dictionary<string, string?>
            {
                ["contentRoot"] = contentRoot,
                ["Persistence:Provider"] = "Sqlite",
                ["Persistence:SqlitePath"] = "./data/anchored.db",
                ["Authentication:Dev:Enabled"] = "true",
                ["Authentication:Dev:SigningKeyPath"] = "data/anchored-key.pem",
                ["Authentication:Oidc:Authority"] = string.Empty,
            });
            using var client = host.CreateClient();

            (await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            host.Services.GetRequiredService<SqliteDatabaseLocation>().Path.ShouldBe(Path.Combine(contentRoot, "data", "anchored.db"));
            File.Exists(Path.Combine(contentRoot, "data", "anchored.db")).ShouldBeTrue();
            File.Exists(Path.Combine(contentRoot, "data", "anchored-key.pem")).ShouldBeTrue();
            Path.GetFullPath("./data/anchored.db").ShouldNotBe(Path.Combine(contentRoot, "data", "anchored.db"), "the working directory is somewhere else");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(contentRoot, recursive: true);
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

    [Fact]
    public void Seed_dev_data_in_production_refuses_to_start()
    {
        // SQL Server in production with the demo seed on: the seed's tenants have fixed, publicly known ids and must never land there.
        using var production = new SettingsFactory("Production", new Dictionary<string, string?>
        {
            ["Authentication:Dev:Enabled"] = "false",
            ["Authentication:Oidc:Authority"] = "https://login.example.test/realms/docproc",
            ["Persistence:Provider"] = "SqlServer",
            ["ConnectionStrings:DocProc"] = "Server=sql.example.test;Database=DocProc;User Id=app;Password=unused;TrustServerCertificate=true",
            ["Persistence:SeedDevData"] = "true",
        });

        var error = Should.Throw<Exception>(() => production.CreateClient());

        Flatten(error).ShouldContain(e => e is InvalidOperationException && e.Message.Contains("Production", StringComparison.Ordinal) && e.Message.Contains("SeedDevData", StringComparison.Ordinal));
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
