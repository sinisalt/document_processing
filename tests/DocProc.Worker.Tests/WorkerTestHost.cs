using System.Reflection;
using DocProc.Application.Abstractions;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DocProc.Worker.Tests;

/// <summary>
/// Builds the worker exactly as <c>Program.cs</c> does (<see cref="WorkerHost.CreateBuilder"/>), with the test's
/// settings passed as command-line arguments, the way an operator would override them. The content root is the test
/// output folder, where the worker's <c>appsettings.json</c> is copied, so the host reads the shipped defaults.
/// </summary>
internal static class WorkerTestHost
{
    public const string UblFixture = "peppol-bis3-invoice.xml";

    /// <summary>A fresh temp root per host; the SQLite file and the blobs live under it.</summary>
    public static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docproc-worker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>The dependency-free development configuration (docs/09 "Running without Docker") as command-line arguments.</summary>
    public static string[] SqliteArgs(string directory, string environment = "Testing") => Args(environment,
        ("Persistence:Provider", "Sqlite"),
        ("Persistence:SqlitePath", Path.Combine(directory, "docproc.db")),
        ("Blobs:RootPath", Path.Combine(directory, "blobs")),
        ("Llm:Replay:Scripted", "true"),
        ("Pipeline:RunInProcess", "true"),
        ("Pipeline:ShutdownGracePeriod", "00:00:05"));

    /// <summary>SQL Server configured but never reached: the host must compose, not connect.</summary>
    public static string[] SqlServerArgs(string environment, params (string Key, string Value)[] extra) => Args(environment,
    [
        ("Persistence:Provider", "SqlServer"),
        ("ConnectionStrings:DocProc", "Server=sql.example.test;Database=DocProc;User Id=app;Password=unused;TrustServerCertificate=true"),
        .. extra,
    ]);

    public static string[] Args(string environment, params (string Key, string Value)[] settings) =>
    [
        $"--environment={environment}",
        $"--contentRoot={AppContext.BaseDirectory}",
        .. settings.Select(s => $"--{s.Key}={s.Value}"),
    ];

    public static async Task<IHost> StartAsync(string directory)
    {
        var host = WorkerHost.CreateBuilder(SqliteArgs(directory)).Build();
        await host.StartAsync();
        return host;
    }

    public static async Task StopAndCleanAsync(IHost host, string directory)
    {
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            await host.StopAsync(stop.Token);
        }

        host.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Stores the bytes as a raw blob and inserts a received ingestion with one candidate item, as the API's upload endpoint does.</summary>
    public static async Task<Ingestion> SeedIngestionAsync(IServiceProvider services, Guid tenantId, string fileName, string mimeType, byte[] bytes)
    {
        var blobs = services.GetRequiredService<IBlobStore>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        BlobRef blob;
        await using (var stream = new MemoryStream(bytes, writable: false))
        {
            blob = await blobs.PutAsync(tenantId, stream, mimeType, CancellationToken.None);
        }

        var ingestion = new Ingestion
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ChannelId = DevSeed.TenantAUploadChannel,
            ReceivedAt = now,
            SourceSubject = "worker test ingestion",
            RawBlob = ToEntity(blob),
            Status = IngestionStatus.Received,
            UpdatedAt = now,
        };
        ingestion.Items.Add(new IngestionItem
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            IngestionId = ingestion.Id,
            FileName = fileName,
            MimeType = mimeType,
            SizeBytes = bytes.Length,
            Sha256 = blob.Sha256,
            Blob = ToEntity(blob),
            Disposition = ItemDisposition.Candidate,
            CreatedAt = now,
        });

        await using var db = services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped();
        db.Ingestions.Add(ingestion);
        await db.SaveChangesAsync();
        return ingestion;
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
