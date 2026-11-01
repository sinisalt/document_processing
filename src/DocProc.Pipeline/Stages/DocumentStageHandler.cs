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

    public abstract PipelineStage Stage { get; }

    public async Task<StageOutcome> HandleAsync(ProcessDocument message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

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

        try
        {
            return await RunAsync(document, message, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // Another unit of work (a twin message on another node, a reviewer, a reprocess) wrote the document after
            // this one loaded it; its version moved on, so this attempt's result is stale and is dropped unsaved.
            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation(exception, "Stage {Stage}: document {DocumentId} was changed by another writer while attempt {Attempt} ran; message dropped as already handled.", Stage, document.Id, message.Attempt);
            }

            return StageOutcome.Skipped;
        }
    }

    /// <summary>The status a document has when this stage is due.</summary>
    protected abstract bool Accepts(DocumentStatus status);

    protected abstract Task<StageOutcome> RunAsync(Document document, ProcessDocument message, CancellationToken cancellationToken);

    protected Task<Tenant> LoadTenantAsync(Document document, CancellationToken cancellationToken) =>
        Db.Tenants.AsNoTracking().SingleAsync(t => t.Id == document.TenantId, cancellationToken);

    /// <summary>
    /// True when stage 3 cut the document's ingestion item into several documents and this is one of them: only a
    /// split creates a second document for an item. Such a document owns its page range, not the whole file.
    /// </summary>
    protected Task<bool> IsSplitPartAsync(Document document, CancellationToken cancellationToken) =>
        Db.Documents.AnyAsync(d => d.IngestionItemId == document.IngestionItemId && d.Id != document.Id, cancellationToken);
}
