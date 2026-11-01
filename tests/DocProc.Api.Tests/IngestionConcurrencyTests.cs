using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocProc.Api.Tests;

/// <summary>
/// The idempotency-key race of <c>POST /ingestions</c> around an <em>expired</em> key: two uploads reusing it both find
/// the stale row, both remove it and add a fresh one, and the loser's delete affects no row, which EF reports as
/// <c>DbUpdateConcurrencyException</c>. Like <see cref="ConcurrencyTests"/> this runs on a file-backed SQLite database
/// in WAL mode with a connection per context (the parent factory's single in-memory connection cannot carry two
/// transactions), keeping the parent's Program, fake clock, blob store and capturing queue. One test forces the losing
/// interleaving with a save interceptor so the classification path runs on every build; the other races two real
/// requests and asserts the outcome that holds whichever order the scheduler picks.
/// </summary>
public sealed class IngestionConcurrencyTests : IDisposable
{
    private static readonly Uri IngestionsUri = new("/api/v1/ingestions", UriKind.Relative);
    private static readonly string TenantA = ApiFactory.TenantA.ToString();

    private readonly ApiFactory _factory = new();
    private readonly string _directory;
    private readonly string _connectionString;
    private readonly ExpiredKeyRace _race;
    private readonly WebApplicationFactory<Program> _host;

    public IngestionConcurrencyTests()
    {
        var factory = _factory;
        _directory = Path.Combine(Path.GetTempPath(), "docproc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "ingestion-concurrency.db"), Pooling = false }.ToString();
        var contexts = new FileSqliteDbContextFactory(_connectionString);
        _race = new ExpiredKeyRace(contexts.CreatePlatformScoped);
        var race = _race;

        _host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<DocProcDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<DocProcDbContext>();
            services.RemoveAll<ISystemDbContextFactory>();
            services.AddDbContext<DocProcDbContext>(o => o.UseSqlite(_connectionString).AddInterceptors(race));
            services.AddSingleton<ISystemDbContextFactory>(contexts);

            using var db = contexts.CreatePlatformScoped();
            db.Database.EnsureCreated();
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            var now = factory.Clock.GetUtcNow();
            db.Tenants.Add(new Tenant { Id = ApiFactory.TenantA, Name = "Tenant A", Slug = "tenant-a", LegalName = "Gasthaus zur Post GmbH", DefaultCurrency = "EUR", CountryCode = "DE", TimeZone = "Europe/Berlin", CreatedAt = now, UpdatedAt = now });
            db.SaveChanges();
        }));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _host.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task An_expired_key_replaced_by_a_rival_between_lookup_and_save_replays_the_rivals_response()
    {
        using var client = Client("uploader");
        var key = "nightly-" + Guid.NewGuid().ToString("N");
        var dayOne = await Upload(client, key, "day-1.pdf", Bytes("day one"));
        dayOne.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var dayOneId = (await dayOne.Content.ReadFromJsonAsync<IngestionCreated>(ApiFactory.Json, Ct))!.Id;

        _factory.Clock.Advance(TimeSpan.FromHours(25));
        var ingestionsBefore = Query(db => db.Ingestions.Count());
        var queuedBefore = _factory.Queue.Ingestions.Count;

        _race.Arm(key);
        var response = await Upload(client, key, "day-2.pdf", Bytes("day two"));

        _race.Winner.ShouldNotBeNull("the rival replaced the stale row while the request was between its lookup and its save");
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        response.Headers.Location!.ToString().ShouldBe(_race.Winner.Location);
        var replayed = (await response.Content.ReadFromJsonAsync<IngestionCreated>(ApiFactory.Json, Ct))!;
        replayed.Id.ShouldBe(_race.WinnerIngestionId, "the loser answers with the winner's response, exactly what its retry would get");
        replayed.Id.ShouldNotBe(dayOneId);

        Query(db => db.Ingestions.Count()).ShouldBe(ingestionsBefore, "the loser's ingestion was rolled back with its key row");
        _factory.Queue.Ingestions.Count.ShouldBe(queuedBefore, "nothing is queued for a rolled-back ingestion");
        var rows = Query(db => db.IdempotencyKeys.Where(k => k.Key == key).ToList());
        rows.ShouldHaveSingleItem().Id.ShouldBe(_race.Winner.Id);
    }

    [Fact]
    public async Task A_rival_with_a_different_body_makes_the_loser_a_422_like_any_reused_key()
    {
        using var client = Client("uploader");
        var key = "nightly-" + Guid.NewGuid().ToString("N");
        (await Upload(client, key, "day-1.pdf", Bytes("day one"))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        _factory.Clock.Advance(TimeSpan.FromHours(25));

        _race.Arm(key, differentRequest: true);
        var response = await Upload(client, key, "day-2.pdf", Bytes("day two"));

        _race.Winner.ShouldNotBeNull();
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Idempotency key reused");
        Query(db => db.IdempotencyKeys.Where(k => k.Key == key).Select(k => k.Id).ToList()).ShouldBe([_race.Winner.Id]);
    }

    [Fact]
    public async Task Two_simultaneous_uploads_reusing_an_expired_key_both_succeed_with_one_ingestion()
    {
        using var first = Client("racer-1");
        using var second = Client("racer-2");
        var key = "nightly-" + Guid.NewGuid().ToString("N");
        var dayOne = await Upload(first, key, "day-1.pdf", Bytes("day one"));
        dayOne.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var dayOneId = (await dayOne.Content.ReadFromJsonAsync<IngestionCreated>(ApiFactory.Json, Ct))!.Id;
        _factory.Clock.Advance(TimeSpan.FromHours(25));
        var dayTwo = Bytes("day two");

        var responses = await Task.WhenAll(Upload(first, key, "day-2.pdf", dayTwo), Upload(second, key, "day-2.pdf", dayTwo));

        // Whether the two interleave (the loser's delete finds nothing and the winner's response is replayed) or run
        // one after the other (the second finds the fresh row inside the window and replays it) is the scheduler's
        // business; either way both callers get the same 202 and exactly one ingestion exists.
        var outcome = string.Join(", ", await Task.WhenAll(responses.Select(async r => $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync(Ct)}")));
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Accepted, outcome);
        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<IngestionCreated>(ApiFactory.Json, Ct))!.Id));
        ids.Distinct().ShouldHaveSingleItem().ShouldNotBe(dayOneId);
        var id = ids[0];
        responses.ShouldAllBe(r => r.Headers.Location!.ToString() == $"/api/v1/ingestions/{id}");

        Query(db => db.Ingestions.Count(i => i.Id == id)).ShouldBe(1);
        _factory.Queue.Ingestions.Count(m => m.IngestionId == id).ShouldBe(1, "the ingestion is queued once");
        var row = Query(db => db.IdempotencyKeys.Where(k => k.Key == key).ToList()).ShouldHaveSingleItem();
        row.CreatedAt.ShouldBe(_factory.Clock.GetUtcNow());
        row.Location.ShouldBe($"/api/v1/ingestions/{id}");
    }

    private HttpClient Client(string name)
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Mint(new TokenSpec(name, [Roles.Reviewer], TenantA)));
        return client;
    }

    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes("%PDF-1.4 " + text + " " + Guid.NewGuid());

    private static async Task<HttpResponseMessage> Upload(HttpClient client, string idempotencyKey, string fileName, byte[] bytes)
    {
        using var body = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        body.Add(part, "files", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, IngestionsUri) { Content = body };
        request.Headers.Add(IngestionLimits.IdempotencyKeyHeader, idempotencyKey);
        return await client.SendAsync(request, Ct);
    }

    private T Query<T>(Func<DocProcDbContext, T> query)
    {
        using var db = new FileSqliteDbContextFactory(_connectionString).CreatePlatformScoped();
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        return query(db);
    }

    /// <summary>
    /// Plays the winning upload: when the armed key is about to be saved, the stale row is replaced through a context
    /// of its own (deleted and re-added with a fresh timestamp, the same request hash and a response of its own), so
    /// the request's delete of that row affects nothing and its save fails the way the real race fails.
    /// </summary>
    private sealed class ExpiredKeyRace(Func<DocProcDbContext> contexts) : SaveChangesInterceptor
    {
        private string? _armedKey;
        private bool _differentRequest;

        public IdempotencyKey? Winner { get; private set; }

        public Guid WinnerIngestionId { get; private set; }

        public void Arm(string key, bool differentRequest = false)
        {
            _armedKey = key;
            _differentRequest = differentRequest;
            Winner = null;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_armedKey is { } key && eventData.Context is { } context
                && context.ChangeTracker.Entries<IdempotencyKey>().SingleOrDefault(e => e.State == EntityState.Added && e.Entity.Key == key)?.Entity is { } added)
            {
                _armedKey = null;
                using var rival = contexts();
                rival.IdempotencyKeys.Remove(rival.IdempotencyKeys.Single(k => k.Key == key));
                WinnerIngestionId = Guid.CreateVersion7();
                Winner = new IdempotencyKey
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = added.TenantId,
                    Key = key,
                    RequestHash = _differentRequest ? new string('f', 64) : added.RequestHash,
                    StatusCode = (int)HttpStatusCode.Accepted,
                    ResponseJson = JsonSerializer.Serialize(new IngestionCreated(WinnerIngestionId, IngestionStatus.Received, []), ApiFactory.Json),
                    Location = $"/api/v1/ingestions/{WinnerIngestionId}",
                    CreatedAt = added.CreatedAt,
                };
                rival.IdempotencyKeys.Add(Winner);
                rival.SaveChanges();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class FileSqliteDbContextFactory(string connectionString) : ISystemDbContextFactory
    {
        public DocProcDbContext CreatePlatformScoped()
        {
            var options = new DbContextOptionsBuilder<DocProcDbContext>().UseSqlite(connectionString).Options;
            return new DocProcDbContext(options, TenantContext.Platform());
        }
    }
}
