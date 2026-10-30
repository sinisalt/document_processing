using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm;
using DocProc.Llm.Gateway;
using DocProc.Llm.Prompts;
using DocProc.Llm.Providers;
using DocProc.Pipeline.Intake;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocProc.Pipeline.Demo;

/// <summary>Where the demo documents live: one sub-folder per tenant slug, resolved once at registration.</summary>
public sealed record DemoDataLocation(string Root);

/// <summary>
/// The demo data of the development mode (docs/09 "Demo data"), applied at start-up after <see cref="DevSeed"/>. On the
/// first start-up of a database (<see cref="DemoData.IsAppliedAsync"/>) it adds the replay provider configuration (when
/// <c>Llm:Replay</c> is configured and no other platform configuration is active), the members and the usage of
/// <see cref="DemoData"/>. On every start-up it then resumes the demo work an earlier process left behind, because
/// the in-process queue is not durable: a demo ingestion still <see cref="IngestionStatus.Received"/> gets its
/// <see cref="ProcessIngestion"/> again, a demo document stuck in a pipeline state gets the <see cref="ProcessDocument"/>
/// of the stage that continues it (<see cref="DemoData.ResumeStage"/>); the handlers drop a message whose work is done,
/// so a twin is harmless. Finally every fixture file of <see cref="DemoDataLocation"/> that is new is ingested for the
/// tenant whose slug names its folder, through the same <see cref="UploadIntake"/> the API upload uses (raw blob,
/// ingestion with one candidate item, <see cref="ProcessIngestion"/> after the commit). A file counts as present when
/// the tenant already has an ingestion with that source file name, so a restart, or a second host on the same
/// database, adds nothing.
/// </summary>
public sealed class DemoDataHostedService(
    ISystemDbContextFactory contexts,
    IBlobStore blobs,
    IPipelineQueue queue,
    LlmReplayOptions replay,
    PromptCatalogue prompts,
    PriceTable prices,
    DemoDataLocation location,
    TimeProvider clock,
    ILogger<DemoDataHostedService> logger) : IHostedService
{
    /// <summary>The file types the demo folders may hold; anything else (a README, the replay fixtures) is not a document.</summary>
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".xml"] = "application/xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".zip"] = "application/zip",
    };

    /// <summary>The document states an interrupted run can leave a document in; <see cref="DemoData.ResumeStage"/> maps all but Validated to a stage.</summary>
    private static readonly DocumentStatus[] PipelineStatuses =
    [
        DocumentStatus.Received, DocumentStatus.Normalized, DocumentStatus.Split, DocumentStatus.Classified,
        DocumentStatus.Matched, DocumentStatus.Extracted, DocumentStatus.Validated,
    ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = contexts.CreatePlatformScoped();
        var tenants = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == DevSeed.TenantA || t.Id == DevSeed.TenantB)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (tenants.Count == 0)
        {
            logger.LogWarning("Demo data skipped: the development seed tenants do not exist.");
            return;
        }

        if (!Directory.Exists(location.Root))
        {
            logger.LogWarning(
                "Demo documents skipped: the folder {Root} does not exist. Set Persistence:DemoDocumentsPath to the repository's fixtures/demo folder (a relative path is searched upwards from the content root).",
                location.Root);
        }

        var rows = await ApplyRowsAsync(db, cancellationToken).ConfigureAwait(false);
        await LogPlatformConfigAsync(db, cancellationToken).ConfigureAwait(false);

        var resumed = 0;
        var ingested = 0;
        foreach (var tenant in tenants.OrderBy(t => t.Slug, StringComparer.Ordinal))
        {
            var files = FilesFor(location.Root, tenant.Slug);
            if (files.Count == 0)
            {
                continue;
            }

            // Resume first: the ingestions queued below are Received too, and must not be queued twice.
            resumed += await ResumeTenantAsync(db, tenant, files, cancellationToken).ConfigureAwait(false);
            ingested += await IngestTenantAsync(db, tenant, files, cancellationToken).ConfigureAwait(false);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Demo data applied from {Root}: {Rows} row(s) inserted, {Resumed} interrupted ingestion(s) or document(s) resumed, {Ingested} document file(s) queued for processing; the rest already existed.",
                location.Root,
                rows,
                resumed,
                ingested);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The demo document files of one tenant folder, in ordinal name order.</summary>
    public static IReadOnlyList<string> FilesFor(string root, string tenantSlug)
    {
        var folder = Path.Combine(root, tenantSlug);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(folder)
            .Where(path => ContentTypes.ContainsKey(Path.GetExtension(path)))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The replay configuration, members and usage, on the first start-up of the database only.</summary>
    private async Task<int> ApplyRowsAsync(DocProcDbContext db, CancellationToken cancellationToken)
    {
        if (await DemoData.IsAppliedAsync(db, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        var now = clock.GetUtcNow();
        var rows = 0;
        if (!string.IsNullOrWhiteSpace(replay.Directory) || replay.Scripted)
        {
            rows += await DemoData.EnsureReplayProviderAsync(db, now, cancellationToken).ConfigureAwait(false);
        }

        rows += await DemoData.EnsureMembersAsync(db, now, cancellationToken).ConfigureAwait(false);
        rows += await DemoData.EnsureUsageAsync(db, prompts, prices, now, cancellationToken).ConfigureAwait(false);
        if (rows > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }

        return rows;
    }

    /// <summary>Says which platform configuration the LLM stages will use, so a developer sees at once whether a real key or the replay serves them.</summary>
    private async Task LogPlatformConfigAsync(DocProcDbContext db, CancellationToken cancellationToken)
    {
        var config = await DemoData.PlatformConfigInUseAsync(db, cancellationToken).ConfigureAwait(false);
        if (config is null || config.Status != ProviderConfigStatus.Active)
        {
            logger.LogWarning(
                "No platform LLM configuration is active: documents that need an LLM call end Failed with LLM_PROVIDER_UNAVAILABLE. Enable one in the admin console (LLM providers); the structured e-invoices need none.");
            return;
        }

        if (config.Kind != LlmProviderKind.Replay)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("LLM platform configuration in use: {ConfigId} ({Kind}).", config.Id, config.Kind);
            }

            return;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "LLM platform configuration in use: {ConfigId} (Replay, recorded responses from {Directory}); only the demo documents have fixtures, any other document needing an LLM call ends Failed until a real provider is enabled in the admin console.",
                config.Id,
                replay.Scripted ? "a scripted provider" : replay.Directory);
        }

        if (!DemoData.IsFixtureCapturePlatform)
        {
            logger.LogWarning(
                "The demo classification fixture was captured on {Platform}; this platform renders PDF pages differently, so the delivery note's classification misses the replay store and ends Failed (docs/09 \"Demo data\").",
                DemoData.FixtureCapturePlatform);
        }
    }

    /// <summary>Queues again what an earlier process queued for this tenant's demo files but did not finish (the in-process queue died with it).</summary>
    private async Task<int> ResumeTenantAsync(DocProcDbContext db, Tenant tenant, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var names = files.Select(Path.GetFileName).OfType<string>().ToList();
        var resumed = 0;

        // Both lists are read before anything is queued: once a resumed ingestion is queued the runner may unpack it
        // and create Received documents, which must not be picked up below as interrupted ones and queued twice.
        // IngestHandler takes only Received ingestions; anything else was unpacked (its documents exist) or failed.
        var ingestions = await db.Ingestions.AsNoTracking()
            .Where(i => i.TenantId == tenant.Id && i.Status == IngestionStatus.Received && i.SourceFileName != null && names.Contains(i.SourceFileName))
            .OrderBy(i => i.SourceFileName)
            .Select(i => new { i.Id, i.SourceFileName })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var documents = await db.Documents.AsNoTracking()
            .Where(d => d.TenantId == tenant.Id
                        && PipelineStatuses.Contains(d.Status)
                        && d.IngestionItem != null
                        && d.IngestionItem.Ingestion != null
                        && d.IngestionItem.Ingestion.SourceFileName != null
                        && names.Contains(d.IngestionItem.Ingestion.SourceFileName))
            .Select(d => new { d.Id, d.Status, File = d.IngestionItem!.FileName })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var ingestion in ingestions)
        {
            await queue.EnqueueAsync(new ProcessIngestion(tenant.Id, ingestion.Id), cancellationToken).ConfigureAwait(false);
            resumed++;
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Resumed demo ingestion {IngestionId} ({File}) of tenant {Slug}: still Received, queued again.", ingestion.Id, ingestion.SourceFileName, tenant.Slug);
            }
        }

        foreach (var document in documents.OrderBy(d => d.File, StringComparer.Ordinal).ThenBy(d => d.Id))
        {
            if (DemoData.ResumeStage(document.Status) is not { } stage)
            {
                logger.LogWarning("Demo document {DocumentId} ({File}) is {Status}, which no stage continues; reprocess it from the portal.", document.Id, document.File, document.Status);
                continue;
            }

            await queue.EnqueueAsync(new ProcessDocument(tenant.Id, document.Id, stage), cancellationToken).ConfigureAwait(false);
            resumed++;
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Resumed demo document {DocumentId} ({File}) of tenant {Slug}: {Status}, queued for stage {Stage}.", document.Id, document.File, tenant.Slug, document.Status, stage);
            }
        }

        return resumed;
    }

    private async Task<int> IngestTenantAsync(DocProcDbContext db, Tenant tenant, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var present = (await db.Ingestions.AsNoTracking()
                .Where(i => i.TenantId == tenant.Id && i.SourceFileName != null)
                .Select(i => i.SourceFileName!)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        var missing = files.Where(path => !present.Contains(Path.GetFileName(path))).ToList();
        if (missing.Count == 0)
        {
            return 0;
        }

        // The tenant's oldest active API-upload channel; the seed creates one per tenant. A paused or failing channel
        // takes no uploads, so the demo does not file documents under it either.
        var channel = await db.IngestionChannels.AsNoTracking()
            .Where(c => c.TenantId == tenant.Id && c.Type == ChannelType.ApiUpload && c.Status == ChannelStatus.Active)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (channel is null)
        {
            logger.LogWarning("Demo documents of tenant {Slug} skipped: the tenant has no active API-upload channel.", tenant.Slug);
            return 0;
        }

        var sender = DemoData.Members.FirstOrDefault(m => m.TenantId == tenant.Id && m.Role == TenantRole.TenantAdmin)?.Email;
        var ingested = 0;
        foreach (var path in missing)
        {
            var fileName = Path.GetFileName(path);
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var contentType = ContentTypes[Path.GetExtension(path)];
            var upload = new UploadFile(fileName, contentType, ItemKinds.Detect(contentType, fileName), () => new MemoryStream(bytes, writable: false));
            var stored = await UploadIntake.StoreAsync(blobs, tenant.Id, [upload], cancellationToken).ConfigureAwait(false);
            var ingestion = UploadIntake.CreateIngestion(tenant.Id, channel.Id, sender, stored, clock.GetUtcNow());
            db.Ingestions.Add(ingestion);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();

            await queue.EnqueueAsync(new ProcessIngestion(tenant.Id, ingestion.Id), cancellationToken).ConfigureAwait(false);
            ingested++;
        }

        return ingested;
    }
}
