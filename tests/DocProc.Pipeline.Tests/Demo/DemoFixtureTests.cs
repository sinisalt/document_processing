using System.Collections.Concurrent;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Llm;
using DocProc.Llm.Gateway;
using DocProc.Llm.Providers;
using DocProc.Pipeline.Demo;
using DocProc.Pipeline.Queue;
using DocProc.Pipeline.Tests.Pdf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace DocProc.Pipeline.Tests.Demo;

/// <summary>
/// Keeps the committed demo data of <c>fixtures/demo</c> (docs/09 "Demo data") in step with the code. The replay test
/// boots the processing stack with the API's own <c>appsettings.json</c> and <c>appsettings.Development.json</c> and the
/// demo data switched on, exactly as <c>dotnet run --project src/DocProc.Api</c> does, except that a capturing provider
/// stands in for the replay store. Every LLM request the real stage code sends for the two demo PDFs is hashed with
/// <see cref="RequestHasher"/> (the gateway's hash, computed on the request the provider receives) and paired with its
/// hand-authored answer from <see cref="DemoAnswers"/>; the resulting <see cref="FileReplayStore"/> files must equal the
/// committed ones in <c>fixtures/demo/llm-replay</c>. A prompt, schema, option or PDF change changes a hash and fails
/// here with the instruction to regenerate.
/// </summary>
public sealed class DemoFixtureTests
{
    /// <summary>The header timestamp of the generated fixtures; fixed so regenerating unchanged requests changes no byte.</summary>
    public static readonly DateTimeOffset RecordedAt = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private const string UpdateVariable = "UPDATE_DEMO_FIXTURES";
    private const string RegeneratePdfsVariable = "REGENERATE_DEMO_PDFS";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Llm_replay_fixtures_answer_exactly_the_requests_the_demo_pdfs_produce()
    {
        var repository = RepositoryRoot();
        var committed = Path.Combine(repository, "fixtures", "demo", "llm-replay");
        var work = Path.Combine(Path.GetTempPath(), "docproc-demo-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var capture = new CapturingProviderFactory();

        try
        {
            using (var host = await StartDevelopmentStackAsync(repository, work, capture))
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                cts.CancelAfter(TimeSpan.FromMinutes(2));
                await host.Services.GetRequiredService<InProcessPipelineQueue>().WaitForIdleAsync(cts.Token);

                await using var db = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped();
                var documents = await db.Documents.AsNoTracking().Include(d => d.IngestionItem).ToListAsync(Ct);
                var fileOf = documents.ToDictionary(d => d.Id, d => d.IngestionItem!.FileName);

                var requests = capture.Requests
                    .Select(r => (File: r.DocumentId is { } id && fileOf.TryGetValue(id, out var file) ? file : "(no document)", Request: r))
                    .OrderBy(r => r.File, StringComparer.Ordinal)
                    .ToList();
                requests.Select(r => (r.File, r.Request.Stage)).ShouldBe(
                    [(DemoPdfs.DeliveryNoteFileName, LlmStage.Classify), (DemoPdfs.InvoiceFileName, LlmStage.Extract)],
                    "The demo PDFs must cost exactly one LLM call each: the invoice its extraction (keywords classify it, heuristics keep its two pages together), the delivery note its classification.");

                // The answers went through the real parsing and mapping, so they are valid for the current contracts.
                var invoice = documents.Single(d => d.IngestionItem!.FileName == DemoPdfs.InvoiceFileName);
                invoice.ExtractionMethod.ShouldBe(ExtractionMethod.Llm);
                invoice.DocumentNumber.ShouldBe("2026-131");
                var deliveryNote = documents.Single(d => d.IngestionItem!.FileName == DemoPdfs.DeliveryNoteFileName);
                deliveryNote.Type.ShouldBe(DocumentType.DeliveryNote);
                deliveryNote.TypeSource.ShouldBe(TypeSource.Llm);

                var generated = Path.Combine(work, "llm-replay");
                var store = new FileReplayStore(generated, new FakeTimeProvider(RecordedAt));
                foreach (var (_, request) in requests)
                {
                    store.Put(RequestHasher.Hash(request), request, DemoAnswers.For(request));
                }

                CompareOrUpdate(generated, committed);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }

    [Fact]
    public void Demo_pdfs_are_committed_and_regenerated_only_on_request()
    {
        var folder = Path.Combine(RepositoryRoot(), "fixtures", "demo", "tenant-a");
        if (Environment.GetEnvironmentVariable(RegeneratePdfsVariable) == "1")
        {
            File.WriteAllBytes(Path.Combine(folder, DemoPdfs.InvoiceFileName), DemoPdfs.Invoice());
            File.WriteAllBytes(Path.Combine(folder, DemoPdfs.DeliveryNoteFileName), DemoPdfs.DeliveryNote());
        }

        foreach (var name in new[] { DemoPdfs.InvoiceFileName, DemoPdfs.DeliveryNoteFileName })
        {
            var path = Path.Combine(folder, name);
            File.Exists(path).ShouldBeTrue($"{path} is missing; run the tests once with {RegeneratePdfsVariable}=1 and then {UpdateVariable}=1, and commit both.");
            File.ReadAllBytes(path).AsSpan(0, 5).SequenceEqual("%PDF-"u8).ShouldBeTrue($"{path} is not a PDF.");
        }
    }

    /// <summary>The processing stack composed like the API in development mode, with the capturing provider ahead of every other adapter.</summary>
    private static async Task<IHost> StartDevelopmentStackAsync(string repository, string work, CapturingProviderFactory capture)
    {
        var api = Path.Combine(repository, "src", "DocProc.Api");
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Testing",
            ContentRootPath = api,
            ApplicationName = "DocProc.Pipeline.Tests",
        });
        builder.Configuration
            .AddJsonFile(Path.Combine(api, "appsettings.json"), optional: false)
            .AddJsonFile(Path.Combine(api, "appsettings.Development.json"), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:SqlitePath"] = Path.Combine(work, "docproc.db"),
                ["Blobs:RootPath"] = Path.Combine(work, "blobs"),
                // A replay store must be configured for the demo data to add the Replay provider, but it must not be
                // the committed folder: the capturing factory answers instead, and a stale fixture must not hide a change.
                ["Llm:Replay:Directory"] = Path.Combine(work, "empty-replay"),
                ["Pipeline:RetryBaseDelay"] = "00:00:00.050",
                ["Pipeline:MaxAttempts"] = "2",
            });
        builder.Services.AddLogging();
        // Registered first: the gateway takes the first factory that supports the Replay kind.
        builder.Services.AddSingleton<ILlmProviderFactory>(capture);
        builder.Services.TryAddDocProcPersistence(builder.Configuration, builder.Environment, migrateByDefault: false).ShouldBeTrue();
        builder.AddDocProcProcessingStack(runInProcessDefault: true);
        builder.AddDocProcDemoData();

        var host = builder.Build();
        await host.StartAsync(Ct);
        return host;
    }

    private static void CompareOrUpdate(string generated, string committed)
    {
        var expected = ReadFixtures(generated);
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(committed);
            foreach (var stale in Directory.EnumerateFiles(committed, "*.json"))
            {
                File.Delete(stale);
            }

            foreach (var (name, content) in expected)
            {
                File.WriteAllText(Path.Combine(committed, name), content);
            }

            return;
        }

        var actual = Directory.Exists(committed) ? ReadFixtures(committed) : [];
        var problems = new List<string>();
        problems.AddRange(expected.Keys.Except(actual.Keys).Select(n => $"missing {n}"));
        problems.AddRange(actual.Keys.Except(expected.Keys).Select(n => $"stale {n}"));
        problems.AddRange(expected.Keys.Intersect(actual.Keys).Where(n => expected[n] != actual[n]).Select(n => $"changed {n}"));
        problems.ShouldBeEmpty(
            $"The LLM replay fixtures in {committed} no longer match the requests the demo PDFs produce (a prompt, schema, " +
            $"option, model or PDF changed). Regenerate them with {UpdateVariable}=1 dotnet test --project tests/DocProc.Pipeline.Tests " +
            "and commit fixtures/demo/llm-replay; review the answers in tests/DocProc.Pipeline.Tests/Demo/DemoAnswers.cs if the contract changed.");
    }

    private static Dictionary<string, string> ReadFixtures(string directory) =>
        Directory.EnumerateFiles(directory, "*.json").ToDictionary(p => Path.GetFileName(p), p => File.ReadAllText(p).ReplaceLineEndings("\n"), StringComparer.Ordinal);

    internal static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DocProc.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"No DocProc.slnx above {AppContext.BaseDirectory}.");
    }

    /// <summary>Records every request and answers it from <see cref="DemoAnswers"/>, whatever the order the runner sends them in.</summary>
    private sealed class CapturingProviderFactory : ILlmProviderFactory, ILlmProvider
    {
        private readonly ConcurrentQueue<LlmRequest> _requests = new();

        public IReadOnlyList<LlmRequest> Requests => _requests.ToList();

        public LlmProviderKind Kind => LlmProviderKind.Replay;

        public LlmCapabilities Capabilities => ReplayLlmProvider.ReplayCapabilities;

        public bool Supports(LlmProviderKind kind) => kind == LlmProviderKind.Replay;

        public ILlmProvider Create(LlmProviderConfig config, LlmCredential credential) => this;

        public Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(request);
            return Task.FromResult(DemoAnswers.For(request));
        }

        public Task<BatchHandle> SubmitBatchAsync(IReadOnlyList<LlmRequest> requests, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The demo data never batches.");

        public Task<BatchResult> GetBatchAsync(BatchHandle handle, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The demo data never batches.");

        public Task ValidateCredentialAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
