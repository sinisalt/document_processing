using System.Reflection;
using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Common;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm;
using DocProc.Llm.Providers;
using DocProc.Pipeline.Queue;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace DocProc.Pipeline.Tests;

/// <summary>
/// A generic host composed exactly as the worker is (<c>AddDocProcProcessingStack</c>), on a temporary SQLite file
/// with the development seed, file-system blobs under a temp root and the scripted LLM provider. The runner consumes
/// the in-process queue in the background; tests enqueue work and await <see cref="RunUntilIdleAsync"/>.
/// </summary>
internal sealed class PipelineTestHost : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    public static readonly Guid TenantA = DevSeed.TenantA;

    private readonly IHost _host;

    private PipelineTestHost(IHost host, string directory, FakeTimeProvider clock)
    {
        _host = host;
        Directory = directory;
        Clock = clock;
    }

    public string Directory { get; }

    public FakeTimeProvider Clock { get; }

    public IServiceProvider Services => _host.Services;

    public InProcessPipelineQueue Queue => Services.GetRequiredService<InProcessPipelineQueue>();

    public IPipelineQueue PipelineQueue => Services.GetRequiredService<IPipelineQueue>();

    public ScriptedLlmProvider Llm => Services.GetRequiredService<ScriptedLlmProvider>();

    public static async Task<PipelineTestHost> StartAsync(Action<Dictionary<string, string?>>? configure = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "docproc-pipeline-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);

        var settings = new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Sqlite",
            ["Persistence:SqlitePath"] = Path.Combine(directory, "docproc.db"),
            ["Blobs:RootPath"] = Path.Combine(directory, "blobs"),
            ["Llm:Replay:Scripted"] = "true",
            ["Pipeline:MaxParallelism"] = "2",
            ["Pipeline:RetryBaseDelay"] = "00:00:00.020",
            ["Pipeline:MaxAttempts"] = "3",
            ["Pipeline:ShutdownGracePeriod"] = "00:00:05",
        };
        configure?.Invoke(settings);

        var clock = new FakeTimeProvider(Now);
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Testing",
            ContentRootPath = directory,
            ApplicationName = "DocProc.Pipeline.Tests",
        });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddLogging();
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.TryAddDocProcPersistence(builder.Configuration, builder.Environment, migrateByDefault: false).ShouldBeTrue();
        builder.AddDocProcProcessingStack(runInProcessDefault: true);

        var host = builder.Build();
        await host.StartAsync();
        return new PipelineTestHost(host, directory, clock);
    }

    public async ValueTask DisposeAsync()
    {
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            await _host.StopAsync(stop.Token);
        }

        _host.Dispose();
        SqliteConnection.ClearAllPools();
        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    /// <summary>Waits until the queue has nothing queued, delayed or running.</summary>
    public async Task RunUntilIdleAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        await Queue.WaitForIdleAsync(cts.Token);
    }

    /// <summary>Runs a query or a change in platform scope, outside any pipeline scope.</summary>
    public async Task<T> WithDbAsync<T>(Func<DocProcDbContext, Task<T>> action)
    {
        await using var db = Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped();
        return await action(db);
    }

    public Task WithDbAsync(Func<DocProcDbContext, Task> action) =>
        WithDbAsync(async db =>
        {
            await action(db);
            return 0;
        });

    /// <summary>Stores the files as raw blobs, inserts the ingestion with one candidate item per file and queues stage 1.</summary>
    public async Task<Ingestion> IngestAsync(Guid tenantId, IReadOnlyList<IngestFile> files, string? from = null, bool enqueue = true)
    {
        var blobs = Services.GetRequiredService<IBlobStore>();
        var now = Clock.GetUtcNow();
        var stored = new List<(IngestFile File, BlobRef Blob)>();
        foreach (var file in files)
        {
            await using var stream = new MemoryStream(file.Bytes, writable: false);
            stored.Add((file, await blobs.PutAsync(tenantId, stream, file.MimeType, CancellationToken.None)));
        }

        var ingestion = new Ingestion
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ChannelId = tenantId == TenantA ? DevSeed.TenantAUploadChannel : DevSeed.TenantBUploadChannel,
            ReceivedAt = now,
            SourceFrom = from,
            SourceSubject = "test ingestion",
            RawBlob = ToEntity(stored[0].Blob),
            Status = IngestionStatus.Received,
            UpdatedAt = now,
        };

        var order = 0;
        foreach (var (file, blob) in stored)
        {
            ingestion.Items.Add(new IngestionItem
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                IngestionId = ingestion.Id,
                FileName = file.FileName,
                MimeType = file.MimeType,
                SizeBytes = file.Bytes.Length,
                Sha256 = blob.Sha256,
                Blob = ToEntity(blob),
                Disposition = ItemDisposition.Candidate,
                CreatedAt = now.AddMilliseconds(order++),
            });
        }

        await WithDbAsync(async db =>
        {
            db.Ingestions.Add(ingestion);
            await db.SaveChangesAsync();
        });

        if (enqueue)
        {
            await PipelineQueue.EnqueueAsync(new ProcessIngestion(tenantId, ingestion.Id), CancellationToken.None);
        }

        return ingestion;
    }

    /// <summary>An active platform configuration of the Replay kind, so the gateway resolves every stage to the scripted provider.</summary>
    public Task AddReplayProviderAsync() => WithDbAsync(async db =>
    {
        db.LlmProviderConfigs.Add(new LlmProviderConfigRecord
        {
            Id = Guid.CreateVersion7(),
            Scope = ConfigScope.Platform,
            TenantId = null,
            Kind = LlmProviderKind.Replay,
            AuthType = LlmAuthType.ApiKey,
            SecretRef = null,
            Models = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [nameof(LlmStage.Classify)] = "claude-haiku-5-5",
                [nameof(LlmStage.Split)] = "claude-haiku-5-5",
                [nameof(LlmStage.Extract)] = "claude-opus-5-5",
            },
            Status = ProviderConfigStatus.Active,
            CreatedBy = "test",
            CreatedAt = Clock.GetUtcNow(),
            UpdatedAt = Clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();
    });

    public Task<Supplier> AddSupplierAsync(Guid tenantId, string name, string? vatId = null, IEnumerable<string>? emailDomains = null, IEnumerable<string>? ibans = null, bool alwaysReview = false) =>
        WithDbAsync(async db =>
        {
            var supplier = new Supplier
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Name = name,
                NormalizedName = Identifiers.NormalizeName(name),
                VatId = vatId,
                EmailDomains = emailDomains?.ToList() ?? [],
                Ibans = ibans?.ToList() ?? [],
                AlwaysReview = alwaysReview,
                CreatedAt = Clock.GetUtcNow(),
                UpdatedAt = Clock.GetUtcNow(),
            };
            db.Suppliers.Add(supplier);
            await db.SaveChangesAsync();
            return supplier;
        });

    public Task<Document> SingleDocumentAsync(Guid ingestionId) =>
        WithDbAsync(db => db.Documents.AsNoTracking()
            .Include(d => d.Flags)
            .Include(d => d.IngestionItem)
            .Where(d => d.IngestionItem!.IngestionId == ingestionId)
            .SingleAsync());

    public Task<List<DocumentEvent>> EventsAsync(Guid documentId) =>
        WithDbAsync(async db => (await db.DocumentEvents.AsNoTracking().Where(e => e.DocumentId == documentId).ToListAsync()).OrderBy(e => e.Id).ToList());

    public async Task<bool> DerivedBlobExistsAsync(string key)
    {
        var store = Services.GetRequiredService<IContainerBlobStore>();
        return await store.FindAsync(BlobContainers.Derived, key, CancellationToken.None) is not null;
    }

    public static byte[] Fixture(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + name, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static BlobReference ToEntity(BlobRef blob) => new()
    {
        Container = blob.Container,
        Key = blob.Key,
        Sha256 = blob.Sha256,
        SizeBytes = blob.SizeBytes,
        ContentType = blob.ContentType,
    };
}

internal sealed record IngestFile(string FileName, string MimeType, byte[] Bytes);
