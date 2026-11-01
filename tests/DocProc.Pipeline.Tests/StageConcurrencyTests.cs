using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Pipeline.Tests;

/// <summary>The two layers of docs/03 "Idempotency and retries" against duplicate deliveries: per-key serialisation and the concurrency token.</summary>
public class StageConcurrencyTests
{
    private const string XmlMime = "application/xml";

    [Fact]
    public async Task The_same_normalize_message_delivered_twice_normalizes_once()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.xml", XmlMime, PipelineTestHost.Fixture("peppol-bis3-invoice.xml"))], enqueue: false);
        var document = await host.AddReceivedDocumentAsync(ingestion);
        var message = new ProcessDocument(PipelineTestHost.TenantA, document.Id, PipelineStage.Normalize);

        await host.PipelineQueue.EnqueueAsync(message, CancellationToken.None);
        await host.PipelineQueue.EnqueueAsync(message, CancellationToken.None);
        await host.RunUntilIdleAsync();

        var events = await host.EventsAsync(document.Id);
        events.Count(e => e.FromStatus == DocumentStatus.Received && e.ToStatus == DocumentStatus.Normalized).ShouldBe(1);
        events.Count(e => e.ToStatus == DocumentStatus.Normalized).ShouldBe(1);
        events.ShouldNotContain(e => e.EventType == DocumentEventTypes.StageFailed);
        var after = await host.DocumentAsync(document.Id);
        after.FailureReason.ShouldBeNull();
        after.Status.ShouldBe(DocumentStatus.AutoApproved, "the one normalize run carried the document through the rest of the pipeline");
        after.Version.ShouldBeGreaterThan(0, "every stage that wrote the row bumped the token");
    }

    [Fact]
    public async Task A_write_from_a_second_context_raises_the_concurrency_exception_and_the_handler_skips()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.xml", XmlMime, PipelineTestHost.Fixture("peppol-bis3-invoice.xml"))], enqueue: false);
        var document = await host.AddReceivedDocumentAsync(ingestion);
        var cancellation = TestContext.Current.CancellationToken;

        // Layer 1, the token itself: two contexts load version 0, both write, the second one loses.
        await using (var first = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped())
        await using (var second = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped())
        {
            var mine = await first.Documents.SingleAsync(d => d.Id == document.Id, cancellation);
            var theirs = await second.Documents.SingleAsync(d => d.Id == document.Id, cancellation);
            mine.Version.ShouldBe(0);

            mine.FailureReason = "first writer";
            await first.SaveChangesAsync(cancellation);
            mine.Version.ShouldBe(1);

            theirs.FailureReason = "second writer";
            await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(cancellation));
        }

        (await host.DocumentAsync(document.Id)).FailureReason.ShouldBe("first writer");

        // Layer 2, the handler: it loaded the document (version 1), another writer moved on, so its result is dropped.
        using var scope = host.CreateTenantScope(PipelineTestHost.TenantA);
        var db = scope.ServiceProvider.GetRequiredService<DocProcDbContext>();
        var loaded = await db.Documents.SingleAsync(d => d.Id == document.Id, cancellation);
        loaded.Version.ShouldBe(1);

        await host.WithDbAsync(async other =>
        {
            var row = await other.Documents.SingleAsync(d => d.Id == document.Id);
            row.FailureReason = null;
            row.UpdatedAt = host.Clock.GetUtcNow().AddSeconds(1);
            await other.SaveChangesAsync();
        });

        var handler = scope.ServiceProvider.GetServices<IStageHandler>().Single(h => h.Stage == PipelineStage.Normalize);
        var outcome = await handler.HandleAsync(new ProcessDocument(PipelineTestHost.TenantA, document.Id, PipelineStage.Normalize), cancellation);

        outcome.ShouldBe(StageOutcome.Skipped);
        var stored = await host.DocumentAsync(document.Id);
        stored.Status.ShouldBe(DocumentStatus.Received, "the stale attempt wrote nothing");
        stored.Version.ShouldBe(2, "only the other writer's update landed");
        stored.EmbeddedXmlBlob.ShouldBeNull();
        (await host.EventsAsync(document.Id)).Select(e => e.EventType).ShouldBe([DocumentEventTypes.Received]);
    }

    [Fact]
    public async Task Ingestions_and_review_tasks_carry_the_token_too()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.xml", XmlMime, PipelineTestHost.Fixture("peppol-bis3-invoice.xml"))]);
        await host.RunUntilIdleAsync();

        var stored = await host.WithDbAsync(db => db.Ingestions.AsNoTracking().SingleAsync(i => i.Id == ingestion.Id));
        stored.Status.ShouldBe(IngestionStatus.Unpacked);
        stored.Version.ShouldBe(1, "stage 1 updated the row once");

        var cancellation = TestContext.Current.CancellationToken;
        await using var first = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped();
        await using var second = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped();
        var mine = await first.Ingestions.SingleAsync(i => i.Id == ingestion.Id, cancellation);
        var theirs = await second.Ingestions.SingleAsync(i => i.Id == ingestion.Id, cancellation);
        mine.LastError = "mine";
        await first.SaveChangesAsync(cancellation);
        theirs.LastError = "theirs";
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(cancellation));

        var task = new ReviewTask { Id = Guid.CreateVersion7(), TenantId = PipelineTestHost.TenantA, DocumentId = (await host.SingleDocumentAsync(ingestion.Id)).Id, Reason = "MANUAL_REVIEW", CreatedAt = host.Clock.GetUtcNow() };
        first.ReviewTasks.Add(task);
        await first.SaveChangesAsync(cancellation);
        task.Version.ShouldBe(0, "an insert starts at zero");
        task.Status = ReviewTaskStatus.InProgress;
        await first.SaveChangesAsync(cancellation);
        task.Version.ShouldBe(1);
    }
}
