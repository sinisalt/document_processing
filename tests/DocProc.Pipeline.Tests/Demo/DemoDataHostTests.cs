using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm;
using DocProc.Llm.Providers;
using DocProc.Llm.Schema;
using DocProc.Pipeline.Demo;
using DocProc.Pipeline.Queue;
using DocProc.Pipeline.Tests.Pdf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocProc.Pipeline.Tests.Demo;

/// <summary>
/// The demo data across restarts of the development host (docs/09 "Demo data"): work an earlier process queued but
/// did not finish is resumed, the seeded rows are added once, and the start-up says what it found. Every test runs
/// the composed stack of <see cref="DemoStack"/> on its own work directory and restarts it on the same data.
/// </summary>
public sealed class DemoDataHostTests : IDisposable
{
    private const string MetroInvoice = "METRO_Rechnung_RG-2026-882140.xml";
    private const string MetroCreditNote = "METRO_Gutschrift_GS-2026-31207.xml";
    private const string HoffmannInvoice = "Getraenke-Hoffmann_RE-2026-55318.xml";
    private const string BioHofXmlInvoice = "BioHof-Mueller_Rechnung_2026-118.xml";
    private const string KonzumInvoice = "Konzum_Racun_04518-2026-P12.xml";

    /// <summary>The designed end state of every demo file (docs/09 "Demo data").</summary>
    private static readonly Dictionary<string, DocumentStatus> EndStates = new(StringComparer.Ordinal)
    {
        [MetroInvoice] = DocumentStatus.AutoApproved,
        [MetroCreditNote] = DocumentStatus.AutoApproved,
        [HoffmannInvoice] = DocumentStatus.AutoApproved,
        [KonzumInvoice] = DocumentStatus.AutoApproved,
        [BioHofXmlInvoice] = DocumentStatus.NeedsReview,
        [DemoPdfs.InvoiceFileName] = DocumentStatus.NeedsReview,
        [DemoPdfs.DeliveryNoteFileName] = DocumentStatus.NeedsReview,
    };

    private readonly string _work = Path.Combine(Path.GetTempPath(), "docproc-demo-host", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }

    [Fact]
    public async Task Work_an_interrupted_start_up_left_queued_is_resumed_and_every_demo_document_reaches_its_end_state()
    {
        var data = Path.Combine(_work, "data");
        var onlyDeliveryNote = DemoStack.CopyDemoDocuments(Path.Combine(_work, "demo-first"), DemoPdfs.DeliveryNoteFileName);
        var all = DemoStack.CopyDemoDocuments(Path.Combine(_work, "demo-all"));

        // 1. The delivery note is ingested and processed until its classification call, which never returns: the host
        //    stops (Ctrl+C) with the document Split and the Classify message gone with the in-process queue.
        var holding = new DemoAnswersProvider { Hold = LlmStage.Classify };
        using (var first = await DemoStack.StartAsync(data, holding, s => s["Persistence:DemoDocumentsPath"] = onlyDeliveryNote))
        {
            await holding.Held.Task.WaitAsync(TimeSpan.FromMinutes(2), Ct);
            await first.StopAsync(Ct);
        }

        // 2. The other files are ingested by a host whose queue nobody reads (a watch restart before the runner got to
        //    them): six ingestions stay Received.
        using (var second = await DemoStack.StartAsync(data, new DemoAnswersProvider(), s =>
               {
                   s["Persistence:DemoDocumentsPath"] = all;
                   s["Pipeline:RunInProcess"] = "false";
               }))
        {
            await second.StopAsync(Ct);
            await using var db = Db(second);
            (await db.Ingestions.CountAsync(i => i.Status == IngestionStatus.Received, Ct)).ShouldBe(EndStates.Count - 1);
            var stuck = await db.Documents.AsNoTracking().SingleAsync(Ct);
            stuck.Status.ShouldBe(DocumentStatus.Split, "the delivery note waits for its classification");
        }

        // 3. The next start resumes both: the Received ingestions and the document at the stage that continues it. The
        //    gated queue lets each resumed ingestion be unpacked before the start-up goes on and holds the documents'
        //    messages back, so the documents this start creates are Received while it looks for interrupted ones and
        //    must not be mistaken for them (and queued twice).
        var logs = new LogSink();
        var answering = new DemoAnswersProvider();
        var gate = new GatedQueue();
        Snapshot settled;
        using (var third = await DemoStack.StartAsync(
                   data,
                   answering,
                   s => s["Persistence:DemoDocumentsPath"] = all,
                   logs,
                   services => services.AddSingleton<IPipelineQueue>(sp => gate.Attach(sp.GetRequiredService<InProcessPipelineQueue>()))))
        {
            await gate.OpenAsync(Ct);
            await SettleAsync(third);
            await using var db = Db(third);
            var documents = await db.Documents.AsNoTracking().Include(d => d.IngestionItem).ToListAsync(Ct);
            documents.Select(d => (d.IngestionItem!.FileName, d.Status)).OrderBy(d => d.FileName, StringComparer.Ordinal)
                .ShouldBe(EndStates.Select(e => (e.Key, e.Value)).OrderBy(e => e.Key, StringComparer.Ordinal));
            documents.Single(d => d.IngestionItem!.FileName == DemoPdfs.DeliveryNoteFileName).Type.ShouldBe(DocumentType.DeliveryNote);
            (await db.Ingestions.CountAsync(i => i.Status == IngestionStatus.Received, Ct)).ShouldBe(0);
            (await db.OutboxMessages.CountAsync(Ct)).ShouldBe(4, "every auto-approved document is published");
            settled = await SnapshotAsync(db);

            var resumed = logs.Demo.Where(l => l.Message.StartsWith("Resumed demo", StringComparison.Ordinal)).Select(l => l.Message).ToList();
            resumed.Count(m => m.StartsWith("Resumed demo ingestion", StringComparison.Ordinal)).ShouldBe(EndStates.Count - 1);
            resumed.Where(m => m.StartsWith("Resumed demo document", StringComparison.Ordinal)).ShouldHaveSingleItem()
                .ShouldContain($"({DemoPdfs.DeliveryNoteFileName}) of tenant tenant-a: Split, queued for stage Classify");
            logs.Demo.ShouldContain(l => l.Message.Contains("0 row(s) inserted, 7 interrupted ingestion(s) or document(s) resumed, 0 document file(s) queued", StringComparison.Ordinal));
        }

        // 4. Once everything settled, a restart resumes nothing, seeds nothing and calls no LLM.
        logs.Clear();
        var idle = new DemoAnswersProvider();
        using (var fourth = await DemoStack.StartAsync(data, idle, s => s["Persistence:DemoDocumentsPath"] = all, logs))
        {
            await SettleAsync(fourth);
            await using var db = Db(fourth);
            (await SnapshotAsync(db)).ShouldBe(settled);
            idle.Requests.ShouldBeEmpty();
            logs.Demo.ShouldNotContain(l => l.Message.StartsWith("Resumed demo", StringComparison.Ordinal));
            logs.Demo.ShouldContain(l => l.Message.Contains("0 row(s) inserted, 0 interrupted ingestion(s) or document(s) resumed, 0 document file(s) queued", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Members_and_the_replay_configuration_are_seeded_once_so_deleted_rows_stay_deleted()
    {
        var data = Path.Combine(_work, "data");
        var empty = Directory.CreateDirectory(Path.Combine(_work, "no-documents")).FullName;
        var reviewer = DemoData.Members.Single(m => m.Username == "tenant-a-reviewer");

        int usage;
        using (var first = await DemoStack.StartAsync(data, configure: s => s["Persistence:DemoDocumentsPath"] = empty))
        {
            await using var db = Db(first);
            (await db.TenantMembers.CountAsync(Ct)).ShouldBe(DemoData.Members.Count);
            (await db.LlmProviderConfigs.AnyAsync(c => c.Id == DemoData.PlatformReplayConfig, Ct)).ShouldBeTrue();
            usage = await db.LlmUsage.CountAsync(Ct);
            usage.ShouldBeGreaterThan(0);

            // What DELETE /members/{userId} and DELETE /llm/providers/{id} do.
            db.TenantMembers.Remove(await db.TenantMembers.SingleAsync(m => m.TenantId == reviewer.TenantId && m.UserId == reviewer.UserId, Ct));
            db.LlmProviderConfigs.Remove(await db.LlmProviderConfigs.SingleAsync(c => c.Id == DemoData.PlatformReplayConfig, Ct));
            await db.SaveChangesAsync(Ct);
            await first.StopAsync(Ct);
        }

        var logs = new LogSink();
        using (var again = await DemoStack.StartAsync(data, configure: s => s["Persistence:DemoDocumentsPath"] = empty, logs: logs))
        {
            await using var db = Db(again);
            (await db.TenantMembers.AnyAsync(m => m.TenantId == reviewer.TenantId && m.UserId == reviewer.UserId, Ct)).ShouldBeFalse("a removed member stays removed");
            (await db.TenantMembers.CountAsync(Ct)).ShouldBe(DemoData.Members.Count - 1);
            (await db.LlmProviderConfigs.AnyAsync(c => c.Id == DemoData.PlatformReplayConfig, Ct)).ShouldBeFalse("a deleted Replay configuration is not re-created");
            (await db.LlmUsage.CountAsync(Ct)).ShouldBe(usage);
            logs.Demo.ShouldContain(l => l.Message.Contains("0 row(s) inserted", StringComparison.Ordinal));
            logs.Demo.ShouldContain(l => l.Level == LogLevel.Warning && l.Message.StartsWith("No platform LLM configuration is active", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_database_already_running_on_a_real_provider_keeps_it_and_the_start_up_names_it(bool keyStored)
    {
        var data = Path.Combine(_work, "data");
        var empty = Directory.CreateDirectory(Path.Combine(_work, "no-documents")).FullName;

        // A dev database from before the demo data, on which the developer enabled the platform Anthropic key.
        using (var before = await DemoStack.StartAsync(data, configure: s => s["Persistence:SeedDemoDocuments"] = "false"))
        {
            await using var db = Db(before);
            var anthropic = await db.LlmProviderConfigs.SingleAsync(c => c.Id == DevSeed.PlatformAnthropicConfig, Ct);
            anthropic.Status = ProviderConfigStatus.Active;
            await db.SaveChangesAsync(Ct);
            await before.StopAsync(Ct);
        }

        var logs = new LogSink();
        using (var demo = await DemoStack.StartAsync(
                   data,
                   configure: s =>
                   {
                       s["Persistence:DemoDocumentsPath"] = empty;
                       s[$"Llm:Secrets:{DevSeed.PlatformSecretRef}"] = keyStored ? "sk-test" : null;
                   },
                   logs: logs))
        {
            await using var db = Db(demo);
            (await db.LlmProviderConfigs.AnyAsync(c => c.Id == DemoData.PlatformReplayConfig, Ct)).ShouldBeFalse();
            (await DemoData.PlatformConfigInUseAsync(db, Ct))!.Id.ShouldBe(DevSeed.PlatformAnthropicConfig);
            (await db.TenantMembers.CountAsync(Ct)).ShouldBe(DemoData.Members.Count, "the other demo rows are added as usual");
            if (keyStored)
            {
                logs.Demo.ShouldContain(l => l.Level == LogLevel.Information && l.Message == $"LLM platform configuration in use: {DevSeed.PlatformAnthropicConfig} (Anthropic).");
            }
            else
            {
                // An active configuration the gateway cannot get a key for is not "in use": every call would fail.
                logs.Demo.ShouldNotContain(l => l.Message.StartsWith("LLM platform configuration in use", StringComparison.Ordinal));
                logs.Demo.ShouldContain(l => l.Level == LogLevel.Warning
                                             && l.Message.StartsWith($"LLM platform configuration {DevSeed.PlatformAnthropicConfig} (Anthropic) is active but has no usable key", StringComparison.Ordinal)
                                             && l.Message.Contains(DevSeed.PlatformSecretRef, StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public async Task On_a_fresh_database_the_replay_configuration_is_in_use_and_a_miss_says_the_document_has_no_demo_fixture()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_work, "no-documents")).FullName;
        var logs = new LogSink();
        using var host = await DemoStack.StartAsync(Path.Combine(_work, "data"), configure: s => s["Persistence:DemoDocumentsPath"] = empty, logs: logs);

        logs.Demo.ShouldContain(l => l.Message.StartsWith($"LLM platform configuration in use: {DemoData.PlatformReplayConfig} (Replay, recorded responses from ", StringComparison.Ordinal));

        // The replay factory the gateway uses (no test provider here), asked for a request nobody recorded.
        var factory = host.Services.GetServices<ILlmProviderFactory>().First(f => f.Supports(LlmProviderKind.Replay));
        var config = new LlmProviderConfig
        {
            Id = DemoData.PlatformReplayConfig,
            Scope = ConfigScope.Platform,
            Kind = LlmProviderKind.Replay,
            Auth = new LlmAuth(LlmAuthType.ApiKey, null),
        };
        var request = new LlmRequest
        {
            Stage = LlmStage.Extract,
            Model = "claude-opus-5-5",
            Prompt = new PromptBundle { Id = "extract-invoice", Version = "1", System = "You extract invoices.", Task = "Not a demo document." },
            OutputSchema = CanonicalSchemas.InvoiceExtraction,
            Document = new DocumentInput { Pdf = "%PDF-1.7 something else"u8.ToArray() },
        };

        var miss = await Should.ThrowAsync<LlmException>(() => factory.Create(config, new LlmCredential()).CompleteAsync(request, Ct));
        miss.Kind.ShouldBe(LlmErrorKind.InvalidRequest);
        miss.Message.ShouldStartWith("No recorded response for request ");
        miss.Message.ShouldEndWith(DemoData.ReplayMissHint);
        miss.Message.ShouldNotContain(ReplayLlmProvider.DefaultMissHint);
    }

    [Fact]
    public async Task A_settled_demo_document_whose_publish_message_was_lost_is_published_on_the_next_start()
    {
        var data = Path.Combine(_work, "data");
        var metro = DemoStack.CopyDemoDocuments(Path.Combine(_work, "demo-metro"), MetroInvoice);
        Guid documentId;
        using (var first = await DemoStack.StartAsync(data, new DemoAnswersProvider(), s => s["Persistence:DemoDocumentsPath"] = metro))
        {
            await SettleAsync(first);
            await using var db = Db(first);
            var document = await db.Documents.AsNoTracking().SingleAsync(Ct);
            document.Status.ShouldBe(DocumentStatus.AutoApproved);
            documentId = document.Id;

            // The process died after validation committed AutoApproved and before the queued publish message ran.
            db.OutboxMessages.RemoveRange(await db.OutboxMessages.ToListAsync(Ct));
            await db.SaveChangesAsync(Ct);
            await first.StopAsync(Ct);
        }

        var logs = new LogSink();
        using (var again = await DemoStack.StartAsync(data, new DemoAnswersProvider(), s => s["Persistence:DemoDocumentsPath"] = metro, logs))
        {
            await SettleAsync(again);
            await using var db = Db(again);
            var message = await db.OutboxMessages.AsNoTracking().SingleAsync(Ct);
            message.DocumentId.ShouldBe(documentId);
            message.EventType.ShouldBe(DocumentEventTypes.Approved);
            logs.Demo.ShouldContain(l => l.Message.StartsWith($"Resumed demo document {documentId} ({MetroInvoice}) of tenant ", StringComparison.Ordinal)
                                         && l.Message.EndsWith("AutoApproved but not in the outbox since it settled, queued for stage Publish.", StringComparison.Ordinal));
        }

        // Published now: a further restart queues nothing.
        logs.Clear();
        using (var third = await DemoStack.StartAsync(data, new DemoAnswersProvider(), s => s["Persistence:DemoDocumentsPath"] = metro, logs))
        {
            await SettleAsync(third);
            await using var db = Db(third);
            (await db.OutboxMessages.CountAsync(Ct)).ShouldBe(1);
            logs.Demo.ShouldNotContain(l => l.Message.StartsWith("Resumed demo", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_demo_document_settled_again_after_an_earlier_settlement_was_published_gets_its_new_event_on_the_next_start()
    {
        var data = Path.Combine(_work, "data");
        var metro = DemoStack.CopyDemoDocuments(Path.Combine(_work, "demo-metro"), MetroInvoice);
        Guid documentId;
        using (var first = await DemoStack.StartAsync(data, new DemoAnswersProvider(), s => s["Persistence:DemoDocumentsPath"] = metro))
        {
            await SettleAsync(first);
            await using var db = Db(first);
            var document = await db.Documents.AsNoTracking().SingleAsync(Ct);
            document.Status.ShouldBe(DocumentStatus.AutoApproved);
            documentId = document.Id;

            // The document was rejected first (its Rejected row is in the outbox), then reprocessed and settled again;
            // the process died before the new publish message ran, so only the old row is left.
            var message = await db.OutboxMessages.SingleAsync(Ct);
            message.EventType = DocumentEventTypes.Rejected;
            message.CreatedAt = (document.ApprovedAt ?? document.UpdatedAt).AddHours(-1);
            await db.SaveChangesAsync(Ct);
            await first.StopAsync(Ct);
        }

        var logs = new LogSink();
        using (var again = await DemoStack.StartAsync(data, new DemoAnswersProvider(), s => s["Persistence:DemoDocumentsPath"] = metro, logs))
        {
            await SettleAsync(again);
            await using var db = Db(again);
            var messages = await db.OutboxMessages.AsNoTracking().Where(m => m.DocumentId == documentId).ToListAsync(Ct);
            messages.Select(m => m.EventType).Order(StringComparer.Ordinal).ShouldBe([DocumentEventTypes.Approved, DocumentEventTypes.Rejected]);
            logs.Demo.ShouldContain(l => l.Message.StartsWith($"Resumed demo document {documentId} ({MetroInvoice}) of tenant ", StringComparison.Ordinal)
                                         && l.Message.EndsWith("AutoApproved but not in the outbox since it settled, queued for stage Publish.", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_missing_demo_folder_is_reported_and_the_rows_are_seeded_anyway()
    {
        var missing = Path.Combine(_work, "no-such-folder");
        var logs = new LogSink();
        using var host = await DemoStack.StartAsync(Path.Combine(_work, "data"), configure: s => s["Persistence:DemoDocumentsPath"] = missing, logs: logs);

        logs.Demo.ShouldContain(l => l.Level == LogLevel.Warning && l.Message.StartsWith($"Demo documents skipped: the folder {missing} does not exist.", StringComparison.Ordinal));
        await using var db = Db(host);
        (await db.Ingestions.CountAsync(Ct)).ShouldBe(0);
        (await db.TenantMembers.CountAsync(Ct)).ShouldBe(DemoData.Members.Count);
    }

    [Fact]
    public async Task Demo_documents_go_through_an_active_api_upload_channel_only()
    {
        var data = Path.Combine(_work, "data");
        var all = DemoStack.CopyDemoDocuments(Path.Combine(_work, "demo-all"));
        Guid replacement;
        using (var before = await DemoStack.StartAsync(data, configure: s => s["Persistence:SeedDemoDocuments"] = "false"))
        {
            await using var db = Db(before);
            var seeded = await db.IngestionChannels.Where(c => c.Type == ChannelType.ApiUpload).ToListAsync(Ct);
            seeded.Select(c => c.TenantId).ShouldBe([DevSeed.TenantA, DevSeed.TenantB], ignoreOrder: true);
            foreach (var channel in seeded)
            {
                channel.Status = ChannelStatus.Paused;
            }

            // Tenant B gets a newer, active API channel; tenant A is left with its paused one only.
            replacement = Guid.CreateVersion7();
            db.IngestionChannels.Add(new IngestionChannel
            {
                Id = replacement,
                TenantId = DevSeed.TenantB,
                Name = "API upload (new)",
                Type = ChannelType.ApiUpload,
                Status = ChannelStatus.Active,
                CreatedAt = seeded.Max(c => c.CreatedAt).AddMinutes(1),
            });
            await db.SaveChangesAsync(Ct);
            await before.StopAsync(Ct);
        }

        var logs = new LogSink();
        using var demo = await DemoStack.StartAsync(data, configure: s =>
        {
            s["Persistence:DemoDocumentsPath"] = all;
            s["Pipeline:RunInProcess"] = "false";
        }, logs: logs);

        await using var check = Db(demo);
        var ingestions = await check.Ingestions.AsNoTracking().ToListAsync(Ct);
        ingestions.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            i => i.TenantId.ShouldBe(DevSeed.TenantB),
            i => i.SourceFileName.ShouldBe(KonzumInvoice),
            i => i.ChannelId.ShouldBe(replacement));
        logs.Demo.ShouldContain(l => l.Level == LogLevel.Warning && l.Message == "Demo documents of tenant tenant-a skipped: the tenant has no active API-upload channel.");
    }

    private static DocProcDbContext Db(IHost host) => host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped();

    private static async Task SettleAsync(IHost host)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMinutes(3));
        await host.Services.GetRequiredService<InProcessPipelineQueue>().WaitForIdleAsync(cts.Token);
    }

    private static async Task<Snapshot> SnapshotAsync(DocProcDbContext db) => new(
        await db.Ingestions.CountAsync(Ct),
        await db.Documents.CountAsync(Ct),
        await db.DocumentEvents.CountAsync(Ct),
        await db.TenantMembers.CountAsync(Ct),
        await db.LlmUsage.CountAsync(Ct),
        await db.LlmProviderConfigs.CountAsync(Ct),
        await db.OutboxMessages.CountAsync(Ct));

    private sealed record Snapshot(int Ingestions, int Documents, int DocumentEvents, int Members, int UsageRows, int ProviderConfigs, int OutboxMessages);
}
