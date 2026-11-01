using System.Net;
using System.Net.Http.Json;
using DocProc.Api.Endpoints;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocProc.Api.Tests;

/// <summary>
/// Check-then-insert races end in 409, not 500. These run on the development host (file-backed SQLite, one pooled
/// connection per request) because <see cref="ApiFactory"/>'s single in-memory connection cannot carry two
/// transactions at once; the unique index and the composite key are what SQL Server enforces too. Four genuinely
/// concurrent requests prove the contract from the outside; the interceptor-driven tests force the losing
/// interleaving (the duplicate lands between the existence check and the insert) so the constraint path is
/// exercised on every run, whatever the scheduler does.
/// </summary>
public sealed class TenancyConcurrencyTests(TenancyConcurrencyTests.RacingDevFactory factory) : IClassFixture<TenancyConcurrencyTests.RacingDevFactory>
{
    private static readonly Uri TenantsUri = new("/api/v1/tenants", UriKind.Relative);
    private static readonly Uri MembersUri = new("/api/v1/members", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CreateTenantRequest NewTenant(string slug) => new("Raced", slug, "Raced GmbH", null, "EUR", "DE", "Europe/Berlin");

    [Fact]
    public async Task Concurrent_tenant_creates_with_the_same_slug_yield_one_created_and_the_rest_conflict()
    {
        using var superAdmin = await factory.ClientAsAsync("superadmin", Ct);
        var slug = "race-" + Guid.NewGuid().ToString("N")[..8];

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => superAdmin.PostAsJsonAsync(TenantsUri, NewTenant(slug), Ct)));

        var outcome = string.Join(", ", await Task.WhenAll(responses.Select(async r => $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync(Ct)}")));
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1, outcome);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(3, outcome);

        await using var db = factory.PlatformDb();
        var tenants = db.Tenants.Where(t => t.Slug == slug).Select(t => t.Id).ToList();
        tenants.Count.ShouldBe(1);
        db.IngestionChannels.Count(c => c.TenantId == tenants[0]).ShouldBe(2, "only the winner's default channels exist");
    }

    [Fact]
    public async Task A_slug_taken_between_the_check_and_the_insert_is_a_409_not_a_500()
    {
        using var superAdmin = await factory.ClientAsAsync("superadmin", Ct);
        var slug = "sneak-" + Guid.NewGuid().ToString("N")[..8];
        var rival = Guid.NewGuid();
        factory.Race.ArmTenantSlug(slug, db => db.Tenants.Add(new Tenant { Id = rival, Name = "Rival", Slug = slug, LegalName = "Rival GmbH", DefaultCurrency = "EUR", CountryCode = "DE", TimeZone = "Europe/Berlin" }));

        var response = await superAdmin.PostAsJsonAsync(TenantsUri, NewTenant(slug), Ct);

        factory.Race.Fired.ShouldBeTrue("the rival row was inserted while the request was between its check and its insert");
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Slug taken");
        await using var db = factory.PlatformDb();
        db.Tenants.Where(t => t.Slug == slug).Select(t => t.Id).ToList().ShouldBe([rival]);
        db.IngestionChannels.Count(c => c.TenantId == rival).ShouldBe(0, "the loser's default channels were rolled back with it");
    }

    [Fact]
    public async Task Concurrent_member_adds_of_the_same_user_yield_one_created_and_the_rest_conflict()
    {
        using var admin = await factory.ClientAsAsync("tenant-a-admin", Ct);
        var userId = "user|raced-" + Guid.NewGuid().ToString("N")[..8];

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => admin.PostAsJsonAsync(MembersUri, new AddMemberRequest(userId, null, null, "Viewer"), Ct)));

        var outcome = string.Join(", ", await Task.WhenAll(responses.Select(async r => $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync(Ct)}")));
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1, outcome);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(3, outcome);

        await using var db = factory.PlatformDb();
        db.TenantMembers.Count(m => m.UserId == userId).ShouldBe(1);
    }

    [Fact]
    public async Task A_member_added_between_the_check_and_the_insert_is_a_409_not_a_500()
    {
        using var admin = await factory.ClientAsAsync("tenant-a-admin", Ct);
        var userId = "user|sneak-" + Guid.NewGuid().ToString("N")[..8];
        factory.Race.ArmMember(userId, db => db.TenantMembers.Add(new TenantMember { TenantId = DevSeed.TenantA, UserId = userId, Role = TenantRole.Reviewer }));

        var response = await admin.PostAsJsonAsync(MembersUri, new AddMemberRequest(userId, null, null, "Viewer"), Ct);

        factory.Race.Fired.ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Already a member");
        await using var db = factory.PlatformDb();
        db.TenantMembers.Where(m => m.UserId == userId).Select(m => m.Role).ToList().ShouldBe([TenantRole.Reviewer], "the rival row stands, the request's did not land");
    }

    /// <summary>
    /// Runs the application's own SaveChanges one step later than the handler expects: when an armed row is about
    /// to be inserted, the rival is written first through a plain context of its own, so the handler's insert hits
    /// the constraint. Attached to both the request-scoped context and the platform factory by <see cref="RacingDevFactory"/>.
    /// </summary>
    public sealed class RaceInjector : SaveChangesInterceptor
    {
        private Func<DbContext, bool>? _trigger;
        private Action<DocProcDbContext>? _rival;

        public Func<DocProcDbContext>? Contexts { get; set; }

        public bool Fired { get; private set; }

        public void ArmTenantSlug(string slug, Action<DocProcDbContext> rival) =>
            Arm(db => db.ChangeTracker.Entries<Tenant>().Any(e => e.State == EntityState.Added && e.Entity.Slug == slug), rival);

        public void ArmMember(string userId, Action<DocProcDbContext> rival) =>
            Arm(db => db.ChangeTracker.Entries<TenantMember>().Any(e => e.State == EntityState.Added && e.Entity.UserId == userId), rival);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_trigger is { } trigger && eventData.Context is { } context && trigger(context))
            {
                _trigger = null;
                using var rivalDb = Contexts!();
                _rival!(rivalDb);
                rivalDb.SaveChanges();
                Fired = true;
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private void Arm(Func<DbContext, bool> trigger, Action<DocProcDbContext> rival)
        {
            Fired = false;
            _rival = rival;
            _trigger = trigger;
        }
    }

    /// <summary>
    /// The development host (as <see cref="DevModeFactory"/>) with the <see cref="RaceInjector"/> attached to every
    /// context the API writes through: the request-scoped <see cref="DocProcDbContext"/> (members) and the
    /// platform-scoped factory (tenants). EF does not pick interceptors up from the application's DI, so the two
    /// registrations the SQLite persistence made are replaced with ones that add it to the options.
    /// </summary>
    public sealed class RacingDevFactory : WebApplicationFactory<Program>
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "docproc-tests", Guid.NewGuid().ToString("N"));

        public RaceInjector Race { get; } = new();

        /// <summary>A plain platform-scoped context without the injector, for the rival writes and for assertions.</summary>
        public DocProcDbContext PlatformDb() => PlainPlatformContext(Services.GetRequiredService<SqliteDatabaseLocation>().ConnectionString);

        private static DocProcDbContext PlainPlatformContext(string connectionString) =>
            new(new DbContextOptionsBuilder<DocProcDbContext>().UseSqlite(connectionString).Options, Infrastructure.Persistence.Tenancy.TenantContext.Platform());

        private sealed class InterceptedPlatformFactory(string connectionString, RaceInjector race) : ISystemDbContextFactory
        {
            public DocProcDbContext CreatePlatformScoped() =>
                new(new DbContextOptionsBuilder<DocProcDbContext>().UseSqlite(connectionString).AddInterceptors(race).Options, Infrastructure.Persistence.Tenancy.TenantContext.Platform());
        }

        public async Task<HttpClient> ClientAsAsync(string username, CancellationToken ct)
        {
            using var anonymous = CreateClient();
            var response = await anonymous.PostAsJsonAsync(new Uri("/dev/token", UriKind.Relative), new Dev.DevTokenRequest(username), ct);
            response.EnsureSuccessStatusCode();
            var token = (await response.Content.ReadFromJsonAsync<Dev.DevTokenResponse>(ct))!;
            var client = CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Persistence:Provider", "Sqlite");
            builder.UseSetting("Persistence:SqlitePath", Path.Combine(_directory, "docproc.db"));
            builder.UseSetting("Blobs:RootPath", Path.Combine(_directory, "blobs"));
            builder.UseSetting("Secrets:Path", Path.Combine(_directory, "secrets"));
            builder.UseSetting("Authentication:Dev:Enabled", "true");
            builder.UseSetting("Authentication:Oidc:Authority", string.Empty);
            builder.UseSetting("Authentication:Dev:SigningKeyPath", string.Empty);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<DocProcDbContext>>();
                services.RemoveAll<DbContextOptions>();
                services.AddDbContext<DocProcDbContext>((sp, options) => options.UseSqlite(sp.GetRequiredService<SqliteDatabaseLocation>().ConnectionString).AddInterceptors(Race));
                services.RemoveAll<ISystemDbContextFactory>();
                services.AddSingleton<ISystemDbContextFactory>(sp => new InterceptedPlatformFactory(sp.GetRequiredService<SqliteDatabaseLocation>().ConnectionString, Race));
            });
            Race.Contexts = PlatformDb;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }
            }
        }
    }
}
