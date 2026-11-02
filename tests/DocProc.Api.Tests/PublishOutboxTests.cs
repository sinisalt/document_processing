using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline;
using DocProc.Pipeline.Queue;
using DocProc.Pipeline.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocProc.Api.Tests;

/// <summary>
/// docs/11 §13.5: approve and reject write the outbox row inside their own transaction, and the publish message they
/// queue afterwards is only a nudge. When the process dies between the commit and the enqueue (simulated by a queue
/// that throws), the decision and its row are committed, and the publish sweeper later queues the publish stage,
/// which the runner of this very host executes; the timeline shows the sweeper published it.
/// </summary>
public sealed class PublishOutboxTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly string TenantA = ApiFactory.TenantA.ToString();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient ReviewerA() => factory.ClientFor(new TokenSpec("reviewer-a", [Roles.Reviewer], TenantA));

    private static Uri Document(Guid id, string suffix = "") => new($"/api/v1/documents/{id}{suffix}", UriKind.Relative);

    private PublishSweeperHostedService Sweeper => factory.Services.GetRequiredService<PublishSweeperHostedService>();

    private PipelineOptions Options => factory.Services.GetRequiredService<IOptions<PipelineOptions>>().Value;

    private List<OutboxMessage> Outbox(Guid documentId) => factory.Query(db => db.OutboxMessages.Where(m => m.DocumentId == documentId).ToList());

    [Fact]
    public async Task Approve_commits_the_outbox_row_with_the_decision()
    {
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { ReviewTask = true });
        using var reviewer = ReviewerA();

        (await reviewer.PostAsJsonAsync(Document(seeded.DocumentId, ":approve"), new ApproveDocumentRequest(null), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var row = Outbox(seeded.DocumentId).ShouldHaveSingleItem();
        row.EventType.ShouldBe(DocumentEventTypes.Approved);
        row.TenantId.ShouldBe(ApiFactory.TenantA);
        row.CreatedAt.ShouldBe(factory.Clock.GetUtcNow());
        row.FannedOutAt.ShouldBeNull("the publish stage has not run yet");
        var document = factory.Query(db => db.Documents.Single(d => d.Id == seeded.DocumentId));
        row.CreatedAt.ShouldBe(document.ApprovedAt!.Value, "written in the approving transaction");
        var payload = JsonDocument.Parse(row.PayloadJson).RootElement;
        payload.GetProperty("tenantId").GetGuid().ShouldBe(ApiFactory.TenantA);
        payload.GetProperty("documentId").GetGuid().ShouldBe(seeded.DocumentId);
        payload.GetProperty("eventType").GetString().ShouldBe(DocumentEventTypes.Approved);

        // The nudge is queued as before, without an origin.
        factory.Queue.Documents.ShouldContain(m => m.DocumentId == seeded.DocumentId && m.Stage == PipelineStage.Publish && m.Origin == null);
    }

    [Fact]
    public async Task Reject_commits_the_outbox_row_with_the_decision()
    {
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { ReviewTask = true });
        using var reviewer = ReviewerA();

        (await reviewer.PostAsJsonAsync(Document(seeded.DocumentId, ":reject"), new RejectDocumentRequest("not ours"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var row = Outbox(seeded.DocumentId).ShouldHaveSingleItem();
        row.EventType.ShouldBe(DocumentEventTypes.Rejected);
        row.FannedOutAt.ShouldBeNull();
        row.CreatedAt.ShouldBe(factory.Query(db => db.Documents.Single(d => d.Id == seeded.DocumentId)).UpdatedAt);
        factory.Queue.Documents.ShouldContain(m => m.DocumentId == seeded.DocumentId && m.Stage == PipelineStage.Publish);
    }

    [Fact]
    public async Task A_refused_decision_writes_no_outbox_row()
    {
        var approved = await factory.SeedDocumentAsync(new DocumentSeed { Status = DocumentStatus.Approved });
        var rejected = await factory.SeedDocumentAsync(new DocumentSeed { Status = DocumentStatus.Rejected });
        using var reviewer = ReviewerA();

        (await reviewer.PostAsync(Document(approved.DocumentId, ":approve"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await reviewer.PostAsJsonAsync(Document(rejected.DocumentId, ":reject"), new RejectDocumentRequest("no"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        Outbox(approved.DocumentId).ShouldBeEmpty();
        Outbox(rejected.DocumentId).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_approval_whose_publish_message_is_lost_after_the_commit_is_published_by_the_sweeper()
    {
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { ReviewTask = true });
        using var reviewer = ReviewerA();

        // The process "dies" after the commit, before the publish message is queued.
        factory.Queue.Crash = m => m.DocumentId == seeded.DocumentId && m.Stage == PipelineStage.Publish;
        try
        {
            (await reviewer.PostAsJsonAsync(Document(seeded.DocumentId, ":approve"), new ApproveDocumentRequest("ok"), Ct)).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        }
        finally
        {
            factory.Queue.Crash = null;
        }

        factory.Queue.Documents.ShouldNotContain(m => m.DocumentId == seeded.DocumentId, "the publish message was lost");
        factory.Query(db => db.Documents.Single(d => d.Id == seeded.DocumentId)).Status.ShouldBe(DocumentStatus.Approved, "the decision was committed");
        var row = Outbox(seeded.DocumentId).ShouldHaveSingleItem();
        row.FannedOutAt.ShouldBeNull();

        // Within the grace period the sweeper leaves it to the normal nudge.
        await Sweeper.SweepOnceAsync(Ct);
        factory.Queue.Documents.ShouldNotContain(m => m.DocumentId == seeded.DocumentId);

        factory.Clock.Advance(Options.PublishSweepGrace + TimeSpan.FromSeconds(1));
        await Sweeper.SweepOnceAsync(Ct);
        var swept = factory.Queue.Documents.First(m => m.DocumentId == seeded.DocumentId);
        swept.ShouldBe(new ProcessDocument(ApiFactory.TenantA, seeded.DocumentId, PipelineStage.Publish, Origin: PipelineMessageOrigins.PublishSweeper));

        // This host's runner executes the publish stage.
        await RunAsync(swept);

        row = Outbox(seeded.DocumentId).ShouldHaveSingleItem("the stage hands the committed row on, it does not write another");
        row.FannedOutAt.ShouldBe(factory.Clock.GetUtcNow());
        var events = (await reviewer.GetFromJsonAsync<List<DocumentEventDto>>(Document(seeded.DocumentId, "/events"), ApiFactory.Json, Ct))!;
        var published = events[^1];
        published.EventType.ShouldBe(DocumentEventTypes.StageCompleted);
        published.Actor.ShouldBe("pipeline");
        published.FromStatus.ShouldBe(DocumentStatus.Approved);
        published.ToStatus.ShouldBe(DocumentStatus.Approved);
        published.Details!.Value.GetProperty("stage").GetString().ShouldBe("Publish");
        published.Details.Value.GetProperty("eventType").GetString().ShouldBe(DocumentEventTypes.Approved);
        published.Details.Value.GetProperty("outboxMessageId").GetGuid().ShouldBe(row.Id);
        published.Details.Value.GetProperty("recoveredBy").GetString().ShouldBe(PipelineMessageOrigins.PublishSweeper);

        // Published once: neither the sweeper nor a late nudge publishes it again.
        var before = factory.Queue.Documents.Count(m => m.DocumentId == seeded.DocumentId);
        factory.Clock.Advance(Options.PublishSweepGrace);
        await Sweeper.SweepOnceAsync(Ct);
        factory.Queue.Documents.Count(m => m.DocumentId == seeded.DocumentId).ShouldBe(before);
        await RunAsync(new ProcessDocument(ApiFactory.TenantA, seeded.DocumentId, PipelineStage.Publish));
        var after = (await reviewer.GetFromJsonAsync<List<DocumentEventDto>>(Document(seeded.DocumentId, "/events"), ApiFactory.Json, Ct))!;
        after.Count.ShouldBe(events.Count);
    }

    [Fact]
    public async Task The_normal_nudge_publishes_with_the_timeline_entry_of_before()
    {
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { ReviewTask = true });
        using var reviewer = ReviewerA();
        (await reviewer.PostAsJsonAsync(Document(seeded.DocumentId, ":reject"), new RejectDocumentRequest("duplicate"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await RunAsync(factory.Queue.Documents.Single(m => m.DocumentId == seeded.DocumentId));

        var row = Outbox(seeded.DocumentId).ShouldHaveSingleItem();
        row.FannedOutAt.ShouldNotBeNull();
        var published = (await reviewer.GetFromJsonAsync<List<DocumentEventDto>>(Document(seeded.DocumentId, "/events"), ApiFactory.Json, Ct))![^1];
        published.EventType.ShouldBe(DocumentEventTypes.StageCompleted);
        published.Details!.Value.EnumerateObject().Select(p => p.Name).ShouldBe(["stage", "eventType", "outboxMessageId"], "no recoveredBy on the normal path");
        published.Details.Value.GetProperty("eventType").GetString().ShouldBe(DocumentEventTypes.Rejected);

        factory.Clock.Advance(Options.PublishSweepGrace + TimeSpan.FromSeconds(1));
        await Sweeper.SweepOnceAsync(Ct);
        factory.Queue.Documents.Count(m => m.DocumentId == seeded.DocumentId && m.Origin == PipelineMessageOrigins.PublishSweeper).ShouldBe(0);
    }

    /// <summary>Hands a message to the runner this API host runs in-process and waits until it is done.</summary>
    private async Task RunAsync(ProcessDocument message)
    {
        var runner = factory.Services.GetRequiredService<InProcessPipelineQueue>();
        await runner.EnqueueAsync(message, Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await runner.WaitForIdleAsync(timeout.Token);
    }
}
