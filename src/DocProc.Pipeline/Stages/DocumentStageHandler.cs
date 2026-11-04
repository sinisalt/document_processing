using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocProc.Pipeline.Stages;

/// <summary>
/// The idempotency contract of docs/03 "Idempotency and retries" for document stages: load the document with its
/// flags and item, compare the current status with the one this stage starts from, and drop stale messages with a
/// log line instead of doing work twice. Subclasses implement the stage itself and commit with the shared context.
/// </summary>
internal abstract class DocumentStageHandler(DocProcDbContext db, DocumentTimeline timeline, ILogger logger) : IStageHandler
{
    protected DocProcDbContext Db { get; } = db;

    protected DocumentTimeline Timeline { get; } = timeline;

    protected ILogger Logger { get; } = logger;

    private readonly List<Func<CancellationToken, Task>> _afterCommit = [];

    public abstract PipelineStage Stage { get; }

    public async Task<StageOutcome> HandleAsync(ProcessDocument message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        _afterCommit.Clear();
        StageOutcome outcome;
        try
        {
            outcome = RunsInTransaction
                ? await Db.Database.CreateExecutionStrategy().ExecuteAsync(
                    async ct =>
                    {
                        // Each attempt of the strategy starts from an empty change tracker and reloads the document,
                        // so a retry after a transient failure does not save the failed attempt's rows a second time.
                        Db.ChangeTracker.Clear();
                        _afterCommit.Clear();
                        await using var transaction = await Db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                        var result = await LoadAndRunAsync(message, ct).ConfigureAwait(false);
                        await transaction.CommitAsync(ct).ConfigureAwait(false);
                        return result;
                    },
                    cancellationToken).ConfigureAwait(false)
                : await LoadAndRunAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // Another unit of work (a twin message on another node, a reviewer, a reprocess) wrote the document after
            // this one loaded it; its version moved on, so this attempt's result is stale and is dropped unsaved (and,
            // for a stage that runs in a transaction, everything it wrote directly is rolled back with it).
            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation(exception, "Stage {Stage}: document {DocumentId} was changed by another writer while attempt {Attempt} ran; message dropped as already handled.", Stage, message.DocumentId, message.Attempt);
            }

            _afterCommit.Clear();
            return StageOutcome.Skipped;
        }

        foreach (var action in _afterCommit)
        {
            await action(cancellationToken).ConfigureAwait(false);
        }

        _afterCommit.Clear();
        return outcome;
    }

    /// <summary>
    /// Whether the stage runs in one database transaction, from loading the document to its last save, under the
    /// context's execution strategy: what the stage writes directly (<c>ExecuteUpdate</c>, such as the place a document
    /// takes in a layout version's warm-up) then commits with its saved result or not at all, also when the attempt is
    /// dropped as stale or fails. Work that must follow the commit (queueing the next message) goes to
    /// <see cref="AfterCommit"/>.
    /// </summary>
    protected virtual bool RunsInTransaction => false;

    /// <summary>Runs <paramref name="action"/> once the stage's work is committed, after the stage returns.</summary>
    protected void AfterCommit(Func<CancellationToken, Task> action) => _afterCommit.Add(action);

    private async Task<StageOutcome> LoadAndRunAsync(ProcessDocument message, CancellationToken cancellationToken)
    {
        var document = await Db.Documents
            .Include(d => d.Flags)
            .Include(d => d.IngestionItem)
            .ThenInclude(i => i!.Ingestion)
            .SingleOrDefaultAsync(d => d.Id == message.DocumentId, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            Logger.LogWarning("Stage {Stage}: document {DocumentId} of tenant {TenantId} does not exist; message dropped.", Stage, message.DocumentId, message.TenantId);
            return StageOutcome.Skipped;
        }

        if (!Accepts(document.Status))
        {
            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation("Stage {Stage}: document {DocumentId} is {Status}; message (attempt {Attempt}) dropped as already handled.", Stage, document.Id, document.Status, message.Attempt);
            }

            return StageOutcome.Skipped;
        }

        return await RunAsync(document, message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The status a document has when this stage is due.</summary>
    protected abstract bool Accepts(DocumentStatus status);

    protected abstract Task<StageOutcome> RunAsync(Document document, ProcessDocument message, CancellationToken cancellationToken);

    protected Task<Tenant> LoadTenantAsync(Document document, CancellationToken cancellationToken) =>
        Db.Tenants.AsNoTracking().SingleAsync(t => t.Id == document.TenantId, cancellationToken);

    /// <summary>
    /// True when stage 3 cut the document's ingestion item into several documents and this is one of them: only a
    /// split (the pipeline's or a reviewer's) creates a second document for an item. Such a document owns its page
    /// range, not the whole file. A part a reviewer joined into another one (<c>Cancelled</c>) no longer counts, so a
    /// document joined back into the whole attachment is the whole file again (docs/11 §12.7).
    /// </summary>
    protected Task<bool> IsSplitPartAsync(Document document, CancellationToken cancellationToken) =>
        Db.Documents.AnyAsync(d => d.IngestionItemId == document.IngestionItemId && d.Id != document.Id && d.Status != DocumentStatus.Cancelled, cancellationToken);
}
