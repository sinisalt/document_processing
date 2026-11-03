using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Api.Endpoints;
using DocProc.Application.Abstractions;
using DocProc.Application.Documents;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Domain.Text;
using DocProc.Infrastructure.Catalogue;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Api.Tests;

/// <summary>
/// The review actions of docs/11 §12 (P2, track A) through the API: <c>:set-type</c> with prefill none and auto,
/// the approval gate without a definition or data, <c>:assign-supplier</c> and create supplier from review,
/// <c>:split</c> and <c>:join</c>, and the <c>move</c> correction. A tenant without <c>configurableTypes</c> approves
/// as before.
/// </summary>
public sealed class ReviewActionEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement V(object? value) => JsonSerializer.SerializeToElement(value);

    private static readonly JsonSerializerOptions TextLayerJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() }, IgnoreReadOnlyProperties = true };

    private Guid NewTenant(string name, bool configurable, bool adoptPriceList = false)
    {
        var tenant = factory.CreateTenant(name);
        using (var db = factory.PlatformDb())
        {
            TypeCatalogueSeeder.AddMissingAdoptedTypesAsync(db, tenant, factory.Clock.GetUtcNow(), Ct).GetAwaiter().GetResult();
            db.SaveChanges();
        }

        factory.Run(db => db.Tenants.Single(t => t.Id == tenant).Settings.Features!.ConfigurableTypes = configurable);
        if (adoptPriceList)
        {
            using var admin = factory.ClientFor(new TokenSpec("adm-" + tenant.ToString("N")[..8], [Roles.TenantAdmin], tenant.ToString()));
            admin.PostAsJsonAsync("/api/v1/document-types", new { from = new { template = "std.price-list", mode = "adopt" } }, Ct).GetAwaiter().GetResult().StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        return tenant;
    }

    private HttpClient Reviewer(Guid tenant, string? name = null) => factory.ClientFor(new TokenSpec(name ?? "rev-" + tenant.ToString("N")[..8], [Roles.Reviewer], tenant.ToString()));

    private static Uri Doc(Guid id, string suffix = "") => new($"/api/v1/documents/{id}{suffix}", UriKind.Relative);

    /// <summary>An Unknown document in review as Classify, Match, Extract and Validate leave one: no data, TYPE_UNKNOWN and SUPPLIER_UNMATCHED, a review task.</summary>
    private async Task<Guid> SeedUnknownAsync(Guid tenant, int pages = 1, string? supplier = null, DocumentStatus reached = DocumentStatus.Validated)
    {
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed
        {
            TenantId = tenant,
            Type = DocumentTypeKey.Unknown,
            Invoice = null,
            SupplierName = supplier,
            ReviewTask = true,
            Flags =
            [
                FlagCodes.Create(FlagCodes.TypeUnknown, "The document type could not be determined."),
                FlagCodes.Create(FlagCodes.SupplierUnmatched, "No supplier of the tenant matched the document; a new supplier may be needed."),
            ],
        });
        factory.RecordPipelineProgress(seeded.DocumentId, reached);
        if (pages > 1)
        {
            await GivePagesAsync(seeded.DocumentId, tenant, pages);
        }

        return seeded.DocumentId;
    }

    /// <summary>Page images and a text layer whose page n carries the words "page-n" and "Seite n".</summary>
    private async Task GivePagesAsync(Guid id, Guid tenant, int pages)
    {
        _ = factory.Server;
        var blobs = factory.Services.GetRequiredService<IContainerBlobStore>();
        var keys = new List<string>();
        var textPages = new List<TextPage>();
        for (var n = 1; n <= pages; n++)
        {
            var png = await blobs.PutAsync(BlobContainers.Derived, tenant, new MemoryStream(TestInvoices.FakePng()), "image/png", Ct);
            keys.Add(png.Key);
            textPages.Add(new TextPage(n, 1000, 1000,
            [
                new PositionedWord($"page-{n}", new BoundingBox(100, 100, 200, 120)),
                .. Enumerable.Range(0, 30).Select(i => new PositionedWord($"Seite{n}w{i}", new BoundingBox(100, 200 + i, 150, 210 + i))),
            ]));
        }

        var layer = await blobs.PutAsync(BlobContainers.Derived, tenant, new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new TextLayer(TextLayerSource.PdfText, textPages), TextLayerJson)), "application/json", Ct);
        factory.Run(db =>
        {
            var document = db.Documents.Single(d => d.Id == id);
            document.PageImageKeys = keys;
            document.PageCount = pages;
            document.PageStart = 1;
            document.PageEnd = pages;
            document.TextLayerBlob = new BlobReference { Container = layer.Container, Key = layer.Key, Sha256 = layer.Sha256, SizeBytes = layer.SizeBytes, ContentType = layer.ContentType };
        });
    }

    private async Task<TextLayer> TextLayerOfAsync(Guid id)
    {
        var blob = factory.Query(db => db.Documents.AsNoTracking().Single(d => d.Id == id).TextLayerBlob).ShouldNotBeNull();
        var blobs = factory.Services.GetRequiredService<IContainerBlobStore>();
        await using var stream = await blobs.OpenReadAsync(new BlobRef(blob.Container, blob.Key, blob.Sha256, blob.SizeBytes, blob.ContentType), Ct);
        return (await JsonSerializer.DeserializeAsync<TextLayer>(stream, TextLayerJson, Ct))!;
    }

    private static async Task<DocumentDetail> DetailOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!;

    private static async Task<JsonElement> ProblemOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    [Fact]
    public async Task Set_type_none_gives_an_unknown_document_the_empty_tree_of_the_published_version_and_approval_follows()
    {
        var tenant = NewTenant("Set type none", configurable: true, adoptPriceList: true);
        var id = await SeedUnknownAsync(tenant);
        using var reviewer = Reviewer(tenant);

        // Before: Unknown cannot be approved, and the detail says why.
        var before = (await reviewer.GetFromJsonAsync<DocumentDetail>(Doc(id), ApiFactory.Json, Ct))!;
        before.ApprovalBlocked.ShouldNotBeNull().Code.ShouldBe(DocumentEndpoints.ApprovalBlockCodes.TypeRequired);
        var refused = await reviewer.PostAsync(Doc(id, ":approve"), null, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await ProblemOf(refused);
        problem.GetProperty("title").GetString().ShouldBe("Cannot approve");
        problem.GetProperty("code").GetString().ShouldBe(DocumentEndpoints.ApprovalBlockCodes.TypeRequired);
        factory.Query(db => db.Documents.Single(d => d.Id == id).Status).ShouldBe(DocumentStatus.NeedsReview);
        factory.Queue.Documents.ShouldNotContain(m => m.DocumentId == id && m.Stage == PipelineStage.Publish);

        var response = await reviewer.PostAsJsonAsync(Doc(id, ":set-type"), new SetDocumentTypeRequest("price-list"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var detail = await DetailOf(response);
        detail.Type.Value.ShouldBe("price-list");
        detail.Status.ShouldBe(DocumentStatus.NeedsReview, "prefill none stays in review");
        detail.CanonicalSchemaVersion.ShouldBe(DefinitionTree.SchemaVersion);
        detail.Definition.ShouldNotBeNull().Key.ShouldBe("price-list");
        detail.TypeVersion.ShouldBe(1);
        detail.ExtractionMethod.ShouldBe(ExtractionMethod.Manual);
        var canonical = JsonNode.Parse(detail.Canonical!.Value.GetRawText())!;
        canonical["validFrom"]!["status"]!.GetValue<string>().ShouldBe("Missing", "every field of the type is there, Missing");
        detail.ConfigurableTypes.ShouldBeTrue("the review screen offers the P2 tools");

        // The empty tree holds no value: approving it would publish an empty price list (docs/11 §11), so it is refused.
        detail.ApprovalBlocked.ShouldNotBeNull().Code.ShouldBe(DocumentEndpoints.ApprovalBlockCodes.NoData);
        var empty = await reviewer.PostAsync(Doc(id, ":approve"), null, Ct);
        empty.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemOf(empty)).GetProperty("code").GetString().ShouldBe(DocumentEndpoints.ApprovalBlockCodes.NoData);
        factory.Query(db => db.Documents.Single(d => d.Id == id).Status).ShouldBe(DocumentStatus.NeedsReview);
        factory.Query(db => db.OutboxMessages.Count(m => m.DocumentId == id)).ShouldBe(0, "nothing is published");
        var reviewerName = "rev-" + tenant.ToString("N")[..8];
        detail.Flags.ShouldContain(f => f.Code == FlagCodes.TypeUnknown && f.ResolvedAt != null && f.ResolvedBy == reviewerName);
        detail.Flags.Where(f => f.ResolvedAt == null).Select(f => f.Code).ShouldContain(FlagCodes.FieldRequired, "the inline check names the required fields");

        var stored = factory.Query(db => db.Documents.AsNoTracking().Single(d => d.Id == id));
        stored.TypeSource.ShouldBe(TypeSource.Human);
        stored.DocumentTypeVersionId.ShouldBe(detail.Definition!.VersionId, "pinned to the published version");
        stored.AppliedConfigJson!.ShouldContain("\"typeKey\":\"price-list\"");
        var correction = factory.Query(db => db.Corrections.AsNoTracking().Single(c => c.DocumentId == id));
        correction.Op.ShouldBe(CorrectionOps.SetType);
        correction.FieldPath.ShouldBe("type");
        correction.OldValueJson!.ShouldContain("\"typeKey\":\"Unknown\"");
        correction.NewValueJson!.ShouldContain("\"prefill\":\"none\"");
        correction.DocumentTypeVersionId.ShouldBe(stored.DocumentTypeVersionId);
        var typeSet = factory.Query(db => db.DocumentEvents.AsNoTracking().Single(e => e.DocumentId == id && e.EventType == DocumentEventTypes.TypeSet));
        typeSet.FromStatus.ShouldBeNull("no state change");
        typeSet.DetailsJson!.ShouldContain("\"to\":\"price-list\"");

        // Setting the same type again with prefill none would only empty the form: refused.
        (await reviewer.PostAsJsonAsync(Doc(id, ":set-type"), new SetDocumentTypeRequest("price-list"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // A row added by hand with every field Missing is no value either.
        var appended = await reviewer.PatchAsJsonAsync(Doc(id, "/fields"), new CorrectFieldsRequest([new FieldCorrection("lines", V(new { }), null, Op: "append")]), Ct);
        appended.StatusCode.ShouldBe(HttpStatusCode.OK, await appended.Content.ReadAsStringAsync(Ct));
        (await DetailOf(appended)).ApprovalBlocked.ShouldNotBeNull().Code.ShouldBe(DocumentEndpoints.ApprovalBlockCodes.NoData);
        (await reviewer.PatchAsJsonAsync(Doc(id, "/fields"), new CorrectFieldsRequest([new FieldCorrection("lines[0]", null, null, Op: "remove")]), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The reviewer fills a value and approves; the type, not the gate, decides now.
        (await reviewer.PatchAsJsonAsync(Doc(id, "/fields"), new CorrectFieldsRequest([new FieldCorrection("validFrom", V("2026-11-01"), null)]), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var approved = await reviewer.PostAsync(Doc(id, ":approve"), null, Ct);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
        (await DetailOf(approved)).ValidFrom.ShouldBe(new DateOnly(2026, 11, 1));
    }

    [Fact]
    public async Task Set_type_auto_sends_the_document_back_to_extract_pinned_to_the_version()
    {
        var tenant = NewTenant("Set type auto", configurable: true);
        var id = await SeedUnknownAsync(tenant);
        using var reviewer = Reviewer(tenant);
        var taskId = factory.Query(db => db.ReviewTasks.Single(t => t.DocumentId == id).Id);

        var response = await reviewer.PostAsJsonAsync(Doc(id, ":set-type"), new SetDocumentTypeRequest("Invoice", Version: 1, Prefill: "auto"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var detail = await DetailOf(response);
        detail.Status.ShouldBe(DocumentStatus.Matched);
        detail.Type.ShouldBe(DocumentTypeKey.Invoice);
        detail.Canonical.ShouldBeNull("Extract fills it");
        factory.Queue.Documents.ShouldContain(m => m.DocumentId == id && m.Stage == PipelineStage.Extract);
        factory.Query(db => db.ReviewTasks.Single(t => t.Id == taskId).Status).ShouldBe(ReviewTaskStatus.Skipped);
        var stored = factory.Query(db => db.Documents.AsNoTracking().Single(d => d.Id == id));
        stored.TypeSource.ShouldBe(TypeSource.Human);
        var version = factory.Query(db => db.DocumentTypeVersions.AsNoTracking().Single(v => v.TenantId == tenant && v.Definition!.Key == "Invoice" && v.Version == 1).Id);
        stored.DocumentTypeVersionId.ShouldBe(version);
        var typeSet = factory.Query(db => db.DocumentEvents.AsNoTracking().Single(e => e.DocumentId == id && e.EventType == DocumentEventTypes.TypeSet));
        typeSet.FromStatus.ShouldBe(DocumentStatus.NeedsReview);
        typeSet.ToStatus.ShouldBe(DocumentStatus.Matched);

        // A document that never got past Classify cannot be extracted yet.
        var early = await SeedUnknownAsync(tenant, reached: DocumentStatus.Classified);
        var notReached = await reviewer.PostAsJsonAsync(Doc(early, ":set-type"), new SetDocumentTypeRequest("Invoice", Prefill: "auto"), Ct);
        notReached.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemOf(notReached)).GetProperty("title").GetString().ShouldBe("Stage not reached");
    }

    [Fact]
    public async Task Set_type_refuses_what_it_cannot_do()
    {
        var tenant = NewTenant("Set type refusals", configurable: true);
        var id = await SeedUnknownAsync(tenant);
        using var reviewer = Reviewer(tenant);

        async Task<HttpResponseMessage> Set(object body) => await reviewer.PostAsJsonAsync(Doc(id, ":set-type"), body, Ct);

        (await Set(new { typeKey = "Unknown" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Set(new { typeKey = "not a key" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Set(new { typeKey = "Invoice", prefill = "llm" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var notAdopted = await Set(new { typeKey = "price-list" });
        notAdopted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await notAdopted.Content.ReadAsStringAsync(Ct)).ShouldContain("no published definition");
        var noVersion = await Set(new { typeKey = "Invoice", version = 7 });
        noVersion.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await noVersion.Content.ReadAsStringAsync(Ct)).ShouldContain("no published version 7");

        using (var viewer = factory.ClientFor(new TokenSpec("viewer", [Roles.Viewer], tenant.ToString())))
        {
            (await viewer.PostAsJsonAsync(Doc(id, ":set-type"), new SetDocumentTypeRequest("Invoice"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var other = factory.ClientFor(new TokenSpec("rev-b", [Roles.Reviewer], ApiFactory.TenantB.ToString())))
        {
            (await other.PostAsJsonAsync(Doc(id, ":set-type"), new SetDocumentTypeRequest("Invoice"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        var settled = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, Status = DocumentStatus.Approved });
        (await reviewer.PostAsJsonAsync(Doc(settled.DocumentId, ":set-type"), new SetDocumentTypeRequest("CreditNote"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        factory.Query(db => db.Corrections.Count(c => c.DocumentId == id)).ShouldBe(0, "nothing was written");
    }

    [Fact]
    public async Task Without_configurable_types_an_unknown_document_approves_as_before_and_set_type_gives_an_invoice_its_empty_record()
    {
        var tenant = NewTenant("Legacy tenant", configurable: false);
        using var reviewer = Reviewer(tenant);

        // The gate is behind configurableTypes: this tenant approves an Unknown document exactly as before.
        var unknown = await SeedUnknownAsync(tenant);
        var legacyDetail = (await reviewer.GetFromJsonAsync<DocumentDetail>(Doc(unknown), ApiFactory.Json, Ct))!;
        legacyDetail.ApprovalBlocked.ShouldBeNull();
        legacyDetail.ConfigurableTypes.ShouldBeFalse("the review screen keeps its pre-P2 empty state for this tenant");
        (await reviewer.PostAsync(Doc(unknown, ":approve"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var id = await SeedUnknownAsync(tenant);
        var response = await reviewer.PostAsJsonAsync(Doc(id, ":set-type"), new SetDocumentTypeRequest("CreditNote"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var detail = await DetailOf(response);
        detail.Type.ShouldBe(DocumentTypeKey.CreditNote);
        detail.CanonicalSchemaVersion.ShouldBe(CanonicalDocumentSerializer.SchemaVersion, "an adopted credit note stays a record of the coded type");
        detail.Definition.ShouldBeNull("the payload of a tenant without the switch is unchanged");
        var record = CanonicalDocumentSerializer.DeserializeInvoice(detail.Canonical!.Value.GetRawText());
        record.Kind.Value.ShouldBe(Domain.Invoices.InvoiceKind.CreditNote);
        record.InvoiceNumber.HasValue.ShouldBeFalse();
        detail.Flags.Where(f => f.ResolvedAt == null).Select(f => f.Code).ShouldContain(FlagCodes.MissingRequired, "the coded rules ran inline");

        // The coded patcher fills it from there.
        (await reviewer.PatchAsJsonAsync(Doc(id, "/fields"), new CorrectFieldsRequest([new FieldCorrection("invoiceNumber", V("GS-7"), null)]), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Query(db => db.Documents.Single(d => d.Id == id).DocumentNumber).ShouldBe("GS-7");
    }

    [Fact]
    public async Task Approve_is_refused_for_a_type_without_definition_or_data_when_types_are_configurable()
    {
        var tenant = NewTenant("Approve gate", configurable: true);
        using var reviewer = Reviewer(tenant);

        // A purchase order with no tree stored yet: nothing was captured.
        var empty = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, Type = DocumentTypeKey.Parse("PurchaseOrder"), Invoice = null, ReviewTask = true });
        var detail = (await reviewer.GetFromJsonAsync<DocumentDetail>(Doc(empty.DocumentId), ApiFactory.Json, Ct))!;
        detail.ApprovalBlocked.ShouldNotBeNull().Code.ShouldBe(DocumentEndpoints.ApprovalBlockCodes.NoData);
        var refused = await reviewer.PostAsync(Doc(empty.DocumentId, ":approve"), null, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemOf(refused)).GetProperty("code").GetString().ShouldBe(DocumentEndpoints.ApprovalBlockCodes.NoData);

        // A type the tenant has no definition of.
        var orphan = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, Type = DocumentTypeKey.Parse("price-list"), Invoice = null, ReviewTask = true });
        (await ProblemOf(await reviewer.PostAsync(Doc(orphan.DocumentId, ":approve"), null, Ct))).GetProperty("code").GetString().ShouldBe(DocumentEndpoints.ApprovalBlockCodes.NoDefinition);

        // An invoice typed in review with prefill none holds the empty record of its kind: its kind is the record's own,
        // not captured, so it is no data either until a value is filled.
        var typed = await SeedUnknownAsync(tenant);
        var record = await DetailOf(await reviewer.PostAsJsonAsync(Doc(typed, ":set-type"), new SetDocumentTypeRequest("Invoice"), Ct));
        record.CanonicalSchemaVersion.ShouldBe(CanonicalDocumentSerializer.SchemaVersion);
        record.ApprovalBlocked.ShouldNotBeNull().Code.ShouldBe(DocumentEndpoints.ApprovalBlockCodes.NoData);
        var filled = await reviewer.PatchAsJsonAsync(Doc(typed, "/fields"), new CorrectFieldsRequest([new FieldCorrection("invoiceNumber", V("RE-77"), null)]), Ct);
        filled.StatusCode.ShouldBe(HttpStatusCode.OK, await filled.Content.ReadAsStringAsync(Ct));
        (await DetailOf(filled)).ApprovalBlocked.ShouldBeNull();

        // An invoice with data approves as always.
        var invoice = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, ReviewTask = true });
        (await reviewer.GetFromJsonAsync<DocumentDetail>(Doc(invoice.DocumentId), ApiFactory.Json, Ct))!.ApprovalBlocked.ShouldBeNull();
        (await reviewer.PostAsync(Doc(invoice.DocumentId, ":approve"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Assign_supplier_sets_a_human_supplier_resolves_the_flag_and_rechecks_duplicates()
    {
        var tenant = NewTenant("Assign supplier", configurable: false);
        using var reviewer = Reviewer(tenant);

        // An approved invoice of METRO with number RE-1, and a second one with the same number that matched nobody.
        var first = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, Status = DocumentStatus.Approved, Invoice = TestInvoices.Valid("RE-1") });
        var supplierId = factory.Query(db => db.Documents.Single(d => d.Id == first.DocumentId).SupplierId!.Value);
        var second = await factory.SeedDocumentAsync(new DocumentSeed
        {
            TenantId = tenant,
            Invoice = TestInvoices.Valid("RE-1"),
            SupplierName = null,
            ReviewTask = true,
            Flags = [FlagCodes.Create(FlagCodes.SupplierUnmatched, "No supplier matched.")],
        });

        var response = await reviewer.PostAsJsonAsync(Doc(second.DocumentId, ":assign-supplier"), new AssignSupplierRequest(supplierId), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var detail = await DetailOf(response);
        detail.SupplierId.ShouldBe(supplierId);
        detail.SupplierName.ShouldBe("METRO Deutschland GmbH");
        detail.SupplierMatchMethod.ShouldBe(SupplierMatchMethod.Human);
        detail.Flags.ShouldContain(f => f.Code == FlagCodes.SupplierUnmatched && f.ResolvedAt != null);
        detail.Flags.ShouldContain(f => f.Code == FlagCodes.DuplicateSuspected && f.ResolvedAt == null, "the same supplier already sent RE-1");
        factory.Query(db => db.DocumentEvents.AsNoTracking().Single(e => e.DocumentId == second.DocumentId && e.EventType == DocumentEventTypes.SupplierAssigned).DetailsJson)!
            .ShouldContain(supplierId.ToString());

        // Only a supplier of the document's own tenant.
        var ofTenantA = await factory.SeedDocumentAsync(new DocumentSeed { SupplierName = "Fremdlieferant GmbH" });
        var foreign = factory.Query(db => db.Documents.Single(d => d.Id == ofTenantA.DocumentId).SupplierId!.Value);
        (await reviewer.PostAsJsonAsync(Doc(second.DocumentId, ":assign-supplier"), new AssignSupplierRequest(foreign), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await reviewer.PostAsJsonAsync(Doc(second.DocumentId, ":assign-supplier"), new AssignSupplierRequest(Guid.NewGuid()), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await reviewer.PostAsJsonAsync(Doc(first.DocumentId, ":assign-supplier"), new AssignSupplierRequest(supplierId), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "a settled document is not in review");
    }

    [Fact]
    public async Task Create_supplier_from_review_runs_the_supplier_checks_and_assigns_the_new_supplier()
    {
        var tenant = NewTenant("Create supplier", configurable: true);
        var id = await SeedUnknownAsync(tenant);
        using var reviewer = Reviewer(tenant);

        var response = await reviewer.PostAsJsonAsync(Doc(id, ":create-supplier"), new CreateSupplierRequest("Nordwind Großhandel GmbH", "DE 811 111 111", null, ["DE89 3704 0044 0532 0130 00"], null, null, "EUR", null), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var created = (await response.Content.ReadFromJsonAsync<SupplierFromReviewResponse>(ApiFactory.Json, Ct))!;
        created.Supplier.Name.ShouldBe("Nordwind Großhandel GmbH");
        created.Supplier.VatId.ShouldBe("DE811111111", "normalised as POST /suppliers does");
        created.Supplier.Ibans.ShouldBe(["DE89370400440532013000"]);
        created.Document.SupplierId.ShouldBe(created.Supplier.Id);
        created.Document.SupplierMatchMethod.ShouldBe(SupplierMatchMethod.Human);
        created.Document.Flags.ShouldContain(f => f.Code == FlagCodes.SupplierUnmatched && f.ResolvedAt != null);
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/suppliers/{created.Supplier.Id}");
        factory.Query(db => db.Suppliers.Single(s => s.Id == created.Supplier.Id).TenantId).ShouldBe(tenant);

        // The checks of POST /suppliers: a name that folds to an existing one, an invalid currency.
        var other = await SeedUnknownAsync(tenant);
        var duplicate = await reviewer.PostAsJsonAsync(Doc(other, ":create-supplier"), new CreateSupplierRequest("NORDWIND Grosshandel GmbH", null, null, null, null, null, null, null), Ct);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemOf(duplicate)).GetProperty("title").GetString().ShouldBe("Supplier exists");
        (await reviewer.PostAsJsonAsync(Doc(other, ":create-supplier"), new CreateSupplierRequest("Südhof Feinkost", null, null, null, null, null, "euro", null), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await reviewer.PostAsJsonAsync(Doc(other, ":create-supplier"), new CreateSupplierRequest(" ", null, null, null, null, null, null, null), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        factory.Query(db => db.Documents.Single(d => d.Id == other).SupplierId).ShouldBeNull("nothing was assigned");

        // The caps of POST /suppliers too: before, a reviewer stored 20,000 IBANs of 222 characters, or "not an iban at all".
        var many = await reviewer.PostAsJsonAsync(Doc(other, ":create-supplier"), new CreateSupplierRequest("Südhof Feinkost", null, null, [.. Enumerable.Range(0, 20_000).Select(i => $"DE{i:0000}" + new string('9', 216))], null, null, null, null), Ct);
        many.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.RequestEntityTooLarge);
        var notAnIban = await reviewer.PostAsJsonAsync(Doc(other, ":create-supplier"), new CreateSupplierRequest("Südhof Feinkost", null, null, ["not an iban at all"], null, null, null, null), Ct);
        notAnIban.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ProblemOf(notAnIban)).GetProperty("errorCodes").GetProperty("ibans").GetProperty("code").GetString().ShouldBe("iban");
        factory.Query(db => db.Suppliers.Any(s => s.TenantId == tenant && s.Name == "Südhof Feinkost")).ShouldBeFalse();
        factory.Query(db => db.Documents.Single(d => d.Id == other).SupplierId).ShouldBeNull("nothing was assigned");

        // A supplier belongs to a tenant: an operator in platform scope names one first.
        using var operatorClient = factory.ClientFor(new TokenSpec("ops", [Roles.Operator]));
        (await operatorClient.PostAsJsonAsync(Doc(other, ":create-supplier"), new CreateSupplierRequest("Platform GmbH", null, null, null, null, null, null, null), Ct)).IsSuccessStatusCode.ShouldBeFalse();
    }

    [Fact]
    public async Task Create_supplier_from_review_leaves_email_domains_aliases_and_always_review_to_a_tenant_admin()
    {
        // Separation of duties (docs/11 §14): what steers the Match stage and the review of later documents beyond the
        // identifiers printed on the document is master data of a tenant admin, as with POST /suppliers.
        var tenant = NewTenant("Create supplier roles", configurable: true);
        var id = await SeedUnknownAsync(tenant);
        using var reviewer = Reviewer(tenant);
        foreach (var (request, field) in new[]
        {
            (new CreateSupplierRequest("Nordwind Großhandel GmbH", null, null, null, ["nordwind.example"], null, null, null), "emailDomains"),
            (new CreateSupplierRequest("Nordwind Großhandel GmbH", null, null, null, null, ["Nordwind"], null, null), "aliases"),
            (new CreateSupplierRequest("Nordwind Großhandel GmbH", null, null, null, null, null, null, false), "alwaysReview"),
        })
        {
            var refused = await reviewer.PostAsJsonAsync(Doc(id, ":create-supplier"), request, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, field);
            (await ProblemOf(refused)).GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain(field);
        }

        factory.Query(db => db.Suppliers.Count(s => s.TenantId == tenant)).ShouldBe(0, "nothing was created");
        factory.Query(db => db.Documents.Single(d => d.Id == id).SupplierId).ShouldBeNull();

        // The name and the identifiers printed on the document are the reviewer's.
        var printed = await reviewer.PostAsJsonAsync(Doc(id, ":create-supplier"), new CreateSupplierRequest("Nordwind Großhandel GmbH", "DE 811 111 111", "12/345/67890", ["DE89 3704 0044 0532 0130 00"], [], [], "EUR", null), Ct);
        printed.StatusCode.ShouldBe(HttpStatusCode.Created, await printed.Content.ReadAsStringAsync(Ct));

        // A tenant admin sets the rest.
        var second = await SeedUnknownAsync(tenant);
        using var admin = factory.ClientFor(new TokenSpec("adm-" + tenant.ToString("N")[..8], [Roles.TenantAdmin], tenant.ToString()));
        var full = await admin.PostAsJsonAsync(Doc(second, ":create-supplier"), new CreateSupplierRequest("Südhof Feinkost", null, null, null, ["suedhof.example"], ["Suedhof"], null, true), Ct);
        full.StatusCode.ShouldBe(HttpStatusCode.Created, await full.Content.ReadAsStringAsync(Ct));
        var created = (await full.Content.ReadFromJsonAsync<SupplierFromReviewResponse>(ApiFactory.Json, Ct))!;
        created.Supplier.EmailDomains.ShouldBe(["suedhof.example"]);
        created.Supplier.AlwaysReview.ShouldBeTrue();
    }

    [Fact]
    public async Task Split_cuts_the_pages_into_documents_of_the_same_attachment_and_join_puts_them_back()
    {
        var tenant = NewTenant("Split and join", configurable: true);
        var id = await SeedUnknownAsync(tenant, pages: 3, supplier: "Nordwind Großhandel GmbH");
        using var reviewer = Reviewer(tenant);
        var taskId = factory.Query(db => db.ReviewTasks.Single(t => t.DocumentId == id).Id);
        var itemId = factory.Query(db => db.Documents.Single(d => d.Id == id).IngestionItemId);
        var keys = factory.Query(db => db.Documents.AsNoTracking().Single(d => d.Id == id).PageImageKeys.ToList());

        var response = await reviewer.PostAsJsonAsync(Doc(id, ":split"), new SplitDocumentRequest([new SplitPartRequest([1, 2]), new SplitPartRequest([3], "Invoice")]), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var split = (await response.Content.ReadFromJsonAsync<SplitDocumentResponse>(ApiFactory.Json, Ct))!;
        split.Parts.Count.ShouldBe(2);
        split.Parts[0].DocumentId.ShouldBe(id, "the first part keeps the id");
        split.Parts[0].Status.ShouldBe(DocumentStatus.Split);
        split.Parts[1].Status.ShouldBe(DocumentStatus.Matched, "a typed part skips Classify and Match");
        var partId = split.Parts[1].DocumentId;

        var first = factory.Query(db => db.Documents.AsNoTracking().Single(d => d.Id == id));
        first.PageCount.ShouldBe(2);
        first.PageEnd.ShouldBe(2);
        first.PageImageKeys.ShouldBe(keys.Take(2));
        first.Type.ShouldBe(DocumentTypeKey.Unknown);
        first.CanonicalJson.ShouldBeNull();
        var part = factory.Query(db => db.Documents.AsNoTracking().Single(d => d.Id == partId));
        part.IngestionItemId.ShouldBe(itemId);
        part.PageStart.ShouldBe(3);
        part.PageCount.ShouldBe(1);
        part.PageImageKeys.ShouldBe([keys[2]]);
        part.Type.ShouldBe(DocumentTypeKey.Invoice);
        part.TypeSource.ShouldBe(TypeSource.Human);
        part.DocumentTypeVersionId.ShouldNotBeNull();
        part.SupplierId.ShouldBe(first.SupplierId, "the supplier came with the attachment");
        var partText = await TextLayerOfAsync(partId);
        partText.Pages.ShouldHaveSingleItem().Number.ShouldBe(1);
        partText.Pages[0].Words[0].Text.ShouldBe("page-3");
        (await TextLayerOfAsync(id)).Pages.Select(p => p.Words[0].Text).ShouldBe(["page-1", "page-2"]);

        factory.Queue.Documents.ShouldContain(m => m.DocumentId == id && m.Stage == PipelineStage.Classify);
        factory.Queue.Documents.ShouldContain(m => m.DocumentId == partId && m.Stage == PipelineStage.Extract);
        factory.Query(db => db.ReviewTasks.Single(t => t.Id == taskId).Status).ShouldBe(ReviewTaskStatus.Skipped);
        factory.Query(db => db.Corrections.AsNoTracking().Single(c => c.DocumentId == id && c.Op == CorrectionOps.Split).NewValueJson)!.ShouldContain(partId.ToString());
        factory.Query(db => db.DocumentEvents.AsNoTracking().Any(e => e.DocumentId == id && e.EventType == DocumentEventTypes.Split && e.ToStatus == DocumentStatus.Split)).ShouldBeTrue();
        factory.Query(db => db.DocumentEvents.AsNoTracking().Single(e => e.DocumentId == partId && e.EventType == DocumentEventTypes.Received).DetailsJson)!.ShouldContain(id.ToString());
        factory.Query(db => db.DocumentFlags.AsNoTracking().Any(f => f.DocumentId == id && f.Code == FlagCodes.TypeUnknown && f.ResolvedAt == null)).ShouldBeFalse("the flags of the old pages go; Classify raises its own");
        factory.Query(db => db.DocumentFlags.AsNoTracking().Any(f => f.DocumentId == partId && f.Code == FlagCodes.MultiDocumentSplit)).ShouldBeTrue();

        // The parts come back from the pipeline into review; then the reviewer joins them again.
        factory.Run(db =>
        {
            foreach (var d in db.Documents.Where(d => d.Id == id || d.Id == partId))
            {
                d.Status = DocumentStatus.NeedsReview;
                d.CanonicalJson = "{}";
                d.DocumentNumber = "RE-JOINED";
                d.DocumentNumberNormalized = "REJOINED";
            }
        });
        var join = await reviewer.PostAsJsonAsync(Doc(id, ":join"), new JoinDocumentsRequest([partId]), Ct);

        join.StatusCode.ShouldBe(HttpStatusCode.Accepted, await join.Content.ReadAsStringAsync(Ct));
        var joined = await DetailOf(join);
        joined.Status.ShouldBe(DocumentStatus.Split);
        joined.PageCount.ShouldBe(3);
        joined.PageImageUrls.Count.ShouldBe(3);
        var stored = factory.Query(db => db.Documents.AsNoTracking().Single(d => d.Id == id));
        stored.PageImageKeys.ShouldBe(keys, "pages in attachment order");
        stored.NormalizedBlob.ShouldNotBeNull("the whole attachment again: its file is the document's");
        stored.PageStart.ShouldBe(1);
        stored.PageEnd.ShouldBe(3);
        (await TextLayerOfAsync(id)).Pages.Select(p => (p.Number, p.Words[0].Text)).ShouldBe([(1, "page-1"), (2, "page-2"), (3, "page-3")]);
        var cancelled = factory.Query(db => db.Documents.AsNoTracking().Single(d => d.Id == partId));
        cancelled.Status.ShouldBe(DocumentStatus.Cancelled);
        cancelled.JoinedIntoDocumentId.ShouldBe(id);
        cancelled.DocumentNumberNormalized.ShouldBeNull("a cancelled part is never a duplicate");
        (await reviewer.GetFromJsonAsync<DocumentDetail>(Doc(partId), ApiFactory.Json, Ct))!.JoinedIntoDocumentId.ShouldBe(id);
        factory.Queue.Documents.Count(m => m.DocumentId == id && m.Stage == PipelineStage.Classify).ShouldBe(2);
        factory.Query(db => db.Corrections.AsNoTracking().Single(c => c.DocumentId == id && c.Op == CorrectionOps.Join).NewValueJson)!.ShouldContain(partId.ToString());
        factory.Query(db => db.DocumentEvents.AsNoTracking().Single(e => e.DocumentId == partId && e.EventType == DocumentEventTypes.Joined).ToStatus).ShouldBe(DocumentStatus.Cancelled);
        factory.Query(db => db.OutboxMessages.Count(o => o.DocumentId == id || o.DocumentId == partId)).ShouldBe(0, "nothing is published for a split or a join");

        // A cancelled part is done with.
        (await reviewer.PostAsync(Doc(partId, ":approve"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await reviewer.PostAsJsonAsync(Doc(partId, ":set-type"), new SetDocumentTypeRequest("Invoice"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await reviewer.PostAsync(Doc(partId, ":reprocess"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Split_and_join_refuse_what_does_not_add_up()
    {
        var tenant = NewTenant("Split refusals", configurable: true);
        var id = await SeedUnknownAsync(tenant, pages: 3);
        using var reviewer = Reviewer(tenant);

        async Task<HttpResponseMessage> Split(Guid document, params SplitPartRequest[] parts) =>
            await reviewer.PostAsJsonAsync(Doc(document, ":split"), new SplitDocumentRequest(parts), Ct);

        (await Split(id, new SplitPartRequest([1, 2, 3]))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var gap = await Split(id, new SplitPartRequest([1]), new SplitPartRequest([3]));
        gap.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await gap.Content.ReadAsStringAsync(Ct)).ShouldContain("page 2 was expected");
        (await Split(id, new SplitPartRequest([1, 3]), new SplitPartRequest([2]))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Split(id, new SplitPartRequest([1]), new SplitPartRequest([2]))).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "page 3 belongs to no part");
        (await Split(id, new SplitPartRequest([1]), new SplitPartRequest([2, 3], "price-list"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "the tenant has no price-list type");
        (await Split(id, new SplitPartRequest([1]), new SplitPartRequest([2, 3], "Unknown"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        factory.Query(db => db.Documents.Count(d => d.IngestionItemId == db.Documents.Single(x => x.Id == id).IngestionItemId)).ShouldBe(1, "nothing was split");

        var einvoice = await SeedUnknownAsync(tenant, pages: 2);
        factory.Run(db => db.Documents.Single(d => d.Id == einvoice).EmbeddedXmlBlob = new BlobReference { Container = BlobContainers.Derived, Key = "x.xml", Sha256 = "00", SizeBytes = 1, ContentType = "application/xml" });
        (await Split(einvoice, new SplitPartRequest([1]), new SplitPartRequest([2]))).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Join: only parts of one attachment, in one run of pages, all in review.
        var other = await SeedUnknownAsync(tenant, pages: 2);
        var otherAttachment = await reviewer.PostAsJsonAsync(Doc(id, ":join"), new JoinDocumentsRequest([other]), Ct);
        otherAttachment.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await otherAttachment.Content.ReadAsStringAsync(Ct)).ShouldContain("same attachment");
        (await reviewer.PostAsJsonAsync(Doc(id, ":join"), new JoinDocumentsRequest([id]), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await reviewer.PostAsJsonAsync(Doc(id, ":join"), new JoinDocumentsRequest([]), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var three = (await (await Split(id, new SplitPartRequest([1]), new SplitPartRequest([2]), new SplitPartRequest([3]))).Content.ReadFromJsonAsync<SplitDocumentResponse>(ApiFactory.Json, Ct))!;
        factory.Run(db =>
        {
            foreach (var d in db.Documents.Where(d => d.IngestionItemId == db.Documents.Single(x => x.Id == id).IngestionItemId))
            {
                d.Status = DocumentStatus.NeedsReview;
            }
        });
        var hole = await reviewer.PostAsJsonAsync(Doc(three.Parts[0].DocumentId, ":join"), new JoinDocumentsRequest([three.Parts[2].DocumentId]), Ct);
        hole.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await hole.Content.ReadAsStringAsync(Ct)).ShouldContain("unbroken run of pages");

        factory.Run(db => db.Documents.Single(d => d.Id == three.Parts[1].DocumentId).Status = DocumentStatus.Approved);
        (await reviewer.PostAsJsonAsync(Doc(three.Parts[0].DocumentId, ":join"), new JoinDocumentsRequest([three.Parts[1].DocumentId]), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using var otherTenant = factory.ClientFor(new TokenSpec("rev-b", [Roles.Reviewer], ApiFactory.TenantB.ToString()));
        (await otherTenant.PostAsJsonAsync(Doc(id, ":split"), new SplitDocumentRequest([new SplitPartRequest([1]), new SplitPartRequest([2])]), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_move_takes_a_value_captured_into_the_wrong_field_into_the_right_one_as_one_correction()
    {
        var tenant = NewTenant("Move", configurable: true, adoptPriceList: true);
        var id = await SeedUnknownAsync(tenant);
        using var reviewer = Reviewer(tenant);
        (await reviewer.PostAsJsonAsync(Doc(id, ":set-type"), new SetDocumentTypeRequest("price-list"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var placed = new CorrectionSource(Method: "region", Page: 1, BBox: new SourceBox(600, 80, 700, 95), RawText: "01.11.2026");
        (await reviewer.PatchAsJsonAsync(Doc(id, "/fields"), new CorrectFieldsRequest([new FieldCorrection("issueDate", V("2026-11-01"), null, Source: placed)]), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var correctionsBefore = factory.Query(db => db.Corrections.Count(c => c.DocumentId == id));

        var response = await reviewer.PatchAsJsonAsync(Doc(id, "/fields"), new CorrectFieldsRequest([new FieldCorrection("validFrom", null, "it is the start of validity", Op: "move", From: "issueDate")]), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var detail = await DetailOf(response);
        detail.ValidFrom.ShouldBe(new DateOnly(2026, 11, 1));
        var canonical = JsonNode.Parse(detail.Canonical!.Value.GetRawText())!;
        canonical["validFrom"]!["value"]!.GetValue<string>().ShouldBe("2026-11-01");
        canonical["validFrom"]!["source"]!["page"]!.GetValue<int>().ShouldBe(1, "the value keeps the place it was read from");
        canonical["validFrom"]!["source"]!["method"]!.GetValue<string>().ShouldBe("Human");
        canonical["issueDate"]!["status"]!.GetValue<string>().ShouldBe("Missing");
        canonical["issueDate"]!["value"].ShouldBeNull();
        canonical["issueDate"]!["history"]!.AsArray().Last()!["value"]!.GetValue<string>().ShouldBe("2026-11-01", "history is kept");
        detail.Flags.ShouldNotContain(f => f.Code == FlagCodes.FieldRequired && f.Field == "validFrom" && f.ResolvedAt == null);

        factory.Query(db => db.Corrections.Count(c => c.DocumentId == id)).ShouldBe(correctionsBefore + 1, "a move is one correction");
        var move = factory.Query(db => db.Corrections.AsNoTracking().OrderBy(c => c.Id).Last(c => c.DocumentId == id));
        move.Op.ShouldBe(CorrectionOps.Move);
        move.FieldPath.ShouldBe("validFrom");
        move.NewValueJson.ShouldBe("\"2026-11-01\"");
        move.Reason.ShouldBe("it is the start of validity");
        JsonNode.Parse(move.SourceJson!)!["from"]!.GetValue<string>().ShouldBe("issueDate");

        // A value that does not fit the target, and a source without a value, are refused; nothing changes.
        var misfit = await reviewer.PatchAsJsonAsync(Doc(id, "/fields"), new CorrectFieldsRequest([new FieldCorrection("currency", null, null, Op: "move", From: "validFrom")]), Ct);
        misfit.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await misfit.Content.ReadAsStringAsync(Ct)).ShouldContain("does not fit");
        (await reviewer.PatchAsJsonAsync(Doc(id, "/fields"), new CorrectFieldsRequest([new FieldCorrection("validUntil", null, null, Op: "move", From: "issueDate")]), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await reviewer.PatchAsJsonAsync(Doc(id, "/fields"), new CorrectFieldsRequest([new FieldCorrection("validUntil", V("2026-12-31"), null, From: "issueDate")]), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        factory.Query(db => db.Corrections.Count(c => c.DocumentId == id)).ShouldBe(correctionsBefore + 1);
    }

    [Fact]
    public async Task A_move_on_an_invoice_record_converts_the_value_and_keeps_both_histories()
    {
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { ReviewTask = true, Invoice = TestInvoices.Valid("RE-2026-0099") });
        using var reviewer = factory.ClientFor(new TokenSpec("reviewer-a", [Roles.Reviewer], ApiFactory.TenantA.ToString()));

        var response = await reviewer.PatchAsJsonAsync(Doc(seeded.DocumentId, "/fields"), new CorrectFieldsRequest([new FieldCorrection("notes", null, null, Op: "move", From: "invoiceNumber")]), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var invoice = CanonicalDocumentSerializer.DeserializeInvoice((await DetailOf(response)).Canonical!.Value.GetRawText());
        invoice.Notes.Value.ShouldBe("RE-2026-0099", "an identifier's verbatim text goes into a text field");
        invoice.Notes.Source!.Page.ShouldBe(1, "with the place it was read from");
        invoice.Notes.Source.Method.ShouldBe(SourceMethod.Human);
        invoice.InvoiceNumber.HasValue.ShouldBeFalse();
        invoice.InvoiceNumber.Status.ShouldBe(FieldStatus.Missing);
        invoice.InvoiceNumber.History.ShouldHaveSingleItem().Value!.Verbatim.ShouldBe("RE-2026-0099");
        factory.Query(db => db.Documents.Single(d => d.Id == seeded.DocumentId).DocumentNumber).ShouldBeNull("the list column follows");
        var move = factory.Query(db => db.Corrections.AsNoTracking().Single(c => c.DocumentId == seeded.DocumentId));
        move.Op.ShouldBe(CorrectionOps.Move);
        move.SourceJson.ShouldBe("{\"from\":\"invoiceNumber\"}");

        // A date moves between date fields of lines too; a value a field cannot take is refused.
        (await reviewer.PatchAsJsonAsync(Doc(seeded.DocumentId, "/fields"), new CorrectFieldsRequest([new FieldCorrection("deliveryDate", null, null, Op: "move", From: "dueDate")]), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await reviewer.PatchAsJsonAsync(Doc(seeded.DocumentId, "/fields"), new CorrectFieldsRequest([new FieldCorrection("lines[0].quantity", null, null, Op: "move", From: "lines[1].description")]), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await reviewer.PatchAsJsonAsync(Doc(seeded.DocumentId, "/fields"), new CorrectFieldsRequest([new FieldCorrection("supplier.vatId", null, null, Op: "move", From: "notes")]), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
