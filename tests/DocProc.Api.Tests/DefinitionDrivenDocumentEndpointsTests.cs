using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Api.Endpoints;
using DocProc.Application.Abstractions;
using DocProc.Application.Documents;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Catalogue;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Mvc;

namespace DocProc.Api.Tests;

/// <summary>
/// Definition-driven documents through the API (docs/11 §5.6, §12.8, §18): <c>PATCH /fields</c> with the descriptor
/// patcher (set, append, remove) and inline validation, the type version in <c>DocumentDetail.definition</c>, the field
/// index behind <c>?field.&lt;key&gt;=</c>, the validity and row-count columns, and the review queue by priority. A
/// tenant without <c>configurableTypes</c> and an adopted invoice keep today's behaviour.
/// </summary>
public sealed class DefinitionDrivenDocumentEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement V(object? value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private Guid NewTenant(string name, bool configurable)
    {
        var tenant = factory.CreateTenant(name);
        using (var db = factory.PlatformDb())
        {
            TypeCatalogueSeeder.AddMissingAdoptedTypesAsync(db, tenant, factory.Clock.GetUtcNow(), Ct).GetAwaiter().GetResult();
            db.SaveChanges();
        }

        factory.Run(db => db.Tenants.Single(t => t.Id == tenant).Settings.Features!.ConfigurableTypes = configurable);
        return tenant;
    }

    private HttpClient Reviewer(Guid tenant) => factory.ClientFor(new TokenSpec("rev-" + tenant.ToString("N")[..8], [Roles.Reviewer], tenant.ToString()));

    /// <summary>A document of <paramref name="type"/> without canonical data yet, as the Extract stage leaves a purchase order today.</summary>
    private async Task<Guid> SeedEmptyAsync(Guid tenant, string type, string supplier = "Nordwind Großhandel GmbH")
    {
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, Type = DocumentTypeKey.Parse(type), Invoice = null, SupplierName = supplier, ReviewTask = true });
        factory.Run(db =>
        {
            var document = db.Documents.Single(d => d.Id == seeded.DocumentId);
            document.CanonicalJson = null;
            document.DocumentNumber = null;
            document.DocumentNumberNormalized = null;
        });
        return seeded.DocumentId;
    }

    private static Task<HttpResponseMessage> Patch(HttpClient client, Guid id, params FieldCorrection[] corrections) =>
        client.PatchAsJsonAsync($"/api/v1/documents/{id}/fields", new CorrectFieldsRequest(corrections), Ct);

    [Fact]
    public async Task A_purchase_order_is_filled_in_review_by_its_definition_and_found_by_its_indexed_number()
    {
        var tenant = NewTenant("PO tenant", configurable: true);
        var id = await SeedEmptyAsync(tenant, "PurchaseOrder");
        using var reviewer = Reviewer(tenant);

        var response = await Patch(reviewer, id,
            new FieldCorrection("poNumber", V("PO-2026-77"), "typed from the page"),
            new FieldCorrection("orderDate", V("2026-10-29"), null),
            new FieldCorrection("currency", V("EUR"), null),
            new FieldCorrection("lines", J("""{"description":"Kaffee Crema 1kg","quantityOrdered":12,"unitPrice":14.9}"""), null, Op: "append"),
            new FieldCorrection("lines", J("""{"description":"Espresso"}"""), null, Op: "append"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var detail = (await response.Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!;
        detail.CanonicalSchemaVersion.ShouldBe(DefinitionTree.SchemaVersion);
        detail.DocumentNumber.ShouldBe("PO-2026-77", "the role documentNumber is bound to poNumber");
        detail.LineCount.ShouldBe(2);
        detail.TypeName.ShouldBe("Purchase order");
        detail.TypeVersion.ShouldBe(1);
        var definition = detail.Definition.ShouldNotBeNull();
        definition.Key.ShouldBe("PurchaseOrder");
        definition.VersionId.ShouldNotBeNull();
        definition.FormSpec.GetRawText().ShouldContain("poNumber");
        var canonical = JsonNode.Parse(detail.Canonical!.Value.GetRawText())!;
        canonical["lines"]![1]!["description"]!["value"]!.GetValue<string>().ShouldBe("Espresso");
        canonical["requestedDeliveryDate"]!["status"]!.GetValue<string>().ShouldBe("Missing", "every field of the type is in the tree");

        // Remove the second row again: one correction row per change, with its op.
        (await Patch(reviewer, id, new FieldCorrection("lines[1]", null, "not ordered", Op: "remove"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Query(db => db.Corrections.Where(c => c.DocumentId == id).Select(c => c.Op).ToList())
            .ShouldBe(["set", "set", "set", "append", "append", "remove"], ignoreOrder: true);
        factory.Query(db => db.Documents.Single(d => d.Id == id).LineCount).ShouldBe(1);

        // The indexed field is searchable (normalised), alone or with the other filters.
        var found = (await reviewer.GetFromJsonAsync<Page<DocumentSummary>>("/api/v1/documents?field.poNumber=po-2026-77", ApiFactory.Json, Ct))!;
        found.Items.Select(d => d.Id).ShouldBe([id]);
        found.Items[0].LineCount.ShouldBe(1);
        (await reviewer.GetFromJsonAsync<Page<DocumentSummary>>("/api/v1/documents?field.poNumber=PO-2026-78", ApiFactory.Json, Ct))!.Items.ShouldBeEmpty();
        (await reviewer.GetFromJsonAsync<Page<DocumentSummary>>("/api/v1/documents?type=PurchaseOrder&field.poNumber=PO-2026-77", ApiFactory.Json, Ct))!.Items.Count.ShouldBe(1);
        (await reviewer.GetFromJsonAsync<Page<DocumentSummary>>("/api/v1/documents?type=Invoice&field.poNumber=PO-2026-77", ApiFactory.Json, Ct))!.Items.ShouldBeEmpty();
        factory.Query(db => db.DocumentFieldIndex.Where(e => e.DocumentId == id).Select(e => new { e.FieldKey, e.StringValue }).ToList())
            .ShouldHaveSingleItem().StringValue.ShouldBe("PO-2026-77");

        var bad = await reviewer.GetAsync("/api/v1/documents?field.po%20Number=1", Ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await bad.Content.ReadAsStringAsync(Ct)).ShouldContain("field.po Number");
        (await reviewer.GetAsync("/api/v1/documents?field.poNumber=", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Operations of later phases are refused with the P0 message.
        var move = await Patch(reviewer, id, new FieldCorrection("lines[0]", null, null, Op: "move"));
        move.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await move.Content.ReadAsStringAsync(Ct)).ShouldContain("not available yet");
    }

    [Fact]
    public async Task The_inbox_gets_indexed_values_as_columns_and_the_type_list_names_the_indexed_fields()
    {
        var tenant = NewTenant("Columns tenant", configurable: true);
        var id = await SeedEmptyAsync(tenant, "PurchaseOrder");
        using var reviewer = Reviewer(tenant);
        (await Patch(reviewer, id, new FieldCorrection("poNumber", V("PO-2026-91"), null))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The inbox offers a type's indexed fields as filters and columns (docs/11 §19): the type list names them.
        var types = (await reviewer.GetFromJsonAsync<List<DocumentTypeDetail>>("/api/v1/document-types", ApiFactory.Json, Ct))!;
        var po = types.Single(t => t.Key == "PurchaseOrder");
        var indexed = po.IndexedFields.ShouldHaveSingleItem();
        indexed.Path.ShouldBe("poNumber");
        indexed.Kind.ShouldBe("identifier");
        indexed.Label.ShouldNotBeNull()["en"].ShouldNotBeNullOrWhiteSpace();
        types.Single(t => t.Key == "Invoice").IndexedFields.Select(f => f.Path).ShouldBe(["invoiceNumber"]);

        // fields= returns the asked-for values as the index stores them; without it the rows carry none, as before.
        var page = (await reviewer.GetFromJsonAsync<Page<DocumentSummary>>("/api/v1/documents?type=PurchaseOrder&fields=poNumber,lines[*].supplierSku", ApiFactory.Json, Ct))!;
        page.Items.Single(d => d.Id == id).Fields.ShouldNotBeNull().ShouldBe(new Dictionary<string, string> { ["poNumber"] = "PO-2026-91" });
        (await reviewer.GetFromJsonAsync<Page<DocumentSummary>>("/api/v1/documents?type=PurchaseOrder", ApiFactory.Json, Ct))!.Items.Single(d => d.Id == id).Fields.ShouldBeNull();

        var bad = await reviewer.GetAsync("/api/v1/documents?fields=po%20Number", Ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await bad.Content.ReadAsStringAsync(Ct)).ShouldContain("po Number");
        var many = string.Join(',', Enumerable.Range(0, DocumentEndpoints.MaxFieldColumns + 1).Select(i => $"f{i}"));
        (await reviewer.GetAsync($"/api/v1/documents?fields={many}", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Without_configurable_types_a_purchase_order_has_nothing_to_correct_as_before()
    {
        var tenant = NewTenant("PO tenant off", configurable: false);
        var id = await SeedEmptyAsync(tenant, "PurchaseOrder");
        using var reviewer = Reviewer(tenant);

        var response = await Patch(reviewer, id, new FieldCorrection("poNumber", V("PO-1"), null));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Title.ShouldBe("Nothing to correct");
        var detail = (await reviewer.GetFromJsonAsync<DocumentDetail>($"/api/v1/documents/{id}", ApiFactory.Json, Ct))!;
        detail.Definition.ShouldBeNull("a tenant that is not on the feature gets the payload it got before");
        detail.TypeName.ShouldBeNull();
    }

    [Fact]
    public async Task An_adopted_invoice_keeps_the_coded_patcher_even_with_configurable_types_on()
    {
        var tenant = NewTenant("Invoice tenant", configurable: true);
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant });
        using var reviewer = Reviewer(tenant);

        var append = await Patch(reviewer, seeded.DocumentId, new FieldCorrection("lines", J("{}"), null, Op: "append"));
        append.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await append.Content.ReadAsStringAsync(Ct)).ShouldContain("not available yet");

        var set = await Patch(reviewer, seeded.DocumentId, new FieldCorrection("invoiceNumber", V("RE-2026-0100"), null));
        set.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = (await set.Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!;
        detail.CanonicalSchemaVersion.ShouldBe(1, "the adopted invoice stays a legacy tree");
        detail.Definition.ShouldNotBeNull().Key.ShouldBe("Invoice", "with the switch on the detail names the type version");
        factory.Query(db => db.DocumentFieldIndex.Single(e => e.DocumentId == seeded.DocumentId && e.FieldKey == "invoiceNumber").StringValue).ShouldBe("RE-2026-0100");
    }

    [Fact]
    public async Task A_price_list_is_validated_inline_and_a_second_list_with_the_same_key_is_a_revision()
    {
        var tenant = NewTenant("Price lists", configurable: true);
        using var admin = factory.TenantAdminOf(tenant);
        (await admin.PostAsJsonAsync("/api/v1/document-types", new { from = new { template = "std.price-list", mode = "adopt" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        using var reviewer = Reviewer(tenant);
        var first = await SeedEmptyAsync(tenant, "price-list");

        // The first correction creates the tree; validate.schema raises FIELD_REQUIRED for what is still missing.
        var started = (await (await Patch(reviewer, first, new FieldCorrection("priceListNumber", V("PL-2026-11"), null))).Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!;
        started.Flags.Where(f => f.Code == FlagCodes.FieldRequired).Select(f => f.Field).ShouldBe(["validFrom", "currency", "supplier"], ignoreOrder: true, string.Join("; ", started.Flags.Select(f => $"{f.Code} {f.Field} {f.Message}")) + " v" + started.CanonicalSchemaVersion);
        started.Flags.ShouldAllBe(f => f.Code != FlagCodes.FieldRequired || f.Severity == FlagSeverity.Blocking);

        var filled = (await (await Patch(reviewer, first,
            new FieldCorrection("validFrom", V("2026-11-01"), null),
            new FieldCorrection("validUntil", V("2026-11-30"), null),
            new FieldCorrection("currency", V("EUR"), null),
            new FieldCorrection("listScope", V("Full"), null),
            new FieldCorrection("supplier", J("""{"name":"Nordwind Großhandel GmbH"}"""), null),
            new FieldCorrection("lines", J("""{"supplierSku":"4711","description":"Kaffee","price":-1,"unit":"Krt"}"""), null, Op: "append"))).Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!;
        filled.Flags.ShouldNotContain(f => f.Code == FlagCodes.FieldRequired);
        filled.ValidFrom.ShouldBe(new DateOnly(2026, 11, 1));
        filled.ValidUntil.ShouldBe(new DateOnly(2026, 11, 30));
        filled.LineCount.ShouldBe(1);
        var price = filled.Flags.Single(f => f.Params["ruleId"] == "r_price_pos");
        (price.Code, price.Severity, price.Field).ShouldBe((FlagCodes.RuleFailed, FlagSeverity.Blocking, "lines[0].price.amount"));
        price.Params["typeKey"].ShouldBe("price-list");
        filled.Flags.Single(f => f.Params.GetValueOrDefault("ruleId") == "r_unit").Field.ShouldBe("lines[0].unit", "Krt is no uom code or alias");
        filled.Flags.ShouldNotContain(f => f.Code == FlagCodes.RevisionOf);

        // A second list of the same supplier, validity and number is a revision of the first (onMatch: revision).
        var second = await SeedEmptyAsync(tenant, "price-list");
        var revision = (await (await Patch(reviewer, second,
            new FieldCorrection("priceListNumber", V("pl-2026-11"), null),
            new FieldCorrection("validFrom", V("2026-11-01"), null),
            new FieldCorrection("listScope", V("Full"), null))).Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!;
        var flag = revision.Flags.Single(f => f.Code == FlagCodes.RevisionOf);
        flag.Severity.ShouldBe(FlagSeverity.Warning);
        flag.Params["documentId"].ShouldBe(first.ToString());
        flag.Field.ShouldBe("priceListNumber");

        // Another validity date is another list.
        var other = (await (await Patch(reviewer, second, new FieldCorrection("validFrom", V("2026-12-01"), null))).Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!;
        other.Flags.ShouldNotContain(f => f.Code == FlagCodes.RevisionOf);

        var listed = (await reviewer.GetFromJsonAsync<Page<DocumentSummary>>("/api/v1/documents?type=price-list&field.validFrom=2026-11-01", ApiFactory.Json, Ct))!;
        listed.Items.Select(d => d.Id).ShouldBe([first]);
        listed.Items[0].ValidUntil.ShouldBe(new DateOnly(2026, 11, 30));
        listed.Items[0].ReviewPriority.ShouldBe(ReviewPriority.Normal);
    }

    [Fact]
    public async Task Reprocess_keeps_the_pinned_type_version_unless_config_mode_is_current()
    {
        var tenant = NewTenant("Reprocess modes", configurable: true);
        var version = factory.Query(db => db.DocumentTypeVersions.Single(v => v.TenantId == tenant && v.Definition!.Key == "Invoice").Id);
        var pinned = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant });
        var current = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, Invoice = TestInvoices.Valid("RE-2026-0043") });
        foreach (var id in new[] { pinned.DocumentId, current.DocumentId })
        {
            factory.Run(db => db.Documents.Single(d => d.Id == id).DocumentTypeVersionId = version);
            factory.RecordPipelineProgress(id, DocumentStatus.Validated);
        }

        using var reviewer = Reviewer(tenant);
        (await reviewer.PostAsJsonAsync($"/api/v1/documents/{pinned.DocumentId}:reprocess", new ReprocessDocumentRequest("Extract"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await reviewer.PostAsJsonAsync($"/api/v1/documents/{current.DocumentId}:reprocess", new ReprocessDocumentRequest("Extract", "current"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var invalid = await reviewer.PostAsJsonAsync($"/api/v1/documents/{current.DocumentId}:reprocess", new ReprocessDocumentRequest("Extract", "latest"), Ct);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await invalid.Content.ReadAsStringAsync(Ct)).ShouldContain("configMode");

        factory.Query(db => db.Documents.Single(d => d.Id == pinned.DocumentId).DocumentTypeVersionId).ShouldBe(version, "the default reruns with the pinned version");
        factory.Query(db => db.Documents.Single(d => d.Id == current.DocumentId).DocumentTypeVersionId).ShouldBeNull("the Extract stage pins the tenant's published version again");
        factory.Query(db => db.DocumentEvents.Where(e => e.DocumentId == current.DocumentId && e.EventType == DocumentEventTypes.Reprocessed).Select(e => e.DetailsJson).Single()).ShouldNotBeNull().ShouldContain("\"configMode\":\"current\"");
    }

    [Fact]
    public async Task The_review_queue_puts_urgent_and_high_tasks_first_and_pages_through_them()
    {
        var tenant = NewTenant("Queue", configurable: false);
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var seeded = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, ReviewTask = true, CreatedAt = factory.Clock.GetUtcNow().AddMinutes(i) });
            ids.Add(seeded.DocumentId);
        }

        factory.Run(db =>
        {
            db.ReviewTasks.Single(t => t.DocumentId == ids[2]).Priority = ReviewPriority.Urgent;
            db.ReviewTasks.Single(t => t.DocumentId == ids[3]).Priority = ReviewPriority.High;
        });
        using var reviewer = Reviewer(tenant);

        var order = new List<(Guid, ReviewPriority)>();
        string? cursor = null;
        do
        {
            var page = (await reviewer.GetFromJsonAsync<Page<ReviewTaskDto>>("/api/v1/review-tasks?limit=1" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)), ApiFactory.Json, Ct))!;
            order.AddRange(page.Items.Select(t => (t.DocumentId, t.Priority)));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        order.ShouldBe([(ids[2], ReviewPriority.Urgent), (ids[3], ReviewPriority.High), (ids[0], ReviewPriority.Normal), (ids[1], ReviewPriority.Normal)]);
        var summary = (await reviewer.GetFromJsonAsync<Page<DocumentSummary>>("/api/v1/documents", ApiFactory.Json, Ct))!.Items.Single(d => d.Id == ids[2]);
        summary.ReviewPriority.ShouldBe(ReviewPriority.Urgent);
    }
}
