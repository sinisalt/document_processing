using System.Text.Json;
using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Runner;
using DocProc.Pipeline.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Pipeline.Tests;

/// <summary>
/// docs/11 §13.5 in the worker's composition: the decision commits the outbox row, the publish stage hands it on
/// (<see cref="OutboxMessage.FannedOutAt"/>), and <see cref="PublishSweeperHostedService"/> publishes, for every tenant,
/// the settlements whose publish message was lost, with the timeline saying so.
/// </summary>
public sealed class PublishSweeperTests
{
    private const string XmlMime = "application/xml";

    private static readonly Guid TenantB = DevSeed.TenantB;

    [Fact]
    public async Task An_auto_approval_commits_its_outbox_row_with_the_decision_and_the_nudge_hands_it_on()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.xml", XmlMime, PipelineTestHost.Fixture("peppol-bis3-invoice.xml"))]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.Status.ShouldBe(DocumentStatus.AutoApproved);
        var row = (await OutboxAsync(host, document.Id)).ShouldHaveSingleItem();
        row.CreatedAt.ShouldBe(document.ApprovedAt!.Value, "written in the save that auto-approved the document");
        row.FannedOutAt.ShouldNotBeNull("the publish stage handed it on");

        var published = (await host.EventsAsync(document.Id))[^1];
        published.EventType.ShouldBe(DocumentEventTypes.StageCompleted);
        Details(published).EnumerateObject().Select(p => p.Name).ShouldBe(["stage", "eventType", "outboxMessageId"]);
        Details(published).GetProperty("outboxMessageId").GetGuid().ShouldBe(row.Id);

        host.Clock.Advance(host.Options.PublishSweepGrace + TimeSpan.FromSeconds(1));
        (await Sweeper(host).SweepOnceAsync(CancellationToken.None)).ShouldBe(0, "nothing was lost");
    }

    [Fact]
    public async Task A_decision_whose_publish_message_was_lost_is_published_by_the_sweeper_and_only_once()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var settledAt = host.Clock.GetUtcNow();
        // What approve leaves behind when the process dies between the commit and the enqueue.
        var approved = await SettleAsync(host, PipelineTestHost.TenantA, DocumentStatus.Approved, settledAt, decisionRow: true);
        // A tenant B decision from before the outbox was written with the decision: no row at all.
        var rejected = await SettleAsync(host, TenantB, DocumentStatus.Rejected, settledAt, decisionRow: false);

        (await Sweeper(host).SweepOnceAsync(CancellationToken.None)).ShouldBe(0, "within the grace the nudge may still come");
        host.Clock.Advance(host.Options.PublishSweepGrace + TimeSpan.FromSeconds(1));
        (await Sweeper(host).SweepOnceAsync(CancellationToken.None)).ShouldBe(2, "every tenant's settlements");
        await host.RunUntilIdleAsync();

        foreach (var (id, eventType) in new[] { (approved, DocumentEventTypes.Approved), (rejected, DocumentEventTypes.Rejected) })
        {
            var row = (await OutboxAsync(host, id)).ShouldHaveSingleItem();
            row.EventType.ShouldBe(eventType);
            row.FannedOutAt.ShouldNotBeNull();
            var published = (await host.EventsAsync(id))[^1];
            published.EventType.ShouldBe(DocumentEventTypes.StageCompleted);
            Details(published).GetProperty("stage").GetString().ShouldBe(nameof(PipelineStage.Publish));
            Details(published).GetProperty("eventType").GetString().ShouldBe(eventType);
            Details(published).GetProperty("outboxMessageId").GetGuid().ShouldBe(row.Id);
            Details(published).GetProperty("recoveredBy").GetString().ShouldBe(PipelineMessageOrigins.PublishSweeper);
        }

        // Published once: another sweep finds nothing, a late nudge is dropped.
        var events = (await host.EventsAsync(approved)).Count;
        (await Sweeper(host).SweepOnceAsync(CancellationToken.None)).ShouldBe(0);
        await host.PipelineQueue.EnqueueAsync(new ProcessDocument(PipelineTestHost.TenantA, approved, PipelineStage.Publish), CancellationToken.None);
        await host.PipelineQueue.EnqueueAsync(new ProcessDocument(PipelineTestHost.TenantA, approved, PipelineStage.Publish, Origin: PipelineMessageOrigins.PublishSweeper), CancellationToken.None);
        await host.RunUntilIdleAsync();
        (await host.EventsAsync(approved)).Count.ShouldBe(events);
        (await OutboxAsync(host, approved)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_sweeper_runs_on_its_own_after_a_restart()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var approved = await SettleAsync(host, PipelineTestHost.TenantA, DocumentStatus.Approved, host.Clock.GetUtcNow(), decisionRow: true);

        // The hosted loop waits one interval on the host clock, then sweeps; past the grace it picks the row up.
        host.Clock.Advance(host.Options.PublishSweepGrace + host.Options.PublishSweepInterval);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((await OutboxAsync(host, approved)).Single().FannedOutAt is null)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the sweeper's loop did not publish the lost decision");
            if (host.Queue.InFlight == 0)
            {
                // Each wait of the loop is one interval of the fake clock; keep it moving until the sweep has run.
                host.Clock.Advance(host.Options.PublishSweepInterval);
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await host.RunUntilIdleAsync();
        Details((await host.EventsAsync(approved))[^1]).GetProperty("recoveredBy").GetString().ShouldBe(PipelineMessageOrigins.PublishSweeper);
    }

    [Fact]
    public async Task The_sweeper_selects_exactly_the_settlements_the_publish_rule_calls_unpublished()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var now = host.Clock.GetUtcNow();
        var old = now - TimeSpan.FromMinutes(10);
        var ancient = now - host.Options.PublishSweepLookback - TimeSpan.FromHours(1);

        var expected = new List<Guid>
        {
            await SettleAsync(host, PipelineTestHost.TenantA, DocumentStatus.Approved, old, decisionRow: true),
            await SettleAsync(host, PipelineTestHost.TenantA, DocumentStatus.AutoApproved, old, decisionRow: false),
            await SettleAsync(host, TenantB, DocumentStatus.Rejected, old, decisionRow: true),
            // Rejected now, but only the earlier approval's row exists (handed on): the rejection is unpublished.
            await SettleAsync(host, PipelineTestHost.TenantA, DocumentStatus.Rejected, old, decisionRow: false, extra: (DocumentEventTypes.Approved, old - TimeSpan.FromMinutes(5), true)),
            // Approved again after an earlier approval was published: that row predates this settlement.
            await SettleAsync(host, PipelineTestHost.TenantA, DocumentStatus.Approved, old, decisionRow: false, extra: (DocumentEventTypes.Approved, old - TimeSpan.FromMinutes(1), true)),
        };
        var notExpected = new List<Guid>
        {
            await SettleAsync(host, PipelineTestHost.TenantA, DocumentStatus.Approved, old, decisionRow: true, fanned: true),
            await SettleAsync(host, PipelineTestHost.TenantA, DocumentStatus.Approved, now - TimeSpan.FromSeconds(30), decisionRow: true),
            await SettleAsync(host, PipelineTestHost.TenantA, DocumentStatus.Approved, ancient, decisionRow: true),
            // A rejection that was reprocessed: no longer settled, its old row is not the sweeper's business.
            await SettleAsync(host, PipelineTestHost.TenantA, DocumentStatus.Received, old, decisionRow: false, extra: (DocumentEventTypes.Rejected, old, false)),
            await SettleAsync(host, PipelineTestHost.TenantA, DocumentStatus.NeedsReview, old, decisionRow: false),
        };

        var selected = await host.WithDbAsync(db => DocumentOutbox.Unpublished(db, now - host.Options.PublishSweepGrace, now - host.Options.PublishSweepLookback).ToListAsync());
        selected.Select(s => s.DocumentId).Order().ShouldBe(expected.Order());
        selected.Select(s => s.SettledAt).ShouldBeInOrder(SortDirection.Ascending);
        selected.Single(s => s.DocumentId == expected[2]).TenantId.ShouldBe(TenantB);

        // The same answer as the publish stage's own rule, applied in memory to every settled document in the window.
        var inMemory = await host.WithDbAsync(async db =>
        {
            var documents = await db.Documents.AsNoTracking().ToListAsync();
            var rows = (await db.OutboxMessages.AsNoTracking().ToListAsync()).ToLookup(m => m.DocumentId);
            return documents
                .Where(d => DocumentStateMachine.IsSettled(d.Status))
                .Where(d => DocumentOutbox.SettledAt(d.ApprovedAt, d.UpdatedAt) is var at && at <= now - host.Options.PublishSweepGrace && at >= now - host.Options.PublishSweepLookback)
                .Where(d => !rows[d.Id].Any(m => m.FannedOutAt is not null && DocumentOutbox.IsOfSettlement(m.EventType, m.CreatedAt, d.Status, d.ApprovedAt, d.UpdatedAt)))
                .Select(d => d.Id)
                .Order()
                .ToList();
        });
        inMemory.ShouldBe(expected.Order().ToList());
        notExpected.ShouldAllBe(id => !inMemory.Contains(id));

        // The batch size bounds one sweep.
        var batch = await host.WithDbAsync(db => DocumentOutbox.Unpublished(db, now, now - host.Options.PublishSweepLookback).Take(2).ToListAsync());
        batch.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Two_publishers_cannot_both_claim_a_row()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var approved = await SettleAsync(host, PipelineTestHost.TenantA, DocumentStatus.Approved, host.Clock.GetUtcNow(), decisionRow: true);
        var factory = host.Services.GetRequiredService<ISystemDbContextFactory>();

        await using var first = factory.CreatePlatformScoped();
        await using var second = factory.CreatePlatformScoped();
        var mine = await first.OutboxMessages.SingleAsync(m => m.DocumentId == approved, TestContext.Current.CancellationToken);
        var theirs = await second.OutboxMessages.SingleAsync(m => m.DocumentId == approved, TestContext.Current.CancellationToken);
        mine.FannedOutAt = host.Clock.GetUtcNow();
        theirs.FannedOutAt = host.Clock.GetUtcNow();

        await first.SaveChangesAsync(TestContext.Current.CancellationToken);
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task A_publish_message_for_a_document_that_is_no_longer_settled_is_dropped()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var reprocessed = await SettleAsync(host, PipelineTestHost.TenantA, DocumentStatus.Received, host.Clock.GetUtcNow(), decisionRow: false, extra: (DocumentEventTypes.Rejected, host.Clock.GetUtcNow(), false));
        using var scope = host.CreateTenantScope(PipelineTestHost.TenantA);
        var publish = scope.ServiceProvider.GetServices<IStageHandler>().Single(h => h.Stage == PipelineStage.Publish);

        (await publish.HandleAsync(new ProcessDocument(PipelineTestHost.TenantA, reprocessed, PipelineStage.Publish, Origin: PipelineMessageOrigins.PublishSweeper), CancellationToken.None)).ShouldBe(StageOutcome.Skipped);
        (await OutboxAsync(host, reprocessed)).Single().FannedOutAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_host_without_a_database_starts_with_the_sweeper_off()
    {
        // The OpenAPI export starts the API without persistence; the sweeper must not stop that host.
        using var empty = new ServiceCollection().BuildServiceProvider();
        using var sweeper = new PublishSweeperHostedService(
            empty,
            new Queue.InProcessPipelineQueue(),
            TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new PipelineOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PublishSweeperHostedService>.Instance);

        await sweeper.StartAsync(TestContext.Current.CancellationToken);
        await sweeper.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await sweeper.StopAsync(TestContext.Current.CancellationToken);
        await Should.ThrowAsync<InvalidOperationException>(() => sweeper.SweepOnceAsync(TestContext.Current.CancellationToken));
    }

    private static PublishSweeperHostedService Sweeper(PipelineTestHost host) => host.Services.GetRequiredService<PublishSweeperHostedService>();

    private static Task<List<OutboxMessage>> OutboxAsync(PipelineTestHost host, Guid documentId) =>
        host.WithDbAsync(db => db.OutboxMessages.AsNoTracking().Where(m => m.DocumentId == documentId).ToListAsync());

    private static JsonElement Details(DocumentEvent e) => JsonDocument.Parse(e.DetailsJson!).RootElement;

    /// <summary>
    /// A document in <paramref name="status"/>, settled (or last changed) at <paramref name="at"/>, with the outbox row
    /// its decision writes (<paramref name="decisionRow"/>; handed on when <paramref name="fanned"/>) and one more row.
    /// </summary>
    private static async Task<Guid> SettleAsync(
        PipelineTestHost host,
        Guid tenantId,
        DocumentStatus status,
        DateTimeOffset at,
        bool decisionRow,
        bool fanned = false,
        (string EventType, DateTimeOffset CreatedAt, bool Fanned)? extra = null)
    {
        var ingestion = await host.IngestAsync(tenantId, [new IngestFile($"{Guid.NewGuid():N}.xml", XmlMime, System.Text.Encoding.UTF8.GetBytes($"<settled id=\"{Guid.NewGuid()}\"/>"))], enqueue: false);
        var received = await host.AddReceivedDocumentAsync(ingestion);
        await host.WithDbAsync(async db =>
        {
            var document = await db.Documents.SingleAsync(d => d.Id == received.Id);
            document.Status = status;
            document.UpdatedAt = at;
            document.ApprovedAt = status is DocumentStatus.Approved or DocumentStatus.AutoApproved ? at : null;
            if (decisionRow)
            {
                DocumentOutbox.Add(db, document, at).FannedOutAt = fanned ? at : null;
            }

            if (extra is { } row)
            {
                db.OutboxMessages.Add(new OutboxMessage
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    DocumentId = document.Id,
                    EventType = row.EventType,
                    PayloadJson = "{}",
                    CreatedAt = row.CreatedAt,
                    FannedOutAt = row.Fanned ? row.CreatedAt : null,
                });
            }

            await db.SaveChangesAsync();
        });
        return received.Id;
    }
}
