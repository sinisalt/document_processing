using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DocProc.Pipeline.Tests.Demo;

/// <summary>
/// Keeps the committed demo data of <c>fixtures/demo</c> (docs/09 "Demo data") in step with the code. The replay test
/// boots the processing stack with the API's own <c>appsettings.json</c> and <c>appsettings.Development.json</c> and the
/// demo data switched on, exactly as <c>dotnet run --project src/DocProc.Api</c> does (<see cref="DemoStack"/>), except
/// that a capturing provider stands in for the replay store. Every LLM request the real stage code sends for the two
/// demo PDFs is hashed with <see cref="RequestHasher"/> (the gateway's hash, computed on the request the provider
/// receives) and paired with its hand-authored answer from <see cref="DemoAnswers"/>; the resulting
/// <see cref="FileReplayStore"/> files must equal the committed ones in <c>fixtures/demo/llm-replay</c>. A prompt, schema,
/// option or PDF change changes a hash and fails here with the instruction to regenerate. The classification fixture
/// hashes the page PDFium renders, which only matches on <see cref="DemoData.FixtureCapturePlatform"/>: elsewhere only
/// the extraction fixture is compared and the test reports itself skipped.
/// </summary>
public sealed class DemoFixtureTests
{
    /// <summary>The header timestamp of the generated fixtures; fixed so regenerating unchanged requests changes no byte.</summary>
    public static readonly DateTimeOffset RecordedAt = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private const string UpdateVariable = "UPDATE_DEMO_FIXTURES";
    private const string RegeneratePdfsVariable = "REGENERATE_DEMO_PDFS";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Why the classification fixture is not compared on this machine (docs/09 "Demo data").</summary>
    internal static string OtherPlatformExplanation =>
        $"The classification fixture of the demo delivery note hashes its first page as PDFium renders it on {DemoData.FixtureCapturePlatform}, " +
        $"where the committed fixtures are captured; this machine is {RuntimeInformation.RuntimeIdentifier} and rasterises differently, so the " +
        "delivery note gets a replay miss here and only the platform-independent checks ran. See docs/09-local-development.md, \"Demo data\"; " +
        "do not commit a classification fixture regenerated on this machine.";

    [Fact]
    public async Task Llm_replay_fixtures_answer_exactly_the_requests_the_demo_pdfs_produce()
    {
        var repository = RepositoryRoot();
        var committed = Path.Combine(repository, "fixtures", "demo", "llm-replay");
        var work = Path.Combine(Path.GetTempPath(), "docproc-demo-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var capture = new DemoAnswersProvider();

        try
        {
            using (var host = await DemoStack.StartAsync(work, capture))
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

                CompareOrUpdate(generated, committed, DemoData.IsFixtureCapturePlatform);
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

        if (!DemoData.IsFixtureCapturePlatform)
        {
            Assert.Skip(OtherPlatformExplanation);
        }
    }

    [Fact]
    public void Off_the_capture_platform_only_the_fixtures_keyed_by_document_bytes_are_compared()
    {
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Assert.Skip($"{UpdateVariable}=1 rewrites instead of comparing.");
        }

        var root = Path.Combine(Path.GetTempPath(), "docproc-demo-compare", Guid.NewGuid().ToString("N"));
        var generated = Directory.CreateDirectory(Path.Combine(root, "generated")).FullName;
        var committed = Directory.CreateDirectory(Path.Combine(root, "committed")).FullName;
        static string Fixture(string stage, string answer) => $$"""{ "header": { "stage": "{{stage}}" }, "json": { "answer": "{{answer}}" } }""";
        try
        {
            File.WriteAllText(Path.Combine(generated, "aaaa.json"), Fixture("Extract", "invoice"));
            File.WriteAllText(Path.Combine(generated, "bbbb.json"), Fixture("Classify", "delivery note"));
            File.WriteAllText(Path.Combine(committed, "aaaa.json"), Fixture("Extract", "invoice"));
            // The classification fixture captured on linux-x64: another PNG, so another hash than this machine computes.
            File.WriteAllText(Path.Combine(committed, "cccc.json"), Fixture("Classify", "delivery note"));

            Should.NotThrow(() => CompareOrUpdate(generated, committed, includeRenderedPages: false));
            Should.Throw<ShouldAssertException>(() => CompareOrUpdate(generated, committed, includeRenderedPages: true))
                .Message.ShouldSatisfyAllConditions(m => m.ShouldContain("missing bbbb.json"), m => m.ShouldContain("stale cccc.json"));

            // The extraction fixture stays strict everywhere.
            File.WriteAllText(Path.Combine(committed, "aaaa.json"), Fixture("Extract", "an older answer"));
            Should.Throw<ShouldAssertException>(() => CompareOrUpdate(generated, committed, includeRenderedPages: false))
                .Message.ShouldContain("changed aaaa.json");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
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

    /// <param name="generated">The fixtures the current code produces.</param>
    /// <param name="committed">The repository's fixtures.</param>
    /// <param name="includeRenderedPages">
    /// False off the capture platform: the fixtures keyed by a rendered page (classification) are neither compared nor
    /// rewritten there, only the ones keyed by document bytes.
    /// </param>
    internal static void CompareOrUpdate(string generated, string committed, bool includeRenderedPages)
    {
        bool Compared(string content) => includeRenderedPages || !IsRenderedPageFixture(content);

        var expected = ReadFixtures(generated).Where(f => Compared(f.Value)).ToDictionary(StringComparer.Ordinal);
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(committed);
            foreach (var stale in Directory.EnumerateFiles(committed, "*.json").Where(p => Compared(File.ReadAllText(p))))
            {
                File.Delete(stale);
            }

            foreach (var (name, content) in expected)
            {
                File.WriteAllText(Path.Combine(committed, name), content);
            }

            return;
        }

        var actual = Directory.Exists(committed)
            ? ReadFixtures(committed).Where(f => Compared(f.Value)).ToDictionary(StringComparer.Ordinal)
            : [];
        var problems = new List<string>();
        problems.AddRange(expected.Keys.Except(actual.Keys).Select(n => $"missing {n}"));
        problems.AddRange(actual.Keys.Except(expected.Keys).Select(n => $"stale {n}"));
        problems.AddRange(expected.Keys.Intersect(actual.Keys).Where(n => expected[n] != actual[n]).Select(n => $"changed {n}"));
        problems.ShouldBeEmpty(
            $"The LLM replay fixtures in {committed} no longer match the requests the demo PDFs produce (a prompt, schema, " +
            $"option, model or PDF changed). Regenerate them with {UpdateVariable}=1 dotnet test --project tests/DocProc.Pipeline.Tests " +
            "and commit fixtures/demo/llm-replay; review the answers in tests/DocProc.Pipeline.Tests/Demo/DemoAnswers.cs if the contract changed.");
    }

    /// <summary>A classification fixture: its request carries the first page as a PNG, whose bytes depend on the PDFium build.</summary>
    private static bool IsRenderedPageFixture(string content) =>
        JsonNode.Parse(content)?["header"]?["stage"]?.GetValue<string>() == nameof(LlmStage.Classify);

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
}
