using System.Net;
using System.Net.Http.Json;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocProc.Api.Tests;

/// <summary>
/// The write races of the review API, run against a host whose requests really do run side by side at the database.
/// <see cref="ApiFactory"/> shares one in-memory <c>SqliteConnection</c> between every context, which is fine for
/// sequential tests but not for simultaneous requests: EF re-registers its SQL functions on the shared handle while
/// another request's statement is active (SQLITE_BUSY), and one connection cannot hold two transactions. This class
/// keeps the parent factory's Program, fake clock and capturing queue but points the context at a SQLite file in WAL
/// mode with a connection per context, so the conditional updates are exercised the way SQL Server would run them.
/// Every test gets a parent factory of its own (not a class fixture): deriving a host re-runs the parent's seed on
/// its in-memory connection, which only works once per parent.
/// </summary>
public sealed class ConcurrencyTests : IDisposable
{
    private const int Racers = 6;

    private static readonly string TenantA = ApiFactory.TenantA.ToString();

    private readonly ApiFactory _factory = new();
    private readonly string _directory;
    private readonly string _connectionString;
    private readonly WebApplicationFactory<Program> _host;

    public ConcurrencyTests()
    {
        var factory = _factory;
        _directory = Path.Combine(Path.GetTempPath(), "docproc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "concurrency.db"), Pooling = false }.ToString();
        var contexts = new FileSqliteDbContextFactory(_connectionString);

        _host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<DocProcDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<DocProcDbContext>();
            services.RemoveAll<ISystemDbContextFactory>();
            services.AddDbContext<DocProcDbContext>(o => o.UseSqlite(_connectionString));
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
    public async Task Simultaneous_approvals_decide_the_document_exactly_once()
    {
        var (documentId, taskId) = SeedReviewableInvoice();
        var clients = Enumerable.Range(0, Racers).Select(i => Client($"approver-{i}")).ToList();
        try
        {
            var uri = new Uri($"/api/v1/documents/{documentId}:approve", UriKind.Relative);
            var responses = await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync(uri, new ApproveDocumentRequest("mine"), Ct)));

            // Which caller wins depends on scheduling; that exactly one does is the contract.
            responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
            responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(Racers - 1);

            _factory.Queue.Documents.Count(m => m.DocumentId == documentId && m.Stage == PipelineStage.Publish).ShouldBe(1, "one publish message");
            var document = Query(db => db.Documents.AsNoTracking().Single(d => d.Id == documentId));
            document.Status.ShouldBe(DocumentStatus.Approved);
            var winner = (await responses.Single(r => r.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!;
            document.ApprovedBy.ShouldBe(winner.ApprovedBy);
            Query(db => db.DocumentEvents.Count(e => e.DocumentId == documentId && e.EventType == DocumentEventTypes.Approved)).ShouldBe(1, "one approval event");
            Query(db => db.ReviewTasks.AsNoTracking().Single(t => t.Id == taskId).Status).ShouldBe(ReviewTaskStatus.Done);

            foreach (var loser in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            {
                var problem = await loser.Content.ReadAsStringAsync(Ct);
                problem.ShouldSatisfyAllConditions(
                    () => problem.ShouldContain("Document"),
                    () => (problem.Contains("Document changed", StringComparison.Ordinal) || problem.Contains("Invalid transition", StringComparison.Ordinal)).ShouldBeTrue("a loser either lost the conditional update or read the document after the winner"));
            }
        }
        finally
        {
            clients.ForEach(c => c.Dispose());
        }
    }

    [Fact]
    public async Task Simultaneous_claims_assign_the_task_to_exactly_one_reviewer()
    {
        var (_, taskId) = SeedReviewableInvoice();
        var names = Enumerable.Range(0, Racers).Select(i => $"claimer-{i}").ToList();
        var clients = names.Select(Client).ToList();
        try
        {
            var uri = new Uri($"/api/v1/review-tasks/{taskId}:claim", UriKind.Relative);
            var responses = await Task.WhenAll(clients.Select(c => c.PostAsync(uri, null, Ct)));

            responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1, "the conditional update lets exactly one claim through");
            responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(Racers - 1);

            var stored = Query(db => db.ReviewTasks.AsNoTracking().Single(t => t.Id == taskId));
            stored.Status.ShouldBe(ReviewTaskStatus.InProgress);
            names.ShouldContain(stored.AssignedTo);
            var winner = (await responses.Single(r => r.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<ReviewTaskDto>(ApiFactory.Json, Ct))!;
            winner.AssignedTo.ShouldBe(stored.AssignedTo);
            foreach (var loser in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            {
                (await loser.Content.ReadAsStringAsync(Ct)).ShouldContain(stored.AssignedTo!);
            }
        }
        finally
        {
            clients.ForEach(c => c.Dispose());
        }
    }

    private HttpClient Client(string name)
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiFactory.Mint(new TokenSpec(name, [Roles.Reviewer], TenantA)));
        return client;
    }

    private T Query<T>(Func<DocProcDbContext, T> query)
    {
        using var db = new FileSqliteDbContextFactory(_connectionString).CreatePlatformScoped();
        return query(db);
    }

    /// <summary>A NeedsReview invoice of tenant A with one open review task; no blobs, the decisions never read them.</summary>
    private (Guid DocumentId, Guid TaskId) SeedReviewableInvoice()
    {
        _ = _host.Server;
        var now = _factory.Clock.GetUtcNow();
        var tenantId = ApiFactory.TenantA;
        using var db = new FileSqliteDbContextFactory(_connectionString).CreatePlatformScoped();

        var channel = db.IngestionChannels.FirstOrDefault(c => c.TenantId == tenantId && c.Type == ChannelType.ApiUpload)
            ?? db.IngestionChannels.Add(new IngestionChannel { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = "API upload", Type = ChannelType.ApiUpload, CreatedAt = now }).Entity;
        var ingestion = TestData.Ingestion(tenantId, channel.Id, now);
        var item = TestData.Item(ingestion, now);
        ingestion.Items.Add(item);
        db.Ingestions.Add(ingestion);

        var invoice = TestInvoices.Valid();
        var document = new Document
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            IngestionItemId = item.Id,
            Type = DocumentType.Invoice,
            Status = DocumentStatus.NeedsReview,
            CanonicalJson = CanonicalDocumentSerializer.Serialize(invoice),
            CanonicalSchemaVersion = CanonicalDocumentSerializer.SchemaVersion,
            DocumentNumber = invoice.InvoiceNumber.Value!.Verbatim,
            DocumentNumberNormalized = invoice.InvoiceNumber.Value.Normalized,
            Currency = "EUR",
            TotalGross = invoice.TotalGross.Value.Amount,
            CreatedAt = now,
            UpdatedAt = now,
        };
        document.Events.Add(new DocumentEvent { TenantId = tenantId, DocumentId = document.Id, EventType = DocumentEventTypes.Received, ToStatus = DocumentStatus.Received, Actor = "pipeline", At = now });
        var task = new ReviewTask { Id = Guid.CreateVersion7(), TenantId = tenantId, DocumentId = document.Id, Reason = "review", CreatedAt = now };
        document.ReviewTasks.Add(task);
        db.Documents.Add(document);
        db.SaveChanges();
        return (document.Id, task.Id);
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
