using DocProc.Application.Pipeline;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using DocProc.Pipeline.Stages;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocProc.Pipeline.Tests;

/// <summary>
/// The cross-process half of duplicate detection (docs/03 "Implementation"): the ownership index, not a query, decides
/// who owns the document when two processes accept the same bytes at the same time.
/// </summary>
public class IngestOwnershipTests
{
    private const string XmlMime = "application/xml";

    [Fact]
    public async Task An_owner_committed_between_the_query_and_the_save_wins_and_the_attempt_relinks_its_items()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var shared = PipelineTestHost.FixtureVariant("raced bytes");
        var unrelated = PipelineTestHost.FixtureVariant("nobody else has these");

        // The ingestion under test: two copies of the raced bytes (the second duplicates the first within the
        // ingestion) and one unrelated file, so the attempt both loses an owner and keeps one.
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA,
            [new IngestFile("a.xml", XmlMime, shared), new IngestFile("b.xml", XmlMime, shared), new IngestFile("other.xml", XmlMime, unrelated)],
            enqueue: false);

        // Another process accepts the same bytes after this handler ran its duplicate query and before it saves.
        // Its item is newer, so no "strictly earlier" rule covers it: only the database can reject the second owner.
        Ingestion? owner = null;
        var interceptor = new OwnerCommitsFirst(async () =>
        {
            host.Clock.Advance(TimeSpan.FromSeconds(1));
            owner = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("theirs.xml", XmlMime, shared)], enqueue: false);
            await host.AddReceivedDocumentAsync(owner);
        });

        var enqueued = new RecordingQueue();
        await using (var db = NewContext(host, interceptor))
        {
            var handler = new IngestHandler(
                db,
                host.Services.GetRequiredService<PipelineBlobs>(),
                enqueued,
                new DocumentTimeline(db, host.Clock),
                host.Services.GetRequiredService<IOptions<PipelineOptions>>(),
                host.Services.GetRequiredService<ILogger<IngestHandler>>());
            await handler.HandleAsync(new ProcessIngestion(PipelineTestHost.TenantA, ingestion.Id), TestContext.Current.CancellationToken);
        }

        interceptor.Fired.ShouldBe(1, "the owner was inserted exactly once, between the query and the first save");
        var ownerItem = (await host.ItemsAsync(owner.ShouldNotBeNull().Id)).ShouldHaveSingleItem();
        ownerItem.Disposition.ShouldBe(ItemDisposition.Accepted);

        var items = (await host.ItemsAsync(ingestion.Id)).ToDictionary(i => i.FileName, StringComparer.Ordinal);
        items.Count.ShouldBe(3);
        foreach (var raced in new[] { items["a.xml"], items["b.xml"] })
        {
            raced.Disposition.ShouldBe(ItemDisposition.Duplicate, $"{raced.FileName}: the committed owner wins, however new it is");
            raced.DuplicateOfItemId.ShouldBe(ownerItem.Id, $"{raced.FileName} points at the owner, not at the item of this ingestion that lost");
            raced.DispositionReason.ShouldNotBeNull();
        }

        items["other.xml"].Disposition.ShouldBe(ItemDisposition.Accepted, "the unrelated file is unaffected by the race");

        var ownedBySharedBytes = await host.WithDbAsync(db => db.Documents.AsNoTracking().Where(d => d.IngestionItem!.Sha256 == ownerItem.Sha256).ToListAsync());
        ownedBySharedBytes.ShouldHaveSingleItem().IngestionItemId.ShouldBe(ownerItem.Id);
        var otherDocument = await host.WithDbAsync(db => db.Documents.AsNoTracking().SingleAsync(d => d.IngestionItemId == items["other.xml"].Id));

        // Nothing of the discarded document reached the database: no row, no event, no message.
        var documentIds = await host.WithDbAsync(db => db.Documents.AsNoTracking().Select(d => d.Id).ToListAsync());
        documentIds.Order().ShouldBe(new[] { ownedBySharedBytes[0].Id, otherDocument.Id }.Order());
        (await host.WithDbAsync(db => db.DocumentEvents.AsNoTracking().CountAsync(e => !documentIds.Contains(e.DocumentId)))).ShouldBe(0);
        enqueued.Documents.ShouldBe([otherDocument.Id]);

        var stored = await host.WithDbAsync(db => db.Ingestions.AsNoTracking().SingleAsync(i => i.Id == ingestion.Id));
        stored.Status.ShouldBe(IngestionStatus.Unpacked);
        stored.Version.ShouldBe(1, "the failed save committed nothing; the retried one is the only commit");
    }

    [Fact]
    public async Task A_second_accepted_item_for_the_same_tenant_and_bytes_is_rejected_by_the_index()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var bytes = PipelineTestHost.FixtureVariant("two owners");
        var first = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("first.xml", XmlMime, bytes)], enqueue: false);
        var second = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("second.xml", XmlMime, bytes)], enqueue: false);
        var otherTenant = await host.IngestAsync(DevSeed.TenantB, [new IngestFile("theirs.xml", XmlMime, bytes)], enqueue: false);

        await host.WithDbAsync(async db =>
        {
            (await db.IngestionItems.SingleAsync(i => i.IngestionId == first.Id)).Disposition = ItemDisposition.Accepted;
            (await db.IngestionItems.SingleAsync(i => i.IngestionId == otherTenant.Id)).Disposition = ItemDisposition.Accepted;
            await db.SaveChangesAsync();
        });

        var violation = await host.WithDbAsync(async db =>
        {
            (await db.IngestionItems.SingleAsync(i => i.IngestionId == second.Id)).Disposition = ItemDisposition.Accepted;
            return await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });
        IngestionItemOwnership.IsViolation(violation).ShouldBeTrue(violation.InnerException?.Message);

        // Any other disposition is free to repeat the bytes: duplicates and candidates are what the index leaves alone.
        await host.WithDbAsync(async db =>
        {
            var item = await db.IngestionItems.SingleAsync(i => i.IngestionId == second.Id);
            item.Disposition = ItemDisposition.Duplicate;
            await db.SaveChangesAsync();
        });

        IngestionItemOwnership.IsViolation(new DbUpdateException("unrelated", new InvalidOperationException("FOREIGN KEY constraint failed"))).ShouldBeFalse();
    }

    private static DocProcDbContext NewContext(PipelineTestHost host, IInterceptor interceptor)
    {
        var location = host.Services.GetRequiredService<SqliteDatabaseLocation>();
        var options = PersistenceOptions.ConfigureSqlite(new DbContextOptionsBuilder<DocProcDbContext>(), location.ConnectionString).AddInterceptors(interceptor);
        return new DocProcDbContext(options.Options, TenantContext.ForTenant(PipelineTestHost.TenantA));
    }

    /// <summary>Runs <paramref name="commitOwner"/> once, right before the first save that inserts a document: the other process's commit lands in the window.</summary>
    private sealed class OwnerCommitsFirst(Func<Task> commitOwner) : SaveChangesInterceptor
    {
        private int _fired;

        public int Fired => _fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context
                && context.ChangeTracker.Entries<Document>().Any(e => e.State == EntityState.Added)
                && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
            {
                await commitOwner();
            }

            return result;
        }
    }

    private sealed class RecordingQueue : IPipelineQueue
    {
        public List<Guid> Documents { get; } = [];

        public Task EnqueueAsync(ProcessIngestion message, CancellationToken cancellationToken) => throw new InvalidOperationException("stage 1 never queues an ingestion");

        public Task EnqueueAsync(ProcessDocument message, CancellationToken cancellationToken)
        {
            Documents.Add(message.DocumentId);
            return Task.CompletedTask;
        }

        public Task EnqueueAsync(ProcessDocument message, TimeSpan delay, CancellationToken cancellationToken) => throw new InvalidOperationException("stage 1 never delays a document");
    }
}
