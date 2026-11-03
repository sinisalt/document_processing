using System.Net;
using System.Net.Http.Json;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace DocProc.Api.Tests;

/// <summary>
/// On SQL Server the context retries transient failures (<c>EnableRetryOnFailure</c>), and a retrying strategy refuses
/// a transaction that it does not run itself. Approve, reject and reprocess open one for the decision and its outbox row,
/// so in the compose stack every one of them answered 500 ("does not support user-initiated transactions") while the
/// SQLite tests passed. Here the API runs under such a strategy: the decisions work, and a transient failure inside the
/// transaction is retried without writing the decision twice.
/// </summary>
public sealed class DecisionExecutionStrategyTests : IDisposable
{
    private static readonly string TenantA = ApiFactory.TenantA.ToString();

    private readonly ApiFactory _factory = new() { RetryingStrategy = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Approve_reject_and_reprocess_work_under_a_retrying_execution_strategy()
    {
        var toApprove = await _factory.SeedDocumentAsync(new DocumentSeed { ReviewTask = true });
        var toReject = await _factory.SeedDocumentAsync(new DocumentSeed { ReviewTask = true });
        var toReprocess = await _factory.SeedDocumentAsync(new DocumentSeed { ReviewTask = true });
        _factory.RecordPipelineProgress(toReprocess.DocumentId, DocumentStatus.Validated);
        using var reviewer = Reviewer();

        (await reviewer.PostAsJsonAsync(Uri(toApprove.DocumentId, ":approve"), new ApproveDocumentRequest("ok"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await reviewer.PostAsJsonAsync(Uri(toReject.DocumentId, ":reject"), new RejectDocumentRequest("not ours"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await reviewer.PostAsJsonAsync(Uri(toReprocess.DocumentId, ":reprocess"), new ReprocessDocumentRequest("Validate"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        Status(toApprove.DocumentId).ShouldBe(DocumentStatus.Approved);
        Status(toReject.DocumentId).ShouldBe(DocumentStatus.Rejected);
        Status(toReprocess.DocumentId).ShouldBe(DocumentStatus.Extracted);
        Outbox(toApprove.DocumentId).ShouldHaveSingleItem().EventType.ShouldBe(DocumentEventTypes.Approved);
        Outbox(toReject.DocumentId).ShouldHaveSingleItem().EventType.ShouldBe(DocumentEventTypes.Rejected);
    }

    /// <summary>The review actions of P2 (docs/11 §12) save without a transaction of their own, so the strategy runs them as it runs any save.</summary>
    [Fact]
    public async Task The_review_actions_work_under_a_retrying_execution_strategy()
    {
        var typed = await _factory.SeedDocumentAsync(new DocumentSeed { Type = DocumentTypeKey.Unknown, Invoice = null, SupplierName = null, ReviewTask = true });
        var assigned = await _factory.SeedDocumentAsync(new DocumentSeed { SupplierName = null, ReviewTask = true });
        var metro = await _factory.SeedDocumentAsync(new DocumentSeed());
        var supplierId = _factory.Query(db => db.Documents.Single(d => d.Id == metro.DocumentId).SupplierId!.Value);
        using var reviewer = Reviewer();

        (await reviewer.PostAsJsonAsync(Uri(typed.DocumentId, ":set-type"), new SetDocumentTypeRequest("Invoice"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await reviewer.PostAsJsonAsync(Uri(assigned.DocumentId, ":assign-supplier"), new AssignSupplierRequest(supplierId), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await reviewer.PatchAsJsonAsync(Uri(assigned.DocumentId, "/fields"), new CorrectFieldsRequest([new FieldCorrection("deliveryDate", null, null, Op: "move", From: "dueDate")]), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        _factory.Query(db => db.Documents.Single(d => d.Id == typed.DocumentId).Type).ShouldBe(DocumentTypeKey.Invoice);
        _factory.Query(db => db.Documents.Single(d => d.Id == assigned.DocumentId).SupplierMatchMethod).ShouldBe(SupplierMatchMethod.Human);
    }

    [Theory]
    [InlineData(":approve", DocumentStatus.Approved, DocumentEventTypes.Approved)]
    [InlineData(":reject", DocumentStatus.Rejected, DocumentEventTypes.Rejected)]
    public async Task A_transient_failure_inside_the_decision_is_retried_and_written_once(string action, DocumentStatus settled, string eventType)
    {
        var seeded = await _factory.SeedDocumentAsync(new DocumentSeed { ReviewTask = true });
        var version = _factory.Query(db => db.Documents.Single(d => d.Id == seeded.DocumentId)).Version;
        using var reviewer = Reviewer();
        _factory.SaveFailure.FailNext = 1;

        using var response = action == ":approve"
            ? await reviewer.PostAsJsonAsync(Uri(seeded.DocumentId, action), new ApproveDocumentRequest("ok"), Ct)
            : await reviewer.PostAsJsonAsync(Uri(seeded.DocumentId, action), new RejectDocumentRequest("no"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _factory.SaveFailure.Failed.ShouldBe(1, "the first attempt failed and was retried");
        var document = _factory.Query(db => db.Documents.Single(d => d.Id == seeded.DocumentId));
        document.Status.ShouldBe(settled);
        document.Version.ShouldBe(version + 1, "the version moved once");
        Outbox(seeded.DocumentId).ShouldHaveSingleItem("the failed attempt's outbox row is not written again");
        _factory.Query(db => db.DocumentEvents.Count(e => e.DocumentId == seeded.DocumentId && e.EventType == eventType)).ShouldBe(1);
        _factory.Query(db => db.ReviewTasks.Single(t => t.DocumentId == seeded.DocumentId)).Status.ShouldBe(ReviewTaskStatus.Done);
        _factory.Queue.Documents.Count(m => m.DocumentId == seeded.DocumentId && m.Stage == PipelineStage.Publish).ShouldBe(1);
    }

    [Fact]
    public async Task A_decision_on_a_document_that_moved_meanwhile_is_still_refused()
    {
        var seeded = await _factory.SeedDocumentAsync(new DocumentSeed { Status = DocumentStatus.Approved });
        using var reviewer = Reviewer();

        (await reviewer.PostAsJsonAsync(Uri(seeded.DocumentId, ":reject"), new RejectDocumentRequest("no"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        Outbox(seeded.DocumentId).ShouldBeEmpty();
    }

    private HttpClient Reviewer() => _factory.ClientFor(new TokenSpec("reviewer-a", [Roles.Reviewer], TenantA));

    private static Uri Uri(Guid id, string action) => new($"/api/v1/documents/{id}{action}", UriKind.Relative);

    private DocumentStatus Status(Guid id) => _factory.Query(db => db.Documents.Single(d => d.Id == id)).Status;

    private List<OutboxMessage> Outbox(Guid id) => _factory.Query(db => db.OutboxMessages.Where(m => m.DocumentId == id).ToList());
}

/// <summary>A retrying strategy for SQLite, shaped like SQL Server's: it retries <see cref="TransientTestException"/>.</summary>
public sealed class TestRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
    : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(5))
{
    protected override bool ShouldRetryOn(Exception exception) => exception is TransientTestException;
}

/// <summary>Fails the next <see cref="FailNext"/> saves with a transient error, before anything is written.</summary>
public sealed class TransientSaveFailure : SaveChangesInterceptor
{
    private int _failNext;
    private int _failed;

    public int FailNext
    {
        get => Volatile.Read(ref _failNext);
        set => Volatile.Write(ref _failNext, value);
    }

    public int Failed => Volatile.Read(ref _failed);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Decrement(ref _failNext) >= 0)
        {
            Interlocked.Increment(ref _failed);
            throw new TransientTestException();
        }

        Interlocked.Exchange(ref _failNext, 0);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

public sealed class TransientTestException : Exception
{
    public TransientTestException()
        : base("A transient failure for the retrying strategy.")
    {
    }

    public TransientTestException(string message)
        : base(message)
    {
    }

    public TransientTestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
