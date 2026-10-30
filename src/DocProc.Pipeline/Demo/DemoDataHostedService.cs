using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm;
using DocProc.Llm.Gateway;
using DocProc.Llm.Prompts;
using DocProc.Pipeline.Intake;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocProc.Pipeline.Demo;

/// <summary>Where the demo documents live: one sub-folder per tenant slug, resolved once at registration.</summary>
public sealed record DemoDataLocation(string Root);

/// <summary>
/// The demo data of the development mode (docs/09 "Demo data"), applied at start-up after <see cref="DevSeed"/>: the
/// replay provider configuration (when <c>Llm:Replay</c> is configured), the members and usage of
/// <see cref="DemoData"/>, then every fixture file of <see cref="DemoDataLocation"/> ingested for the tenant whose slug
/// names its folder, through the same <see cref="UploadIntake"/> the API upload uses (raw blob, ingestion with one
/// candidate item, <see cref="ProcessIngestion"/> after the commit). A file counts as present when the tenant already
/// has an ingestion with that source file name, so a restart, or a second host on the same database, adds nothing.
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
        }

        var ingested = 0;
        foreach (var tenant in tenants.OrderBy(t => t.Slug, StringComparer.Ordinal))
        {
            ingested += await IngestTenantAsync(db, tenant, cancellationToken).ConfigureAwait(false);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Demo data applied from {Root}: {Rows} row(s) inserted, {Ingested} document file(s) queued for processing; the rest already existed.",
                location.Root,
                rows,
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

    private async Task<int> IngestTenantAsync(DocProcDbContext db, Tenant tenant, CancellationToken cancellationToken)
    {
        var files = FilesFor(location.Root, tenant.Slug);
        if (files.Count == 0)
        {
            return 0;
        }

        var present = (await db.Ingestions.AsNoTracking()
                .Where(i => i.TenantId == tenant.Id && i.SourceFileName != null)
                .Select(i => i.SourceFileName!)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        // The tenant's API-upload channel, as an upload without channelId would pick it; the seed creates one per tenant.
        var channel = await db.IngestionChannels.AsNoTracking()
            .Where(c => c.TenantId == tenant.Id && c.Type == ChannelType.ApiUpload)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (channel is null)
        {
            logger.LogWarning("Demo documents of tenant {Slug} skipped: the tenant has no API-upload channel.", tenant.Slug);
            return 0;
        }

        var sender = DemoData.Members.FirstOrDefault(m => m.TenantId == tenant.Id && m.Role == TenantRole.TenantAdmin)?.Email;
        var ingested = 0;
        foreach (var path in files)
        {
            var fileName = Path.GetFileName(path);
            if (present.Contains(fileName))
            {
                continue;
            }

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
