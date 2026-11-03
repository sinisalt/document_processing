using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Catalogue;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm;
using DocProc.Llm.Gateway;
using DocProc.Llm.Providers;
using DocProc.Pipeline.Demo;
using DocProc.Pipeline.Intake;
using DocProc.Pipeline.Queue;
using DocProc.Pipeline.Review;
using DocProc.Pipeline.Support;
using DocProc.Pipeline.Tests.Pdf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
                deliveryNote.Type.ShouldBe(DocumentTypeKey.DeliveryNote);
                deliveryNote.TypeSource.ShouldBe(TypeSource.Llm);

                // docs/09 "Document types": the tenant forks Invoice with the demo custom field and reprocesses the PDF
                // invoice with configMode current; the x_custom extraction it then sends has a fixture of its own.
                await ForkWithFieldAsync(host, invoice.TenantId, "std.invoice", "Invoice", CustomFieldPath());
                await ReprocessFromExtractWithCurrentConfigAsync(host, invoice);
                await host.Services.GetRequiredService<InProcessPipelineQueue>().WaitForIdleAsync(cts.Token);
                var custom = capture.Requests.Where(r => r.DocumentId == invoice.Id && r.Stage == LlmStage.Extract).ToList();
                custom.Count.ShouldBe(2);
                custom[1].OutputSchema!.Version.ShouldStartWith("1+x.", customMessage: "the reprocess after the fork asks for the custom field in x_custom");
                await using (var after = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped())
                {
                    var reprocessed = await after.Documents.AsNoTracking().SingleAsync(d => d.Id == invoice.Id, Ct);
                    reprocessed.CanonicalSchemaVersion.ShouldBe(2);
                    JsonNode.Parse(reprocessed.CanonicalJson!)!["supplierCustomerRef"]!["value"]!["verbatim"]!.GetValue<string>().ShouldBe(DemoAnswers.CustomerNumber);
                }

                requests.Add((DemoPdfs.InvoiceFileName, custom[1]));

                // docs/09 "Demo: types as data": the tenant forks CreditNote in place with the custom field of docs/11
                // §5.3 and uploads the Getränke Hoffmann credit note, which is classified by its keywords and extracted
                // with x_custom at once; that extraction is the fourth fixture.
                await ForkWithFieldAsync(host, invoice.TenantId, "std.credit-note", "CreditNote", CreditNoteFieldPath());
                var before = capture.Requests.Count;
                var creditNoteId = await UploadAsync(host, invoice.TenantId, CreditNotePath());
                await host.Services.GetRequiredService<InProcessPipelineQueue>().WaitForIdleAsync(cts.Token);
                var creditNoteRequests = capture.Requests.Skip(before).ToList();
                creditNoteRequests.Select(r => r.Stage).ShouldBe([LlmStage.Extract], "the credit note costs one LLM call: keywords classify it ('Gutschrift'), the extraction asks for the custom field");
                creditNoteRequests[0].DocumentId.ShouldBe(creditNoteId);
                creditNoteRequests[0].OutputSchema!.Version.ShouldStartWith("1+x.");
                await using (var after = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped())
                {
                    var creditNote = await after.Documents.AsNoTracking().Include(d => d.Flags).SingleAsync(d => d.Id == creditNoteId, Ct);
                    creditNote.Type.ShouldBe(DocumentTypeKey.CreditNote);
                    creditNote.TypeSource.ShouldBe(TypeSource.Rule);
                    creditNote.DocumentNumber.ShouldBe(DemoAnswers.CreditNoteNumber);
                    creditNote.CanonicalSchemaVersion.ShouldBe(2);
                    creditNote.Status.ShouldBe(DocumentStatus.NeedsReview, "the worked-out due date is not printed, so LOW_CONFIDENCE sends the credit note to review");
                    creditNote.Flags.Select(f => f.Code).ShouldContain("LOW_CONFIDENCE");
                    var scr = JsonNode.Parse(creditNote.CanonicalJson!)!["supplierCustomerRef"]!;
                    scr["value"]!["verbatim"]!.GetValue<string>().ShouldBe(DemoPdfs.CreditNoteCustomerNumber);
                    scr["source"].ShouldNotBeNull("the value has LLM provenance located in the text layer");
                    (await after.DocumentFieldIndex.AsNoTracking().CountAsync(i => i.DocumentId == creditNoteId && i.FieldKey == "supplierCustomerRef", Ct)).ShouldBe(1);
                }

                requests.Add((DemoPdfs.CreditNoteFileName, creditNoteRequests[0]));

                // docs/09 "Demo: manual mapping" (the P2 demo of docs/11 §23): the tenant has adopted std.price-list and
                // uploads the 14-page Nordwind price list. 14 pages are more than the heuristic splitter decides alone,
                // so the split stage asks the LLM (one document); no keyword rule knows a price list, so the classifier
                // asks the LLM, whose types have none either ("Other"). It arrives in review without a form.
                await AdoptAsync(host, invoice.TenantId, "std.price-list");
                before = capture.Requests.Count;
                var priceListId = await UploadAsync(host, invoice.TenantId, PriceListPath());
                await host.Services.GetRequiredService<InProcessPipelineQueue>().WaitForIdleAsync(cts.Token);
                var priceListRequests = capture.Requests.Skip(before).ToList();
                priceListRequests.Select(r => r.Stage).ShouldBe([LlmStage.Split, LlmStage.Classify], "the price list costs a split call (too long for the heuristics) and a classification call, and no extraction");
                priceListRequests.ShouldAllBe(r => r.DocumentId == priceListId);
                await using (var after = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped())
                {
                    var priceList = await after.Documents.AsNoTracking().Include(d => d.Flags).SingleAsync(d => d.Id == priceListId, Ct);
                    priceList.PageCount.ShouldBe(DemoPriceList.PageCount, "the LLM splitter keeps the 14 pages together");
                    priceList.Type.ShouldBe(DocumentTypeKey.Other);
                    priceList.TypeSource.ShouldBe(TypeSource.Llm);
                    priceList.CanonicalJson.ShouldBeNull();
                    priceList.Status.ShouldBe(DocumentStatus.NeedsReview);
                    priceList.Flags.Select(f => f.Code).ShouldContain("UNSUPPORTED_TYPE");
                    (await after.Documents.CountAsync(d => d.IngestionItemId == priceList.IngestionItemId, Ct)).ShouldBe(1);
                }

                requests.AddRange(priceListRequests.Select(r => (DemoPriceList.FileName, r)));

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

    /// <summary>
    /// docs/09 "Demo: manual mapping", the split and join of the invoice 2026-131: joined back, the document is the whole
    /// file again and its extraction is the request of the upload, which the committed fixture answers. That holds only
    /// while no other Bio-Hof Müller document is approved: the extraction then carries the supplier's hints (its last
    /// approved document and that document's article numbers), so the request, and its hash, change and the replay
    /// store has no answer for it (the joined invoice ends Failed in the compose stack).
    /// </summary>
    [Fact]
    public async Task Joined_back_the_demo_invoice_is_read_from_its_fixture_until_a_document_of_its_supplier_is_approved()
    {
        var committed = Path.Combine(RepositoryRoot(), "fixtures", "demo", "llm-replay");
        var work = Path.Combine(Path.GetTempPath(), "docproc-demo-join", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var capture = new DemoAnswersProvider();
        try
        {
            using var host = await DemoStack.StartAsync(work, capture);
            var queue = host.Services.GetRequiredService<InProcessPipelineQueue>();
            await WaitIdleAsync(queue);
            Guid invoiceId;
            Guid tenantId;
            await using (var db = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped())
            {
                var invoice = await db.Documents.AsNoTracking().SingleAsync(d => d.IngestionItem!.FileName == DemoPdfs.InvoiceFileName, Ct);
                (invoiceId, tenantId) = (invoice.Id, invoice.TenantId);
            }

            string LastExtractionHash() => RequestHasher.Hash(capture.Requests.Last(r => r.DocumentId == invoiceId && r.Stage == LlmStage.Extract));
            var uploaded = LastExtractionHash();
            File.Exists(Path.Combine(committed, uploaded + ".json")).ShouldBeTrue("the upload's extraction is the committed fixture");

            await SplitAndJoinAsync(host, invoiceId, tenantId);
            LastExtractionHash().ShouldBe(uploaded, "joined back, the invoice is read with the request of the upload");

            // A reviewer approves the Bio-Hof e-invoice 2026-118 (or the delivery note of "Demo: types as data", step 7).
            await using (var db = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped())
            {
                var other = await db.Documents.SingleAsync(d => d.IngestionItem!.FileName == "BioHof-Mueller_Rechnung_2026-118.xml", Ct);
                other.SupplierId.ShouldNotBeNull();
                (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == invoiceId, Ct)).SupplierId.ShouldBe(other.SupplierId);
                other.Status = DocumentStatus.Approved;
                other.ApprovedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(Ct);
            }

            await SplitAndJoinAsync(host, invoiceId, tenantId);
            var hinted = LastExtractionHash();
            hinted.ShouldNotBe(uploaded, "the request now carries the supplier's last approved document");
            File.Exists(Path.Combine(committed, hinted + ".json")).ShouldBeFalse("no fixture answers it: a replay miss in the compose stack");
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

    private static async Task WaitIdleAsync(InProcessPipelineQueue queue)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        await queue.WaitForIdleAsync(cts.Token);
    }

    /// <summary>What More > Split into documents (page 2 starts a document) and then Join parts on page 1 do (<c>:split</c>, <c>:join</c>).</summary>
    private static async Task SplitAndJoinAsync(IHost host, Guid id, Guid tenantId)
    {
        var queue = host.Services.GetRequiredService<InProcessPipelineQueue>();
        var store = host.Services.GetRequiredService<IContainerBlobStore>();
        var pdf = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DocProc.Infrastructure.Pdf.PdfProcessingOptions>>().Value;
        IReadOnlyList<ReviewPartOutcome> parts;
        await using (var db = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped())
        {
            var document = await db.Documents.Include(d => d.Flags).SingleAsync(d => d.Id == id, Ct);
            parts = await ReviewPartitioning.SplitAsync(db, store, pdf, document, [new ReviewSplitPart([1], null, null), new ReviewSplitPart([2], null, null)], "tenant-a-reviewer", DateTimeOffset.UtcNow, Ct);
            await db.SaveChangesAsync(Ct);
        }

        foreach (var part in parts)
        {
            await queue.EnqueueAsync(new ProcessDocument(tenantId, part.DocumentId, part.Stage), Ct);
        }

        await WaitIdleAsync(queue);
        await using (var db = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped())
        {
            var target = await db.Documents.Include(d => d.Flags).SingleAsync(d => d.Id == id, Ct);
            var otherId = parts.Single(p => p.DocumentId != id).DocumentId;
            var other = await db.Documents.Include(d => d.Flags).SingleAsync(d => d.Id == otherId, Ct);
            var joined = await ReviewPartitioning.JoinAsync(db, store, pdf, target, [other], "tenant-a-reviewer", DateTimeOffset.UtcNow, Ct);
            await db.SaveChangesAsync(Ct);
            await queue.EnqueueAsync(new ProcessDocument(tenantId, joined.DocumentId, joined.Stage), Ct);
        }

        await WaitIdleAsync(queue);
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
        var demo = Path.Combine(RepositoryRoot(), "fixtures", "demo");
        (string Path, Func<byte[]> Draw)[] pdfs =
        [
            (Path.Combine(demo, "tenant-a", DemoPdfs.InvoiceFileName), DemoPdfs.Invoice),
            (Path.Combine(demo, "tenant-a", DemoPdfs.DeliveryNoteFileName), DemoPdfs.DeliveryNote),
            (CreditNotePath(), DemoPdfs.CreditNote),
            (PriceListPath(), DemoPriceList.Pdf),
        ];

        // "1" redraws every PDF; a file name redraws only that one, so adding a demo PDF leaves the others' bytes alone.
        var regenerate = Environment.GetEnvironmentVariable(RegeneratePdfsVariable);
        foreach (var (path, draw) in pdfs.Where(p => regenerate == "1" || regenerate == Path.GetFileName(p.Path)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, draw());
        }

        foreach (var (path, _) in pdfs)
        {
            File.Exists(path).ShouldBeTrue($"{path} is missing; run the tests once with {RegeneratePdfsVariable}={Path.GetFileName(path)} and then {UpdateVariable}=1, and commit both.");
            File.ReadAllBytes(path).AsSpan(0, 5).SequenceEqual("%PDF-"u8).ShouldBeTrue($"{path} is not a PDF.");
        }
    }

    /// <summary>The demo custom field of docs/09 "Document types" (<c>fixtures/demo/types</c>), as the curl steps there add it.</summary>
    internal static string CustomFieldPath() => Path.Combine(RepositoryRoot(), "fixtures", "demo", "types", "invoice-customer-number.field.json");

    /// <summary>The custom field of docs/11 §5.3 that docs/09 "Demo: types as data" adds to the tenant's fork of CreditNote.</summary>
    internal static string CreditNoteFieldPath() => Path.Combine(RepositoryRoot(), "fixtures", "demo", "types", "credit-note-supplier-customer-ref.field.json");

    /// <summary>The credit note that demo uploads (not ingested by the demo data: <c>uploads/</c> is no tenant folder).</summary>
    internal static string CreditNotePath() => Path.Combine(RepositoryRoot(), "fixtures", "demo", "uploads", "tenant-a", DemoPdfs.CreditNoteFileName);

    /// <summary>The Nordwind price list the P2 demo uploads (docs/09 "Demo: manual mapping"; not ingested by the demo data).</summary>
    internal static string PriceListPath() => Path.Combine(RepositoryRoot(), "fixtures", "demo", "uploads", "tenant-a", DemoPriceList.FileName);

    /// <summary>What <c>POST /document-types {from: template, mode: adopt}</c> does (configurableTypes is already on).</summary>
    private static async Task AdoptAsync(IHost host, Guid tenantId, string template)
    {
        await using var db = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped();
        var catalogue = new DocumentTypeCatalogue(db, TimeProvider.System);
        var adopted = await catalogue.AdoptAsync(tenantId, template, null, "tenant-a-admin", Ct);
        adopted.Status.ShouldBe(CatalogueStatus.Created, adopted.Detail);
    }

    /// <summary>
    /// What <c>POST /document-types {from: template, copy}</c> (in place, under the adopted legacy key), the draft
    /// <c>PUT</c> with the field appended and <c>:publish</c> do; switches <c>configurableTypes</c> on first.
    /// </summary>
    private static async Task ForkWithFieldAsync(IHost host, Guid tenantId, string template, string key, string fieldPath)
    {
        await using var db = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped();
        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId, Ct);
        tenant.Settings.Features!.ConfigurableTypes = true;
        await db.SaveChangesAsync(Ct);

        var catalogue = new DocumentTypeCatalogue(db, TimeProvider.System);
        var copied = await catalogue.CopyAsync(tenantId, template, null, "tenant-a-admin", Ct);
        copied.Status.ShouldBe(CatalogueStatus.Ok, copied.Detail);
        copied.Value!.Definition.Key.ShouldBe(key);
        var draft = copied.Value!.Versions.Single(v => v.State == DocumentTypeVersionState.Draft);
        var definition = JsonNode.Parse(draft.DefinitionJson.ShouldNotBeNull())!.AsObject();
        definition["fields"]!.AsArray().Add(JsonNode.Parse(await File.ReadAllTextAsync(fieldPath, Ct)));
        var saved = await catalogue.SaveDraftAsync(tenantId, key, draft.Version, definition.ToJsonString(), draft.Revision, "tenant-a-admin", Ct);
        saved.Status.ShouldBe(CatalogueStatus.Ok, saved.Detail);
        var published = await catalogue.PublishAsync(tenantId, key, draft.Version, null, "Customer number at supplier", "tenant-a-admin", Ct);
        published.Status.ShouldBe(CatalogueStatus.Ok, published.Detail);
    }

    /// <summary>What <c>POST /ingestions</c> with one file does (the same <see cref="UploadIntake"/>); answers the new document's id.</summary>
    private static async Task<Guid> UploadAsync(IHost host, Guid tenantId, string path)
    {
        await using var db = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped();
        var channel = await db.IngestionChannels.AsNoTracking().Where(c => c.TenantId == tenantId && c.Type == ChannelType.ApiUpload).OrderBy(c => c.CreatedAt).FirstAsync(Ct);
        var bytes = await File.ReadAllBytesAsync(path, Ct);
        var upload = new UploadFile(Path.GetFileName(path), "application/pdf", ItemKinds.Detect("application/pdf", Path.GetFileName(path)), () => new MemoryStream(bytes, writable: false));
        var blobs = host.Services.GetRequiredService<IBlobStore>();
        var stored = await UploadIntake.StoreAsync(blobs, tenantId, [upload], Ct);
        var ingestion = UploadIntake.CreateIngestion(tenantId, channel.Id, SourceMetadata.Upload("tenant-a-admin@gasthaus-zur-post.example"), stored, DateTimeOffset.UtcNow);
        db.Ingestions.Add(ingestion);
        await db.SaveChangesAsync(Ct);
        await host.Services.GetRequiredService<InProcessPipelineQueue>().EnqueueAsync(new ProcessIngestion(tenantId, ingestion.Id), Ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        await host.Services.GetRequiredService<InProcessPipelineQueue>().WaitForIdleAsync(cts.Token);
        await using var read = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped();
        return await read.Documents.AsNoTracking().Where(d => d.IngestionItem!.IngestionId == ingestion.Id).Select(d => d.Id).SingleAsync(Ct);
    }

    /// <summary>What <c>POST /documents/{id}:reprocess {fromStage: Extract, configMode: current}</c> does.</summary>
    private static async Task ReprocessFromExtractWithCurrentConfigAsync(IHost host, Document invoice)
    {
        await using (var db = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped())
        {
            var document = await db.Documents.SingleAsync(d => d.Id == invoice.Id, Ct);
            document.Status = DocumentStatus.Matched;
            document.DocumentTypeVersionId = null;
            foreach (var task in await db.ReviewTasks.Where(t => t.DocumentId == invoice.Id && t.Status == ReviewTaskStatus.Open).ToListAsync(Ct))
            {
                task.Status = ReviewTaskStatus.Skipped;
            }

            await db.SaveChangesAsync(Ct);
        }

        await host.Services.GetRequiredService<InProcessPipelineQueue>().EnqueueAsync(new ProcessDocument(invoice.TenantId, invoice.Id, PipelineStage.Extract), Ct);
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

    /// <summary>
    /// A classification or split fixture: its request carries rendered pages as PNGs (the first page, the page
    /// thumbnails), whose bytes depend on the PDFium build.
    /// </summary>
    private static bool IsRenderedPageFixture(string content) =>
        JsonNode.Parse(content)?["header"]?["stage"]?.GetValue<string>() is nameof(LlmStage.Classify) or nameof(LlmStage.Split);

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
