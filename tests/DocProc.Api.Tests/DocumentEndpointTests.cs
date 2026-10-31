using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Api.Endpoints;
using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Api.Tests;

public sealed class DocumentEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly string TenantA = ApiFactory.TenantA.ToString();
    private static readonly string TenantB = ApiFactory.TenantB.ToString();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient ReviewerA(string name = "reviewer-a") => factory.ClientFor(new TokenSpec(name, [Roles.Reviewer], TenantA));

    private static Uri Documents(string query = "") => new("/api/v1/documents" + query, UriKind.Relative);

    private static Uri Document(Guid id, string suffix = "") => new($"/api/v1/documents/{id}{suffix}", UriKind.Relative);

    [Fact]
    public async Task List_filters_by_status_type_supplier_flag_issue_date_and_text()
    {
        // A tenant of its own so other tests' documents do not interfere with the counts.
        var tenant = Guid.NewGuid();
        factory.Query(db =>
        {
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Filters", Slug = "filters-" + tenant.ToString("N")[..8], LegalName = "Filters GmbH", DefaultCurrency = "EUR", CountryCode = "DE", TimeZone = "Europe/Berlin", CreatedAt = factory.Clock.GetUtcNow(), UpdatedAt = factory.Clock.GetUtcNow() });
            return db.SaveChanges();
        });

        var metroReview = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, Status = DocumentStatus.NeedsReview, Invoice = TestInvoices.Valid("RE-1001", new DateOnly(2026, 10, 1)), SupplierName = "METRO Deutschland GmbH", Flags = [FlagCodes.Create(FlagCodes.TotalsMismatch, "sum off", "totalGross"), FlagCodes.Create(FlagCodes.LowConfidence, "low", "lines[1].quantity")] });
        var metroApproved = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, Status = DocumentStatus.Approved, Invoice = TestInvoices.Valid("RE-1002", new DateOnly(2026, 10, 15)), SupplierName = "METRO Deutschland GmbH" });
        var hoffmannCredit = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, Status = DocumentStatus.NeedsReview, Type = DocumentType.CreditNote, Invoice = TestInvoices.Valid("GS-77", new DateOnly(2026, 9, 20)), SupplierName = "Getränke Hoffmann GmbH" });
        var failed = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, Status = DocumentStatus.Failed, Invoice = null, SupplierName = null });

        using var client = factory.ClientFor(new TokenSpec("filter-reviewer", [Roles.Reviewer], tenant.ToString()));

        async Task<List<Guid>> Ids(string query) => (await client.GetFromJsonAsync<Page<DocumentSummary>>(Documents(query), ApiFactory.Json, Ct))!.Items.Select(d => d.Id).ToList();

        (await Ids(string.Empty)).ShouldBe([failed.DocumentId, hoffmannCredit.DocumentId, metroApproved.DocumentId, metroReview.DocumentId], "newest first");
        (await Ids("?status=NeedsReview")).ShouldBe([hoffmannCredit.DocumentId, metroReview.DocumentId]);
        (await Ids("?status=needsreview,approved")).Count.ShouldBe(3);
        (await Ids("?type=CreditNote")).ShouldBe([hoffmannCredit.DocumentId]);
        (await Ids("?flag=TOTALS_MISMATCH")).ShouldBe([metroReview.DocumentId]);
        (await Ids("?flag=totals_mismatch")).ShouldBe([metroReview.DocumentId]);
        (await Ids("?from=2026-10-01&to=2026-10-10")).ShouldBe([metroReview.DocumentId]);
        (await Ids("?from=2026-10-02")).ShouldBe([metroApproved.DocumentId]);
        (await Ids("?q=re-100")).ShouldBe([metroApproved.DocumentId, metroReview.DocumentId], "document number, case-insensitive");
        (await Ids("?q=hoffmann")).ShouldBe([hoffmannCredit.DocumentId], "supplier name");
        (await Ids("?q=%25")).ShouldBeEmpty("LIKE wildcards are escaped");

        var metroId = factory.Query(db => db.Suppliers.Single(s => s.TenantId == tenant && s.Name == "METRO Deutschland GmbH").Id);
        (await Ids($"?supplierId={metroId}")).ShouldBe([metroApproved.DocumentId, metroReview.DocumentId]);

        var summary = (await client.GetFromJsonAsync<Page<DocumentSummary>>(Documents("?status=NeedsReview&type=Invoice"), ApiFactory.Json, Ct))!.Items.ShouldHaveSingleItem();
        summary.Id.ShouldBe(metroReview.DocumentId);
        summary.SupplierName.ShouldBe("METRO Deutschland GmbH");
        summary.DocumentNumber.ShouldBe("RE-1001");
        summary.IssueDate.ShouldBe(new DateOnly(2026, 10, 1));
        summary.Currency.ShouldBe("EUR");
        summary.TotalGross.ShouldBe(166.50m);
        summary.ExtractionMethod.ShouldBe(ExtractionMethod.Llm);
        summary.BlockingFlagCount.ShouldBe(1);
        summary.WarningFlagCount.ShouldBe(1);
        summary.PageCount.ShouldBe(1);

        (await client.GetAsync(Documents("?status=Bogus"), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync(Documents("?from=2026-10-10&to=2026-10-01"), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_pages_with_an_opaque_cursor_newest_first()
    {
        var tenant = Guid.NewGuid();
        factory.Query(db =>
        {
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Paging", Slug = "paging-" + tenant.ToString("N")[..8], LegalName = "Paging GmbH", DefaultCurrency = "EUR", CountryCode = "DE", TimeZone = "Europe/Berlin", CreatedAt = factory.Clock.GetUtcNow(), UpdatedAt = factory.Clock.GetUtcNow() });
            return db.SaveChanges();
        });

        var created = new List<Guid>();
        var baseTime = factory.Clock.GetUtcNow();
        for (var i = 0; i < 5; i++)
        {
            // Two documents share a timestamp, so the id tie-break is exercised too.
            var seeded = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, CreatedAt = baseTime.AddMinutes(i / 2), Invoice = TestInvoices.Valid($"P-{i}") });
            created.Add(seeded.DocumentId);
        }

        using var client = factory.ClientFor(new TokenSpec("pager", [Roles.Viewer], tenant.ToString()));
        var pages = new List<List<Guid>>();
        string? cursor = null;
        do
        {
            var page = (await client.GetFromJsonAsync<Page<DocumentSummary>>(Documents($"?limit=2&cursor={cursor}"), ApiFactory.Json, Ct))!;
            pages.Add(page.Items.Select(d => d.Id).ToList());
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        pages.Select(p => p.Count).ShouldBe([2, 2, 1]);
        var all = pages.SelectMany(p => p).ToList();
        all.ShouldBeUnique();
        all.ShouldBe(created.AsEnumerable().Reverse(), "newest first, ids descending within a timestamp");

        (await client.GetFromJsonAsync<Page<DocumentSummary>>(Documents("?limit=999"), ApiFactory.Json, Ct))!.Items.Count.ShouldBe(5, "limit is clamped, not rejected");
        (await client.GetAsync(Documents("?cursor=not-a-cursor"), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Detail_flags_carry_their_parameters_and_an_empty_map_without_any()
    {
        var withParams = FlagCodes.Create(
            FlagCodes.LowConfidence,
            "'dueDate' has confidence 0.50, below the threshold 0.80.",
            "dueDate",
            FlagParams.Of(("field", "dueDate"), ("confidence", "0.50"), ("threshold", "0.80")));
        var withoutParams = FlagCodes.Create(FlagCodes.SupplierUnmatched, "No supplier of the tenant matched the document; a new supplier may be needed.");
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { Flags = [withParams, withoutParams], ReviewTask = true });
        using var reviewer = ReviewerA();

        var json = JsonDocument.Parse(await (await reviewer.GetAsync(Document(seeded.DocumentId), Ct)).Content.ReadAsStringAsync(Ct)).RootElement;

        var flags = json.GetProperty("flags").EnumerateArray().ToDictionary(f => f.GetProperty("code").GetString()!);
        var low = flags["LOW_CONFIDENCE"];
        low.GetProperty("message").GetString().ShouldBe("'dueDate' has confidence 0.50, below the threshold 0.80.");
        var parameters = low.GetProperty("params");
        parameters.GetProperty("field").GetString().ShouldBe("dueDate");
        parameters.GetProperty("confidence").GetString().ShouldBe("0.50");
        parameters.GetProperty("threshold").GetString().ShouldBe("0.80");
        flags["SUPPLIER_UNMATCHED"].GetProperty("params").ValueKind.ShouldBe(JsonValueKind.Object);
        flags["SUPPLIER_UNMATCHED"].GetProperty("params").EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    public async Task Detail_returns_the_canonical_model_with_provenance_flags_costs_and_origin()
    {
        var png = TestInvoices.FakePng();
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { Flags = [FlagCodes.Create(FlagCodes.LowConfidence, "quantity read at 0.72", "lines[1].quantity")], PagePng = png, ReviewTask = true });
        using var reviewer = ReviewerA();

        var response = await reviewer.GetAsync(Document(seeded.DocumentId), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        json.GetProperty("status").GetString().ShouldBe("NeedsReview");
        json.GetProperty("type").GetString().ShouldBe("Invoice");
        json.GetProperty("supplierName").GetString().ShouldBe("METRO Deutschland GmbH");
        json.GetProperty("blockingFlagCount").GetInt32().ShouldBe(0);
        json.GetProperty("warningFlagCount").GetInt32().ShouldBe(1);

        // The canonical JSON is passed through verbatim: provenance, confidence and history are the stored ones.
        var canonical = json.GetProperty("canonical");
        canonical.GetRawText().ShouldBe(seeded.CanonicalJson);
        var number = canonical.GetProperty("invoiceNumber");
        number.GetProperty("value").GetProperty("verbatim").GetString().ShouldBe("RE-2026-0042");
        number.GetProperty("status").GetString().ShouldBe("Extracted");
        number.GetProperty("confidence").GetDouble().ShouldBe(0.98);
        number.GetProperty("source").GetProperty("method").GetString().ShouldBe("Llm");
        number.GetProperty("source").GetProperty("page").GetInt32().ShouldBe(1);
        number.GetProperty("source").GetProperty("bBox").GetProperty("x0").GetDouble().ShouldBe(100);
        canonical.GetProperty("lines")[1].GetProperty("quantity").GetProperty("confidence").GetDouble().ShouldBe(0.72);

        var flag = json.GetProperty("flags").EnumerateArray().ShouldHaveSingleItem();
        flag.GetProperty("code").GetString().ShouldBe("LOW_CONFIDENCE");
        flag.GetProperty("severity").GetString().ShouldBe("Warning");
        flag.GetProperty("field").GetString().ShouldBe("lines[1].quantity");

        json.GetProperty("costs").GetProperty("llmInputTokens").GetInt32().ShouldBe(1200);
        json.GetProperty("costs").GetProperty("keyOwner").GetString().ShouldBe("Platform");

        var ingestion = json.GetProperty("ingestion");
        ingestion.GetProperty("ingestionId").GetGuid().ShouldBe(seeded.IngestionId);
        ingestion.GetProperty("itemId").GetGuid().ShouldBe(seeded.ItemId);
        ingestion.GetProperty("fileName").GetString().ShouldBe("invoice.pdf");
        ingestion.GetProperty("sourceFrom").GetString().ShouldBe("scanner@example.test");

        json.GetProperty("pageImageUrls").EnumerateArray().Select(u => u.GetString()).ShouldBe([$"/api/v1/documents/{seeded.DocumentId}/pages/1.png"]);

        // The typed client model round-trips too.
        var detail = (await reviewer.GetFromJsonAsync<DocumentDetail>(Document(seeded.DocumentId), ApiFactory.Json, Ct))!;
        detail.Canonical!.Value.GetProperty("currency").GetProperty("value").GetString().ShouldBe("EUR");
        detail.Flags.Count.ShouldBe(1);

        using var tenantB = factory.ClientFor(new TokenSpec("reviewer-b", [Roles.Reviewer], TenantB));
        (await tenantB.GetAsync(Document(seeded.DocumentId), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await tenantB.GetFromJsonAsync<Page<DocumentSummary>>(Documents(), ApiFactory.Json, Ct))!.Items.ShouldNotContain(d => d.Id == seeded.DocumentId);

        using var operatorClient = factory.ClientFor(new TokenSpec("ops", [Roles.Operator]));
        (await operatorClient.GetFromJsonAsync<Page<DocumentSummary>>(Documents(), ApiFactory.Json, Ct))!.Items.ShouldContain(d => d.Id == seeded.DocumentId, "platform scope lists every tenant");
    }

    [Fact]
    public async Task Summaries_name_the_owning_tenant_in_platform_and_tenant_scope()
    {
        var north = Guid.NewGuid();
        var south = Guid.NewGuid();
        factory.Query(db =>
        {
            foreach (var (id, name) in new[] { (north, "Tenant North"), (south, "Tenant South") })
            {
                db.Tenants.Add(new Tenant { Id = id, Name = name, Slug = "names-" + id.ToString("N")[..8], LegalName = name + " GmbH", DefaultCurrency = "EUR", CountryCode = "DE", TimeZone = "Europe/Berlin", CreatedAt = factory.Clock.GetUtcNow(), UpdatedAt = factory.Clock.GetUtcNow() });
            }

            return db.SaveChanges();
        });

        // Newest of all, so the platform list's first page holds them whatever other tests seeded.
        var at = factory.Clock.GetUtcNow().AddYears(5);
        var northDoc = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = north, CreatedAt = at, Invoice = TestInvoices.Valid("N-1") });
        var southDoc = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = south, CreatedAt = at.AddSeconds(1), Invoice = TestInvoices.Valid("S-1") });

        using var operatorClient = factory.ClientFor(new TokenSpec("ops", [Roles.Operator]));
        var platform = (await operatorClient.GetFromJsonAsync<Page<DocumentSummary>>(Documents("?limit=10"), ApiFactory.Json, Ct))!.Items;
        var northRow = platform.Where(d => d.Id == northDoc.DocumentId).ShouldHaveSingleItem();
        northRow.TenantId.ShouldBe(north);
        northRow.TenantName.ShouldBe("Tenant North");
        var southRow = platform.Where(d => d.Id == southDoc.DocumentId).ShouldHaveSingleItem();
        southRow.TenantId.ShouldBe(south);
        southRow.TenantName.ShouldBe("Tenant South");

        var json = JsonDocument.Parse(await operatorClient.GetStringAsync(Documents("?limit=1"), Ct)).RootElement.GetProperty("items")[0];
        json.GetProperty("tenantId").GetString().ShouldBe(south.ToString());
        json.GetProperty("tenantName").GetString().ShouldBe("Tenant South");

        // The detail names its tenant too: a platform-scope detail page has no other way to say whose document it is.
        var detail = (await operatorClient.GetFromJsonAsync<DocumentDetail>(Document(northDoc.DocumentId), ApiFactory.Json, Ct))!;
        detail.TenantId.ShouldBe(north);
        detail.TenantName.ShouldBe("Tenant North");

        using var northViewer = factory.ClientFor(new TokenSpec("north-viewer", [Roles.Viewer], north.ToString()));
        var own = (await northViewer.GetFromJsonAsync<Page<DocumentSummary>>(Documents(), ApiFactory.Json, Ct))!.Items.ShouldHaveSingleItem();
        own.Id.ShouldBe(northDoc.DocumentId);
        own.TenantId.ShouldBe(north);
        own.TenantName.ShouldBe("Tenant North", "a tenant sees its own tenant row, so the name is there in tenant scope too");
    }

    [Fact]
    public async Task Page_image_original_and_text_stream_the_stored_bytes()
    {
        var png = TestInvoices.FakePng();
        const string textLayer = "{\"source\":\"PdfText\",\"pages\":[{\"number\":1,\"width\":1000,\"height\":1000,\"words\":[]}]}";
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { PagePng = png, TextLayerJson = textLayer, FileName = "Lieferung März.pdf" });
        using var viewer = factory.ClientFor(new TokenSpec("viewer", [Roles.Viewer], TenantA));

        var page = await viewer.GetAsync(Document(seeded.DocumentId, "/pages/1.png"), Ct);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        page.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        (await page.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(png);
        var etag = page.Headers.ETag!;
        etag.Tag.ShouldBe("\"" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png)) + "\"");

        using var conditional = new HttpRequestMessage(HttpMethod.Get, Document(seeded.DocumentId, "/pages/1.png"));
        conditional.Headers.IfNoneMatch.Add(etag);
        (await viewer.SendAsync(conditional, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotModified);

        (await viewer.GetAsync(Document(seeded.DocumentId, "/pages/2.png"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await viewer.GetAsync(Document(seeded.DocumentId, "/pages/0.png"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var original = await viewer.GetAsync(Document(seeded.DocumentId, "/original"), Ct);
        original.StatusCode.ShouldBe(HttpStatusCode.OK);
        original.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        original.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
        original.Content.Headers.ContentDisposition.FileNameStar.ShouldBe("Lieferung März.pdf");
        (await original.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(seeded.OriginalBytes);

        var text = await viewer.GetAsync(Document(seeded.DocumentId, "/text"), Ct);
        text.StatusCode.ShouldBe(HttpStatusCode.OK);
        text.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        (await text.Content.ReadAsStringAsync(Ct)).ShouldBe(textLayer);

        var bare = await factory.SeedDocumentAsync(new DocumentSeed());
        (await viewer.GetAsync(Document(bare.DocumentId, "/text"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await viewer.GetAsync(Document(bare.DocumentId, "/pages/1.png"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A page whose blob vanished from the derived store is a 404 problem, not a 500.
        var orphan = await factory.SeedDocumentAsync(new DocumentSeed { PagePng = TestInvoices.FakePng() });
        var key = factory.Query(db => db.Documents.AsNoTracking().Single(d => d.Id == orphan.DocumentId).PageImageKeys.Single());
        File.Delete(Path.Combine(factory.BlobRoot, BlobContainers.Derived, key.Replace('/', Path.DirectorySeparatorChar)));
        var missing = await viewer.GetAsync(Document(orphan.DocumentId, "/pages/1.png"), Ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missing.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        using var tenantB = factory.ClientFor(new TokenSpec("reviewer-b", [Roles.Reviewer], TenantB));
        (await tenantB.GetAsync(Document(seeded.DocumentId, "/pages/1.png"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await tenantB.GetAsync(Document(seeded.DocumentId, "/original"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Approve_from_needs_review_closes_the_task_writes_the_event_and_queues_publishing()
    {
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { ReviewTask = true, AssignedTo = "reviewer-a" });
        using var reviewer = ReviewerA();

        var response = await reviewer.PostAsJsonAsync(Document(seeded.DocumentId, ":approve"), new ApproveDocumentRequest("looks right"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = (await response.Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!;
        detail.Status.ShouldBe(DocumentStatus.Approved);
        detail.ApprovedAt.ShouldBe(factory.Clock.GetUtcNow());
        detail.ApprovedBy.ShouldBe("reviewer-a");

        var task = factory.Query(db => db.ReviewTasks.AsNoTracking().Single(t => t.Id == seeded.ReviewTaskId));
        task.Status.ShouldBe(ReviewTaskStatus.Done);
        task.CompletedAt.ShouldBe(factory.Clock.GetUtcNow());

        factory.Queue.Documents.ShouldContain(m => m.DocumentId == seeded.DocumentId && m.Stage == PipelineStage.Publish && m.TenantId == ApiFactory.TenantA);

        var events = (await reviewer.GetFromJsonAsync<List<DocumentEventDto>>(Document(seeded.DocumentId, "/events"), ApiFactory.Json, Ct))!;
        events.Select(e => e.EventType).ShouldBe([DocumentEventTypes.Received, DocumentEventTypes.StageCompleted, DocumentEventTypes.Approved], "ordered by time");
        var approved = events[^1];
        approved.FromStatus.ShouldBe(DocumentStatus.NeedsReview);
        approved.ToStatus.ShouldBe(DocumentStatus.Approved);
        approved.Actor.ShouldBe("reviewer-a");
        approved.Details!.Value.GetProperty("comment").GetString().ShouldBe("looks right");

        // Approving again is an invalid transition; the body is optional.
        var again = await reviewer.PostAsync(Document(seeded.DocumentId, ":approve"), null, Ct);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = JsonDocument.Parse(await again.Content.ReadAsStringAsync(Ct)).RootElement;
        problem.GetProperty("title").GetString().ShouldBe("Invalid transition");
        problem.GetProperty("detail").GetString()!.ShouldContain("Approved");
    }

    [Fact]
    public async Task Approve_bumps_the_version_token_so_a_stale_tracked_writer_fails_its_save()
    {
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { ReviewTask = true });
        using var reviewer = ReviewerA();

        // The pipeline (or PATCH /fields) loaded the document before the reviewer decided it.
        await using var stale = factory.PlatformDb();
        var loadedBefore = await stale.Documents.SingleAsync(d => d.Id == seeded.DocumentId, Ct);
        var versionBefore = loadedBefore.Version;

        (await reviewer.PostAsync(Document(seeded.DocumentId, ":approve"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var stored = factory.Query(db => db.Documents.Single(d => d.Id == seeded.DocumentId));
        stored.Status.ShouldBe(DocumentStatus.Approved);
        stored.Version.ShouldBe(versionBefore + 1, "the conditional update bumps the token exactly once, like a tracked save would");
        factory.Query(db => db.DocumentEvents.Count(e => e.DocumentId == seeded.DocumentId && e.EventType == DocumentEventTypes.Approved)).ShouldBe(1);
        factory.Query(db => db.ReviewTasks.Single(t => t.Id == seeded.ReviewTaskId).Status).ShouldBe(ReviewTaskStatus.Done);

        loadedBefore.FailureReason = "written over the approval";
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync(Ct));
        factory.Query(db => db.Documents.Single(d => d.Id == seeded.DocumentId)).FailureReason.ShouldBeNull("the stale write did not land");
    }

    [Fact]
    public async Task Reject_and_reprocess_bump_the_version_token_too()
    {
        using var reviewer = ReviewerA();

        var rejected = await factory.SeedDocumentAsync(new DocumentSeed());
        var before = factory.Query(db => db.Documents.Single(d => d.Id == rejected.DocumentId).Version);
        (await reviewer.PostAsJsonAsync(Document(rejected.DocumentId, ":reject"), new RejectDocumentRequest("duplicate"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Query(db => db.Documents.Single(d => d.Id == rejected.DocumentId).Version).ShouldBe(before + 1);

        var reprocessed = await factory.SeedDocumentAsync(new DocumentSeed());
        factory.RecordPipelineProgress(reprocessed.DocumentId, DocumentStatus.Validated);
        before = factory.Query(db => db.Documents.Single(d => d.Id == reprocessed.DocumentId).Version);
        (await reviewer.PostAsJsonAsync(Document(reprocessed.DocumentId, ":reprocess"), new ReprocessDocumentRequest("Extract"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Query(db => db.Documents.Single(d => d.Id == reprocessed.DocumentId).Version).ShouldBe(before + 1);
    }

    [Fact]
    public async Task Approve_also_works_from_validated_and_without_a_body()
    {
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { Status = DocumentStatus.Validated });
        using var admin = factory.ClientFor(new TokenSpec("admin-a", [Roles.TenantAdmin], TenantA));

        var response = await admin.PostAsync(Document(seeded.DocumentId, ":approve"), null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!.Status.ShouldBe(DocumentStatus.Approved);
    }

    [Fact]
    public async Task Viewer_cannot_approve_reject_reprocess_or_correct()
    {
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed());
        using var viewer = factory.ClientFor(new TokenSpec("viewer", [Roles.Viewer], TenantA));

        (await viewer.PostAsync(Document(seeded.DocumentId, ":approve"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.PostAsJsonAsync(Document(seeded.DocumentId, ":reject"), new RejectDocumentRequest("no"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.PostAsync(Document(seeded.DocumentId, ":reprocess"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.PatchAsJsonAsync(Document(seeded.DocumentId, "/fields"), new CorrectFieldsRequest([new FieldCorrection("notes", JsonSerializer.SerializeToElement("x"), null)]), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        factory.Query(db => db.Documents.AsNoTracking().Single(d => d.Id == seeded.DocumentId).Status).ShouldBe(DocumentStatus.NeedsReview);
        (await viewer.GetAsync(Document(seeded.DocumentId), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "viewers read");
    }

    [Fact]
    public async Task Reject_requires_a_reason_and_publishes_the_rejection()
    {
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { ReviewTask = true });
        using var reviewer = ReviewerA();

        var missing = await reviewer.PostAsJsonAsync(Document(seeded.DocumentId, ":reject"), new { }, Ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await missing.Content.ReadAsStringAsync(Ct)).ShouldContain("reason");
        (await reviewer.PostAsJsonAsync(Document(seeded.DocumentId, ":reject"), new RejectDocumentRequest("   "), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var response = await reviewer.PostAsJsonAsync(Document(seeded.DocumentId, ":reject"), new RejectDocumentRequest("duplicate of RE-2026-0041"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!.Status.ShouldBe(DocumentStatus.Rejected);
        factory.Query(db => db.ReviewTasks.AsNoTracking().Single(t => t.Id == seeded.ReviewTaskId).Status).ShouldBe(ReviewTaskStatus.Done);
        factory.Queue.Documents.ShouldContain(m => m.DocumentId == seeded.DocumentId && m.Stage == PipelineStage.Publish);
        var rejected = factory.Query(db => db.DocumentEvents.AsNoTracking().Single(e => e.DocumentId == seeded.DocumentId && e.EventType == DocumentEventTypes.Rejected));
        rejected.DetailsJson!.ShouldContain("duplicate of RE-2026-0041");

        (await reviewer.PostAsJsonAsync(Document(seeded.DocumentId, ":reject"), new RejectDocumentRequest("again"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Reprocess_resets_the_status_to_before_the_stage_and_queues_it()
    {
        using var reviewer = ReviewerA();

        // The seed only records the jump to the seeded status; these documents ran through the whole pipeline first.
        var failed = await factory.SeedDocumentAsync(new DocumentSeed { Status = DocumentStatus.Failed, ReviewTask = false });
        factory.RecordPipelineProgress(failed.DocumentId, DocumentStatus.Validated);
        var response = await reviewer.PostAsync(Document(failed.DocumentId, ":reprocess"), null, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = (await response.Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!;
        detail.Status.ShouldBe(DocumentStatus.Matched, "Extract is the default stage; Matched is the state before it");
        detail.FailureReason.ShouldBeNull();
        factory.Queue.Documents.ShouldContain(m => m.DocumentId == failed.DocumentId && m.Stage == PipelineStage.Extract);

        var review = await factory.SeedDocumentAsync(new DocumentSeed { ReviewTask = true });
        factory.RecordPipelineProgress(review.DocumentId, DocumentStatus.Validated);
        var classify = await reviewer.PostAsJsonAsync(Document(review.DocumentId, ":reprocess"), new ReprocessDocumentRequest("classify"), Ct);
        classify.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await classify.Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!.Status.ShouldBe(DocumentStatus.Split);
        factory.Queue.Documents.ShouldContain(m => m.DocumentId == review.DocumentId && m.Stage == PipelineStage.Classify);
        factory.Query(db => db.ReviewTasks.AsNoTracking().Single(t => t.Id == review.ReviewTaskId).Status).ShouldBe(ReviewTaskStatus.Skipped, "the task for the superseded extraction is closed");
        var reprocessed = factory.Query(db => db.DocumentEvents.AsNoTracking().Single(e => e.DocumentId == review.DocumentId && e.EventType == DocumentEventTypes.Reprocessed));
        reprocessed.FromStatus.ShouldBe(DocumentStatus.NeedsReview);
        reprocessed.ToStatus.ShouldBe(DocumentStatus.Split);
        reprocessed.DetailsJson!.ShouldContain("Classify");

        var rejected = await factory.SeedDocumentAsync(new DocumentSeed { Status = DocumentStatus.Rejected });
        factory.RecordPipelineProgress(rejected.DocumentId, DocumentStatus.Validated);
        var validate = await reviewer.PostAsJsonAsync(Document(rejected.DocumentId, ":reprocess"), new ReprocessDocumentRequest("Validate"), Ct);
        (await validate.Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!.Status.ShouldBe(DocumentStatus.Extracted);

        var approved = await factory.SeedDocumentAsync(new DocumentSeed { Status = DocumentStatus.Approved });
        (await reviewer.PostAsync(Document(approved.DocumentId, ":reprocess"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var inFlight = await factory.SeedDocumentAsync(new DocumentSeed { Status = DocumentStatus.Extracted });
        (await reviewer.PostAsync(Document(inFlight.DocumentId, ":reprocess"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var badStage = await reviewer.PostAsJsonAsync(Document(review.DocumentId, ":reprocess"), new ReprocessDocumentRequest("Publish"), Ct);
        badStage.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await badStage.Content.ReadAsStringAsync(Ct)).ShouldContain("fromStage");
    }

    [Fact]
    public async Task Patch_fields_corrects_header_and_line_fields_with_history_and_audit_rows()
    {
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed());
        using var reviewer = ReviewerA();
        factory.Clock.Advance(TimeSpan.FromMinutes(3));
        var now = factory.Clock.GetUtcNow();

        var request = new CorrectFieldsRequest(
        [
            new FieldCorrection("invoiceNumber", JsonSerializer.SerializeToElement("RE-2026-0043"), "digit misread"),
            new FieldCorrection("lines[1].quantity", JsonSerializer.SerializeToElement(6), null),
            new FieldCorrection("supplier.vatId", JsonSerializer.SerializeToElement("DE 811 177 936"), "spacing as printed"),
            new FieldCorrection("totalGross", JsonSerializer.SerializeToElement(176.50), "bare amount takes the document currency"),
        ]);

        var response = await reviewer.PatchAsJsonAsync(Document(seeded.DocumentId, "/fields"), request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        json.GetProperty("documentNumber").GetString().ShouldBe("RE-2026-0043", "denormalised column follows the correction");
        json.GetProperty("totalGross").GetDecimal().ShouldBe(176.50m);

        var canonical = json.GetProperty("canonical");
        var number = canonical.GetProperty("invoiceNumber");
        number.GetProperty("value").GetProperty("verbatim").GetString().ShouldBe("RE-2026-0043");
        number.GetProperty("value").GetProperty("normalized").GetString().ShouldBe("RE-2026-0043");
        number.GetProperty("status").GetString().ShouldBe("Corrected");
        number.GetProperty("confidence").GetDouble().ShouldBe(1);
        number.GetProperty("source").GetProperty("method").GetString().ShouldBe("Human");
        var history = number.GetProperty("history").EnumerateArray().ShouldHaveSingleItem();
        history.GetProperty("value").GetProperty("verbatim").GetString().ShouldBe("RE-2026-0042");
        history.GetProperty("status").GetString().ShouldBe("Extracted");
        history.GetProperty("confidence").GetDouble().ShouldBe(0.98);
        history.GetProperty("source").GetProperty("method").GetString().ShouldBe("Llm");
        history.GetProperty("source").GetProperty("bBox").GetProperty("x0").GetDouble().ShouldBe(100);
        history.GetProperty("by").GetString().ShouldBe("reviewer-a");
        history.GetProperty("at").GetDateTimeOffset().ShouldBe(now);

        var quantity = canonical.GetProperty("lines")[1].GetProperty("quantity");
        quantity.GetProperty("value").GetDecimal().ShouldBe(6);
        quantity.GetProperty("status").GetString().ShouldBe("Corrected");
        quantity.GetProperty("history")[0].GetProperty("value").GetDecimal().ShouldBe(5);
        canonical.GetProperty("lines")[0].GetProperty("quantity").GetProperty("status").GetString().ShouldBe("Extracted", "untouched line is unchanged");
        canonical.GetProperty("lines")[1].GetProperty("description").GetProperty("status").GetString().ShouldBe("Extracted");

        var supplier = canonical.GetProperty("supplier");
        supplier.GetProperty("value").GetProperty("vatId").GetString().ShouldBe("DE 811 177 936");
        supplier.GetProperty("value").GetProperty("name").GetString().ShouldBe("METRO Deutschland GmbH", "the rest of the party is kept");
        supplier.GetProperty("status").GetString().ShouldBe("Corrected");

        var gross = canonical.GetProperty("totalGross");
        gross.GetProperty("value").GetProperty("amount").GetDecimal().ShouldBe(176.50m);
        gross.GetProperty("value").GetProperty("currency").GetString().ShouldBe("EUR");

        var corrections = factory.Query(db => db.Corrections.AsNoTracking().Where(c => c.DocumentId == seeded.DocumentId).OrderBy(c => c.Id).ToList());
        corrections.Select(c => c.FieldPath).ShouldBe(["invoiceNumber", "lines[1].quantity", "supplier.vatId", "totalGross"]);
        corrections[0].OldValueJson!.ShouldContain("RE-2026-0042");
        corrections[0].NewValueJson!.ShouldContain("RE-2026-0043");
        corrections[0].Reason.ShouldBe("digit misread");
        corrections[0].By.ShouldBe("reviewer-a");
        corrections[0].At.ShouldBe(now);
        corrections[1].OldValueJson.ShouldBe("5");
        corrections[1].NewValueJson.ShouldBe("6");
        corrections[1].Reason.ShouldBeNull();

        var corrected = factory.Query(db => db.DocumentEvents.AsNoTracking().Single(e => e.DocumentId == seeded.DocumentId && e.EventType == DocumentEventTypes.Corrected));
        corrected.DetailsJson!.ShouldContain("lines[1].quantity");

        // A second round stacks history.
        var second = await reviewer.PatchAsJsonAsync(Document(seeded.DocumentId, "/fields"), new CorrectFieldsRequest([new FieldCorrection("invoiceNumber", JsonSerializer.SerializeToElement("RE-2026-0044"), null)]), Ct);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync(Ct)).RootElement;
        secondJson.GetProperty("canonical").GetProperty("invoiceNumber").GetProperty("history").GetArrayLength().ShouldBe(2);

        // The stored invoice still deserialises into the domain model.
        var stored = factory.Query(db => db.Documents.AsNoTracking().Single(d => d.Id == seeded.DocumentId));
        var invoice = CanonicalDocumentSerializer.DeserializeInvoice(stored.CanonicalJson!);
        invoice.InvoiceNumber.Value!.Verbatim.ShouldBe("RE-2026-0044");
        invoice.InvoiceNumber.History.Count.ShouldBe(2);
        invoice.Lines[1].Quantity.Value.ShouldBe(6m);
        stored.DocumentNumberNormalized.ShouldBe("RE-2026-0044");
    }

    [Fact]
    public async Task Patch_fields_rejects_unknown_paths_bad_values_and_settled_documents()
    {
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed());
        using var reviewer = ReviewerA();

        async Task<HttpResponseMessage> Patch(Guid id, params FieldCorrection[] corrections) =>
            await reviewer.PatchAsJsonAsync(Document(id, "/fields"), new CorrectFieldsRequest(corrections), Ct);

        var unknown = await Patch(seeded.DocumentId, new FieldCorrection("invoiceNumbr", JsonSerializer.SerializeToElement("x"), null));
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync(Ct)).RootElement;
        problem.GetProperty("errors").GetProperty("invoiceNumbr")[0].GetString()!.ShouldContain("not a field");

        (await Patch(seeded.DocumentId, new FieldCorrection("lines[7].quantity", JsonSerializer.SerializeToElement(1), null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Patch(seeded.DocumentId, new FieldCorrection("lines.quantity", JsonSerializer.SerializeToElement(1), null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Patch(seeded.DocumentId, new FieldCorrection("lines[0].unitRaw", JsonSerializer.SerializeToElement("kg"), null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "not a provenance-carrying field");
        (await Patch(seeded.DocumentId, new FieldCorrection("issueDate", JsonSerializer.SerializeToElement("yesterday"), null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Patch(seeded.DocumentId, new FieldCorrection("lines[0].quantity", null, null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "a decimal cannot be cleared");
        (await Patch(seeded.DocumentId)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "no corrections");

        // Nothing partial was applied.
        factory.Query(db => db.Corrections.Count(c => c.DocumentId == seeded.DocumentId)).ShouldBe(0);
        factory.Query(db => db.Documents.AsNoTracking().Single(d => d.Id == seeded.DocumentId).CanonicalJson).ShouldBe(seeded.CanonicalJson);

        var approved = await factory.SeedDocumentAsync(new DocumentSeed { Status = DocumentStatus.Approved });
        (await Patch(approved.DocumentId, new FieldCorrection("notes", JsonSerializer.SerializeToElement("x"), null))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var rejected = await factory.SeedDocumentAsync(new DocumentSeed { Status = DocumentStatus.Rejected });
        (await Patch(rejected.DocumentId, new FieldCorrection("notes", JsonSerializer.SerializeToElement("x"), null))).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var noCanonical = await factory.SeedDocumentAsync(new DocumentSeed { Status = DocumentStatus.Failed, Invoice = null });
        (await Patch(noCanonical.DocumentId, new FieldCorrection("notes", JsonSerializer.SerializeToElement("x"), null))).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await Patch(Guid.NewGuid(), new FieldCorrection("notes", JsonSerializer.SerializeToElement("x"), null))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reprocess_refuses_a_stage_the_document_never_reached()
    {
        using var reviewer = ReviewerA();

        // Failed straight after Received (the seed records Received → Failed only): Extract would run with no text layer.
        var earlyFailure = await factory.SeedDocumentAsync(new DocumentSeed { Status = DocumentStatus.Failed, Invoice = null });
        var refused = await reviewer.PostAsync(Document(earlyFailure.DocumentId, ":reprocess"), null, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct)).RootElement;
        problem.GetProperty("title").GetString().ShouldBe("Stage not reached");
        problem.GetProperty("detail").GetString()!.ShouldContain("Normalize", customMessage: "the latest stage it may rerun from is named");
        factory.Query(db => db.Documents.AsNoTracking().Single(d => d.Id == earlyFailure.DocumentId).Status).ShouldBe(DocumentStatus.Failed, "nothing changed");
        factory.Queue.Documents.ShouldNotContain(m => m.DocumentId == earlyFailure.DocumentId);

        var normalize = await reviewer.PostAsJsonAsync(Document(earlyFailure.DocumentId, ":reprocess"), new ReprocessDocumentRequest("Normalize"), Ct);
        normalize.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await normalize.Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!.Status.ShouldBe(DocumentStatus.Received);
        factory.Queue.Documents.ShouldContain(m => m.DocumentId == earlyFailure.DocumentId && m.Stage == PipelineStage.Normalize);

        // Failed after classification: Classify may rerun, Extract and Validate may not.
        var classified = await factory.SeedDocumentAsync(new DocumentSeed { Status = DocumentStatus.Failed, Invoice = null });
        factory.RecordPipelineProgress(classified.DocumentId, DocumentStatus.Classified);
        (await reviewer.PostAsJsonAsync(Document(classified.DocumentId, ":reprocess"), new ReprocessDocumentRequest("Validate"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await reviewer.PostAsJsonAsync(Document(classified.DocumentId, ":reprocess"), new ReprocessDocumentRequest("Extract"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var classify = await reviewer.PostAsJsonAsync(Document(classified.DocumentId, ":reprocess"), new ReprocessDocumentRequest("Classify"), Ct);
        classify.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await classify.Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!.Status.ShouldBe(DocumentStatus.Split);
    }

    [Fact]
    public async Task Patch_fields_is_refused_while_the_pipeline_is_mid_flight_and_allowed_on_failed_documents()
    {
        using var reviewer = ReviewerA();
        var correction = new CorrectFieldsRequest([new FieldCorrection("notes", JsonSerializer.SerializeToElement("checked"), null)]);

        foreach (var inFlight in new[] { DocumentStatus.Classified, DocumentStatus.Matched, DocumentStatus.Extracted })
        {
            var seeded = await factory.SeedDocumentAsync(new DocumentSeed { Status = inFlight });
            var response = await reviewer.PatchAsJsonAsync(Document(seeded.DocumentId, "/fields"), correction, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict, inFlight.ToString());
            var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
            problem.GetProperty("title").GetString().ShouldBe("Document not correctable");
            problem.GetProperty("detail").GetString()!.ShouldContain("pipeline");
            factory.Query(db => db.Corrections.Count(c => c.DocumentId == seeded.DocumentId)).ShouldBe(0);
        }

        var failed = await factory.SeedDocumentAsync(new DocumentSeed { Status = DocumentStatus.Failed });
        var corrected = await reviewer.PatchAsJsonAsync(Document(failed.DocumentId, "/fields"), correction, Ct);
        corrected.StatusCode.ShouldBe(HttpStatusCode.OK, "a failed document keeps its canonical data and may be fixed by hand");
        (await corrected.Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!.Status.ShouldBe(DocumentStatus.Failed);

        var validated = await factory.SeedDocumentAsync(new DocumentSeed { Status = DocumentStatus.Validated });
        (await reviewer.PatchAsJsonAsync(Document(validated.DocumentId, "/fields"), correction, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Patch_fields_validates_currency_codes_before_touching_the_document()
    {
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed());
        using var reviewer = ReviewerA();

        async Task<HttpResponseMessage> Patch(params FieldCorrection[] corrections) =>
            await reviewer.PatchAsJsonAsync(Document(seeded.DocumentId, "/fields"), new CorrectFieldsRequest(corrections), Ct);

        var spelledOut = await Patch(new FieldCorrection("currency", JsonSerializer.SerializeToElement("EURO"), null));
        spelledOut.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "Document.Currency is char(3); the UPDATE must never be the place this fails");
        var problem = JsonDocument.Parse(await spelledOut.Content.ReadAsStringAsync(Ct)).RootElement;
        problem.GetProperty("errors").GetProperty("currency")[0].GetString()!.ShouldContain("ISO 4217");

        (await Patch(new FieldCorrection("totalGross", JsonSerializer.SerializeToElement(new { amount = 10, currency = "EURO" }), null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Patch(new FieldCorrection("totalGross.currency", JsonSerializer.SerializeToElement("EURO"), null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Patch(new FieldCorrection("lines[0].unitPrice", JsonSerializer.SerializeToElement(new { amount = 10, currency = "" }), null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        factory.Query(db => db.Corrections.Count(c => c.DocumentId == seeded.DocumentId)).ShouldBe(0);
        factory.Query(db => db.Documents.AsNoTracking().Single(d => d.Id == seeded.DocumentId).Currency).ShouldBe("EUR");

        // A lower-case code is normalised, not refused, and reaches the list column in its stored form.
        var lowerCase = await Patch(new FieldCorrection("currency", JsonSerializer.SerializeToElement("usd"), "paid in dollars"));
        lowerCase.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await lowerCase.Content.ReadAsStringAsync(Ct)).RootElement;
        json.GetProperty("currency").GetString().ShouldBe("USD");
        json.GetProperty("canonical").GetProperty("currency").GetProperty("value").GetString().ShouldBe("USD");
        factory.Query(db => db.Documents.AsNoTracking().Single(d => d.Id == seeded.DocumentId).Currency).ShouldBe("USD");
    }

    [Fact]
    public async Task Platform_scope_writes_carry_the_documents_tenant()
    {
        // docs/05: operators work the global review queue without X-Tenant-Id. Every row they write must still belong
        // to the document's tenant, or the tenant would never see its own corrections and timeline.
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = ApiFactory.TenantB, ReviewTask = true });
        using var operatorClient = factory.ClientFor(new TokenSpec("ops", [Roles.Operator]));

        (await operatorClient.PostAsync(new Uri($"/api/v1/review-tasks/{seeded.ReviewTaskId}:claim", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var patched = await operatorClient.PatchAsJsonAsync(Document(seeded.DocumentId, "/fields"), new CorrectFieldsRequest([new FieldCorrection("invoiceNumber", JsonSerializer.SerializeToElement("RE-OPS-1"), "fixed by operator")]), Ct);
        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await operatorClient.PostAsJsonAsync(Document(seeded.DocumentId, ":approve"), new ApproveDocumentRequest("ok"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var task = factory.Query(db => db.ReviewTasks.AsNoTracking().Single(t => t.Id == seeded.ReviewTaskId));
        task.TenantId.ShouldBe(ApiFactory.TenantB);
        task.AssignedTo.ShouldBe("ops");
        task.Status.ShouldBe(ReviewTaskStatus.Done);

        var corrections = factory.Query(db => db.Corrections.AsNoTracking().Where(c => c.DocumentId == seeded.DocumentId).ToList());
        corrections.ShouldHaveSingleItem().TenantId.ShouldBe(ApiFactory.TenantB);

        var events = factory.Query(db => db.DocumentEvents.AsNoTracking().Where(e => e.DocumentId == seeded.DocumentId && e.Actor == "ops").ToList());
        events.Select(e => e.EventType).ShouldBe([DocumentEventTypes.Corrected, DocumentEventTypes.Approved], ignoreOrder: true);
        events.ShouldAllBe(e => e.TenantId == ApiFactory.TenantB);
        factory.Queue.Documents.ShouldContain(m => m.DocumentId == seeded.DocumentId && m.Stage == PipelineStage.Publish && m.TenantId == ApiFactory.TenantB);

        // Tenant B sees everything the operator did through its own scope.
        using var tenantB = factory.ClientFor(new TokenSpec("reviewer-b", [Roles.Reviewer], TenantB));
        var detail = (await tenantB.GetFromJsonAsync<DocumentDetail>(Document(seeded.DocumentId), ApiFactory.Json, Ct))!;
        detail.Status.ShouldBe(DocumentStatus.Approved);
        detail.DocumentNumber.ShouldBe("RE-OPS-1");
    }

    [Fact]
    public async Task Original_is_inline_only_for_pdf_and_raster_images_and_never_sniffed()
    {
        using var viewer = factory.ClientFor(new TokenSpec("viewer", [Roles.Viewer], TenantA));

        static string? NoSniff(HttpResponseMessage response) =>
            response.Headers.TryGetValues("X-Content-Type-Options", out var values) ? values.Single() : null;

        var html = await factory.SeedDocumentAsync(new DocumentSeed { FileName = "invoice.html", ContentType = "text/html", OriginalBytes = "<html><script>alert(document.cookie)</script></html>"u8.ToArray() });
        var htmlResponse = await viewer.GetAsync(Document(html.DocumentId, "/original"), Ct);
        htmlResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        htmlResponse.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream", "the uploader's type is not trusted for anything the viewer does not render");
        htmlResponse.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        htmlResponse.Content.Headers.ContentDisposition.FileNameStar.ShouldBe("invoice.html");
        NoSniff(htmlResponse).ShouldBe("nosniff");
        (await htmlResponse.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(html.OriginalBytes);

        var svg = await factory.SeedDocumentAsync(new DocumentSeed { FileName = "logo.svg", ContentType = "image/svg+xml", OriginalBytes = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>1</script></svg>"u8.ToArray() });
        var svgResponse = await viewer.GetAsync(Document(svg.DocumentId, "/original"), Ct);
        svgResponse.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream", "SVG can carry script");
        svgResponse.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");

        // A parameter or odd casing on the stored type must not slip SVG past the image/* test.
        foreach (var disguised in new[] { "image/svg+xml; charset=utf-8", "IMAGE/SVG+XML", " image/svg+xml ;q=1" })
        {
            var seeded = await factory.SeedDocumentAsync(new DocumentSeed { FileName = "logo.svg", ContentType = disguised, OriginalBytes = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>2</script></svg>"u8.ToArray() });
            var response = await viewer.GetAsync(Document(seeded.DocumentId, "/original"), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, disguised);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream", disguised);
            response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment", disguised);
            NoSniff(response).ShouldBe("nosniff", disguised);
        }

        // A stored type that merely starts like a raster image is not one: a browser reads "image/png,text/html" as its
        // last entry and would run the HTML on this origin; an image type the viewer does not render downloads too.
        foreach (var smuggled in new[] { "image/png,text/html", "image/png, text/html", "image/x-anything", "image/heic" })
        {
            var seeded = await factory.SeedDocumentAsync(new DocumentSeed { FileName = "x.png", ContentType = smuggled, OriginalBytes = "<html><script>document.title='XSS'</script></html>"u8.ToArray() });
            var response = await viewer.GetAsync(Document(seeded.DocumentId, "/original"), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, smuggled);
            response.Content.Headers.NonValidated["Content-Type"].ToString().ShouldBe("application/octet-stream", smuggled);
            response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment", smuggled);
            NoSniff(response).ShouldBe("nosniff", smuggled);
        }

        // Inline types are served as the allow-list's constant, aliases included.
        var aliased = await factory.SeedDocumentAsync(new DocumentSeed { FileName = "scan3.jpg", ContentType = "image/jpg", OriginalBytes = [0xFF, 0xD8, 0xFF, 0xE0, 7, 8, 9] });
        var aliasedResponse = await viewer.GetAsync(Document(aliased.DocumentId, "/original"), Ct);
        aliasedResponse.Content.Headers.NonValidated["Content-Type"].ToString().ShouldBe("image/jpeg");
        aliasedResponse.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");

        var jpeg = await factory.SeedDocumentAsync(new DocumentSeed { FileName = "scan.jpg", ContentType = "image/jpeg", OriginalBytes = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3] });
        var jpegResponse = await viewer.GetAsync(Document(jpeg.DocumentId, "/original"), Ct);
        jpegResponse.Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
        jpegResponse.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
        NoSniff(jpegResponse).ShouldBe("nosniff");

        // A raster image with parameters is still inline, served under the bare lower-case media type.
        var jpegWithParameters = await factory.SeedDocumentAsync(new DocumentSeed { FileName = "scan2.jpg", ContentType = "Image/JPEG; charset=binary", OriginalBytes = [0xFF, 0xD8, 0xFF, 0xE0, 4, 5, 6] });
        var parameterised = await viewer.GetAsync(Document(jpegWithParameters.DocumentId, "/original"), Ct);
        parameterised.Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
        parameterised.Content.Headers.ContentType.CharSet.ShouldBeNull();
        parameterised.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");

        var pdf = await factory.SeedDocumentAsync(new DocumentSeed { PagePng = TestInvoices.FakePng(), TextLayerJson = "{\"pages\":[]}" });
        var pdfResponse = await viewer.GetAsync(Document(pdf.DocumentId, "/original"), Ct);
        pdfResponse.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        pdfResponse.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
        NoSniff(pdfResponse).ShouldBe("nosniff");
        NoSniff(await viewer.GetAsync(Document(pdf.DocumentId, "/pages/1.png"), Ct)).ShouldBe("nosniff");
        NoSniff(await viewer.GetAsync(Document(pdf.DocumentId, "/text"), Ct)).ShouldBe("nosniff");
    }

    [Fact]
    public async Task OpenApi_document_describes_the_document_endpoints()
    {
        using var anonymous = factory.ClientFor(null);

        var json = JsonDocument.Parse(await anonymous.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct)).RootElement;

        var paths = json.GetProperty("paths");
        foreach (var path in new[] { "/api/v1/ingestions", "/api/v1/ingestions/{id}", "/api/v1/documents", "/api/v1/documents/{id}", "/api/v1/documents/{id}/pages/{n}.png", "/api/v1/documents/{id}/original", "/api/v1/documents/{id}/text", "/api/v1/documents/{id}/events", "/api/v1/documents/{id}:approve", "/api/v1/documents/{id}:reject", "/api/v1/documents/{id}:reprocess", "/api/v1/documents/{id}/fields", "/api/v1/review-tasks", "/api/v1/review-tasks/{id}:claim", "/api/v1/review-tasks/{id}:release" })
        {
            paths.TryGetProperty(path, out _).ShouldBeTrue(path);
        }

        var approve = paths.GetProperty("/api/v1/documents/{id}:approve").GetProperty("post");
        approve.GetProperty("operationId").GetString().ShouldBe("ApproveDocument");
        approve.GetProperty("tags")[0].GetString().ShouldBe("Documents");
        approve.GetProperty("summary").GetString().ShouldNotBeNullOrEmpty();
        paths.GetProperty("/api/v1/ingestions").GetProperty("post").GetProperty("tags")[0].GetString().ShouldBe("Ingestions");
        paths.GetProperty("/api/v1/review-tasks").GetProperty("get").GetProperty("tags")[0].GetString().ShouldBe("Review");

        var schemas = json.GetProperty("components").GetProperty("schemas");
        schemas.GetProperty("DocumentSummary").GetProperty("properties").GetProperty("status").GetProperty("$ref").GetString().ShouldBe("#/components/schemas/DocumentStatus");
        schemas.GetProperty("DocumentStatus").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ShouldContain("NeedsReview", "enums are documented as strings");
        schemas.GetProperty("DocumentDetail").GetProperty("properties").TryGetProperty("canonical", out _).ShouldBeTrue();
    }
}
