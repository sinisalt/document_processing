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

        // A move needs the field it takes the value from; a review action is not a field correction.
        var move = await Patch(reviewer, id, new FieldCorrection("lines[0].description", null, null, Op: "move"));
        move.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await move.Content.ReadAsStringAsync(Ct)).ShouldContain("'from'");
        var split = await Patch(reviewer, id, new FieldCorrection("lines", null, null, Op: "split"));
        split.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await split.Content.ReadAsStringAsync(Ct)).ShouldContain(":split");

        // docs/11 §18: the detail carries what the pipeline resolved (AppliedConfigJson), null before Extract wrote it.
        (await reviewer.GetFromJsonAsync<DocumentDetail>($"/api/v1/documents/{id}", ApiFactory.Json, Ct))!.AppliedConfig.ShouldBeNull();
        factory.Run(db => db.Documents.Single(d => d.Id == id).AppliedConfigJson = """{"typeKey":"PurchaseOrder","typeVersion":1}""");
        var applied = (await reviewer.GetFromJsonAsync<DocumentDetail>($"/api/v1/documents/{id}", ApiFactory.Json, Ct))!.AppliedConfig.ShouldNotBeNull();
        applied.GetProperty("typeKey").GetString().ShouldBe("PurchaseOrder");
    }

    /// <summary>
    /// Each <c>field.</c> filter is one more correlated EXISTS over the field index, and SQL Server's plan compile grows
    /// fast with their number, so a list request carries at most <see cref="DocumentEndpoints.MaxFieldFilters"/> of
    /// them, with one value each, for every tenant (also one that never switched <c>configurableTypes</c> on).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_list_request_with_more_field_filters_than_the_cap_is_refused(bool configurable)
    {
        var tenant = NewTenant($"Filter cap tenant {configurable}", configurable);
        using var reviewer = Reviewer(tenant);
        static string Filters(int count) => string.Join('&', Enumerable.Range(0, count).Select(i => $"field.f{i}=1"));

        (await reviewer.GetAsync($"/api/v1/documents?{Filters(DocumentEndpoints.MaxFieldFilters)}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var tooMany = await reviewer.GetAsync($"/api/v1/documents?{Filters(DocumentEndpoints.MaxFieldFilters + 1)}", Ct);
        tooMany.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = (await tooMany.Content.ReadFromJsonAsync<ValidationProblemDetails>(ApiFactory.Json, Ct))!;
        problem.Errors.Keys.ShouldBe([DocumentEndpoints.FieldFiltersErrorKey]);
        problem.Errors[DocumentEndpoints.FieldFiltersErrorKey].ShouldBe([$"At most {DocumentEndpoints.MaxFieldFilters} field filters."]);

        // Mixed case still counts as the same prefix, and a key given twice is one filter with two values: refused.
        (await reviewer.GetAsync("/api/v1/documents?field.a=1&FIELD.b=1&Field.c=1&field.d=1&field.e=1&fIeLd.f=1", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var twice = await reviewer.GetAsync("/api/v1/documents?field.poNumber=1&field.poNumber=2", Ct);
        twice.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await twice.Content.ReadFromJsonAsync<ValidationProblemDetails>(ApiFactory.Json, Ct))!.Errors["field.poNumber"].ShouldBe(["One value per field filter."]);
    }

    [Fact]
    public async Task A_delivery_note_read_before_the_switch_opens_with_the_empty_tree_of_its_type_and_keeps_its_definition_on_decisions()
    {
        // Found by the P1 demo (docs/09 "Demo: types as data"): the demo delivery note was extracted while the switch was
        // off, so it has no tree; after the switch the review screen said "no extracted data" although PATCH could fill it.
        var tenant = NewTenant("DN before switch", configurable: true);
        var id = await SeedEmptyAsync(tenant, "DeliveryNote");
        using var reviewer = Reviewer(tenant);

        var detail = (await reviewer.GetFromJsonAsync<DocumentDetail>($"/api/v1/documents/{id}", ApiFactory.Json, Ct))!;
        detail.CanonicalSchemaVersion.ShouldBe(DefinitionTree.SchemaVersion);
        var canonical = JsonNode.Parse(detail.Canonical.ShouldNotBeNull().GetRawText())!;
        canonical["deliveryNoteNumber"]!["status"]!.GetValue<string>().ShouldBe("Missing");
        canonical["lines"]!.AsArray().ShouldBeEmpty();
        detail.Definition.ShouldNotBeNull().Key.ShouldBe("DeliveryNote");
        factory.Query(db => db.Documents.Single(d => d.Id == id).CanonicalJson).ShouldBeNull("the empty tree is only what the first correction starts from; nothing is written");

        // The first correction stores the tree; a stored tree that lacks a field of its version (read under an earlier
        // one) is answered completed, as PATCH completes it.
        (await Patch(reviewer, id, new FieldCorrection("deliveryNoteNumber", V("L-2026-0457"), null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Run(db =>
        {
            var document = db.Documents.Single(d => d.Id == id);
            var stored = JsonNode.Parse(document.CanonicalJson!)!.AsObject();
            stored.Remove("carrier").ShouldBeTrue();
            document.CanonicalJson = stored.ToJsonString();
        });
        detail = (await reviewer.GetFromJsonAsync<DocumentDetail>($"/api/v1/documents/{id}", ApiFactory.Json, Ct))!;
        canonical = JsonNode.Parse(detail.Canonical!.Value.GetRawText())!;
        canonical["carrier"]!["status"]!.GetValue<string>().ShouldBe("Missing");
        canonical["deliveryNoteNumber"]!["value"]!["verbatim"]!.GetValue<string>().ShouldBe("L-2026-0457");

        // Approve, reject and reprocess answer with the definition too, so the screen that shows their answer keeps the form.
        var approved = await reviewer.PostAsJsonAsync($"/api/v1/documents/{id}:approve", new ApproveDocumentRequest(null), Ct);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
        var afterApprove = (await approved.Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!;
        afterApprove.Definition.ShouldNotBeNull().Key.ShouldBe("DeliveryNote");
        afterApprove.TypeVersion.ShouldBe(1);
    }

    [Fact]
    public async Task Without_the_switch_a_document_without_a_tree_is_answered_as_before()
    {
        var tenant = NewTenant("DN switch off", configurable: false);
        var id = await SeedEmptyAsync(tenant, "DeliveryNote");
        using var reviewer = Reviewer(tenant);

        var detail = (await reviewer.GetFromJsonAsync<DocumentDetail>($"/api/v1/documents/{id}", ApiFactory.Json, Ct))!;
        detail.Canonical.ShouldBeNull();
        detail.Definition.ShouldBeNull();
        detail.CanonicalSchemaVersion.ShouldNotBe(DefinitionTree.SchemaVersion);
        var rejected = await reviewer.PostAsJsonAsync($"/api/v1/documents/{id}:reject", new RejectDocumentRequest("not ours"), Ct);
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await rejected.Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!.Definition.ShouldBeNull();
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

    /// <summary>
    /// A row field's column is its first value (ordinal 0), read alone, not every row's; the <c>?field.</c> filter
    /// still finds a document by any value the index holds for it.
    /// </summary>
    [Fact]
    public async Task A_row_fields_column_is_its_first_value_and_the_filter_finds_any_indexed_row()
    {
        var tenant = NewTenant("Row column tenant", configurable: true);
        var id = await SeedEmptyAsync(tenant, "PurchaseOrder");
        const string key = "lines[*].supplierSku";
        factory.Run(db =>
        {
            for (var ordinal = 0; ordinal < 3; ordinal++)
            {
                db.DocumentFieldIndex.Add(new DocumentFieldIndexEntry { TenantId = tenant, DocumentId = id, FieldKey = key, Ordinal = ordinal, StringValue = $"SKU-{ordinal}" });
            }
        });
        using var reviewer = Reviewer(tenant);

        var page = (await reviewer.GetFromJsonAsync<Page<DocumentSummary>>($"/api/v1/documents?type=PurchaseOrder&fields={key}", ApiFactory.Json, Ct))!;
        page.Items.Single(d => d.Id == id).Fields.ShouldNotBeNull().ShouldBe(new Dictionary<string, string> { [key] = "SKU-0" });

        (await reviewer.GetFromJsonAsync<Page<DocumentSummary>>($"/api/v1/documents?field.{key}=sku-2", ApiFactory.Json, Ct))!.Items.ShouldHaveSingleItem().Id.ShouldBe(id);
        (await reviewer.GetFromJsonAsync<Page<DocumentSummary>>($"/api/v1/documents?field.{key}=SKU-3", ApiFactory.Json, Ct))!.Items.ShouldBeEmpty();
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
        (await append.Content.ReadAsStringAsync(Ct)).ShouldContain("does not apply to this document");

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
    public async Task An_operators_correction_never_finds_a_duplicate_in_another_tenant()
    {
        // Tenant B holds a price list; tenant A's copied type keys duplicates without the supplier.
        var tenantB = NewTenant("Dup B", configurable: true);
        using (var adminB = factory.TenantAdminOf(tenantB))
        {
            (await adminB.PostAsJsonAsync("/api/v1/document-types", new { from = new { template = "std.price-list", mode = "adopt" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var ofB = await SeedEmptyAsync(tenantB, "price-list");
        using (var reviewerB = Reviewer(tenantB))
        {
            (await Patch(reviewerB, ofB,
                new FieldCorrection("priceListNumber", V("PL-SECRET-1"), null),
                new FieldCorrection("validFrom", V("2026-11-01"), null),
                new FieldCorrection("listScope", V("Full"), null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var tenantA = NewTenant("Dup A", configurable: true);
        using var adminA = factory.TenantAdminOf(tenantA);
        var copy = await adminA.PostAsJsonAsync("/api/v1/document-types", new { from = new { template = "std.price-list", mode = "copy" } }, Ct);
        copy.IsSuccessStatusCode.ShouldBeTrue(await copy.Content.ReadAsStringAsync(Ct));
        var draftVersion = (await copy.Content.ReadFromJsonAsync<DocumentTypeDetail>(ApiFactory.Json, Ct))!.DraftVersion!.Value;
        var draft = await adminA.GetAsync($"/api/v1/document-types/price-list/versions/{draftVersion}", Ct);
        var definition = JsonNode.Parse((await draft.Content.ReadFromJsonAsync<DocumentTypeVersionDetail>(ApiFactory.Json, Ct))!.Definition.GetRawText())!.AsObject();
        definition["roles"]!["duplicateKey"] = new JsonArray("validFrom", "listScope", "priceListNumber");
        using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/document-types/price-list/versions/{draftVersion}")
        {
            Content = new StringContent(definition.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        put.Headers.IfMatch.Add(draft.Headers.ETag!);
        (await adminA.SendAsync(put, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var published = await adminA.PostAsJsonAsync($"/api/v1/document-types/price-list/versions/{draftVersion}:publish", new { changeNote = "no supplier in the key" }, Ct);
        published.StatusCode.ShouldBe(HttpStatusCode.OK, await published.Content.ReadAsStringAsync(Ct));

        var ofA = await SeedEmptyAsync(tenantA, "price-list");
        using var reviewerA = Reviewer(tenantA);
        var own = (await (await Patch(reviewerA, ofA,
            new FieldCorrection("priceListNumber", V("PL-SECRET-1"), null),
            new FieldCorrection("validFrom", V("2026-11-01"), null),
            new FieldCorrection("listScope", V("Full"), null))).Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!;
        own.Flags.ShouldNotContain(f => f.Code == FlagCodes.RevisionOf || f.Code == FlagCodes.DuplicateSuspected);

        // An operator works in platform scope (no X-Tenant-Id): the check still reads tenant A's documents only.
        using var operatorClient = factory.ClientFor(new TokenSpec("ops", [Roles.Operator]));
        var corrected = await Patch(operatorClient, ofA, new FieldCorrection("validUntil", V("2026-11-30"), "operator"));
        corrected.StatusCode.ShouldBe(HttpStatusCode.OK, await corrected.Content.ReadAsStringAsync(Ct));
        var detail = (await corrected.Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!;
        detail.Flags.ShouldNotContain(f => f.Code == FlagCodes.RevisionOf || f.Code == FlagCodes.DuplicateSuspected);
        detail.Flags.ShouldNotContain(f => f.Params.Values.Contains(ofB.ToString()));
    }

    /// <summary>Forks the tenant's invoice in place (docs/11 §5.1) and publishes the copy unchanged as version 2; returns its version id.</summary>
    private async Task<Guid> PublishInvoiceForkAsync(Guid tenant)
    {
        using var admin = factory.TenantAdminOf(tenant);
        (await admin.PostAsJsonAsync("/api/v1/document-types", new { from = new { template = "std.invoice", mode = "copy" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var published = await admin.PostAsJsonAsync("/api/v1/document-types/Invoice/versions/2:publish", new { changeNote = "fork" }, Ct);
        published.StatusCode.ShouldBe(HttpStatusCode.OK, await published.Content.ReadAsStringAsync(Ct));
        return factory.Query(db => db.DocumentTypeVersions.Single(v => v.TenantId == tenant && v.Definition!.Key == "Invoice" && v.Version == 2).Id);
    }

    [Fact]
    public async Task A_forked_invoice_takes_only_corrections_that_leave_a_tree_the_invoice_record_reads()
    {
        var tenant = NewTenant("Invoice fork", configurable: true);
        var version = await PublishInvoiceForkAsync(tenant);
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant });
        factory.Run(db =>
        {
            var document = db.Documents.Single(d => d.Id == seeded.DocumentId);
            document.DocumentTypeVersionId = version;
            document.CanonicalSchemaVersion = DefinitionTree.SchemaVersion;
        });
        string Stored() => factory.Query(db => db.Documents.Single(d => d.Id == seeded.DocumentId).CanonicalJson)!;
        int Corrections() => factory.Query(db => db.Corrections.Count(c => c.DocumentId == seeded.DocumentId));
        using var reviewer = Reviewer(tenant);

        // A new row gets its line number, and a unit given as the UN/ECE code the form offers is stored as the record's name.
        var append = await Patch(reviewer, seeded.DocumentId, new FieldCorrection("lines", J("""{"description": "Leergut", "unit": "CR"}"""), null, Op: "append"));
        append.StatusCode.ShouldBe(HttpStatusCode.OK, await append.Content.ReadAsStringAsync(Ct));
        var invoice = JsonSerializer.Deserialize<Domain.Invoices.Invoice>(Stored(), CanonicalJson.Options)!;
        invoice.Lines.Select(l => l.LineNumber).ShouldBe([1, 2, 3]);
        invoice.Lines[2].Unit.Value.ShouldBe(Domain.Common.UnitOfMeasure.Crate);
        var set = await Patch(reviewer, seeded.DocumentId, new FieldCorrection("lines[0].unit", V("kg"), null), new FieldCorrection("invoiceNumber", V("RE-2026-0099"), null));
        set.StatusCode.ShouldBe(HttpStatusCode.OK, await set.Content.ReadAsStringAsync(Ct));
        JsonNode.Parse(Stored())!["lines"]![0]!["unit"]!["value"]!.GetValue<string>().ShouldBe("Kilogram");

        // A unit the record cannot hold is a 400 that names the field, and nothing is saved.
        var before = Stored();
        var corrections = Corrections();
        var refused = await Patch(reviewer, seeded.DocumentId, new FieldCorrection("invoiceNumber", V("RE-2026-0100"), null), new FieldCorrection("lines[1].unit", V("furlong"), null));
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = (await refused.Content.ReadFromJsonAsync<ValidationProblemDetails>(ApiFactory.Json, Ct))!;
        problem.Errors["lines[1].unit"].Single().ShouldContain("is not a unit of an invoice line");
        Stored().ShouldBe(before);
        Corrections().ShouldBe(corrections);

        // The tree is still one the validate checks read: approve settles it.
        (await reviewer.PostAsJsonAsync($"/api/v1/documents/{seeded.DocumentId}:approve", new ApproveDocumentRequest("checked"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_document_moved_onto_a_version_that_renamed_a_field_keeps_the_value_under_the_new_key()
    {
        var tenant = NewTenant("Rename tenant", configurable: true);
        var id = await SeedEmptyAsync(tenant, "PurchaseOrder");
        using var reviewer = Reviewer(tenant);
        (await Patch(reviewer, id, new FieldCorrection("requestedDeliveryDate", V("2026-12-01"), null), new FieldCorrection("poNumber", V("PO-9"), null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var v1 = factory.Query(db => db.DocumentTypeVersions.Single(v => v.TenantId == tenant && v.Definition!.Key == "PurchaseOrder" && v.Version == 1).Id);
        factory.Run(db => db.Documents.Single(d => d.Id == id).DocumentTypeVersionId = v1);

        // The fork renames the field (same id, new key) and is published as version 2.
        using var admin = factory.TenantAdminOf(tenant);
        (await admin.PostAsJsonAsync("/api/v1/document-types", new { from = new { template = "std.purchase-order", mode = "copy" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var draft = JsonNode.Parse((await admin.GetFromJsonAsync<DocumentTypeVersionDetail>("/api/v1/document-types/PurchaseOrder/versions/2", ApiFactory.Json, Ct))!.Definition.GetRawText())!.AsObject();
        draft["fields"]!.AsArray().Single(f => f!["key"]!.GetValue<string>() == "requestedDeliveryDate")!["key"] = "wantedBy";
        using (var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/document-types/PurchaseOrder/versions/2") { Content = new StringContent(draft.ToJsonString(), System.Text.Encoding.UTF8, "application/json") })
        {
            put.Headers.IfMatch.Add(System.Net.Http.Headers.EntityTagHeaderValue.Parse("\"0\""));
            (await admin.SendAsync(put, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var published = await admin.PostAsJsonAsync("/api/v1/document-types/PurchaseOrder/versions/2:publish", new { changeNote = "rename" }, Ct);
        published.StatusCode.ShouldBe(HttpStatusCode.OK, await published.Content.ReadAsStringAsync(Ct));
        var v2 = factory.Query(db => db.DocumentTypeVersions.Single(v => v.TenantId == tenant && v.Definition!.Key == "PurchaseOrder" && v.Version == 2));
        v2.DescriptorsJson.ShouldNotBeNull().ShouldContain("\"formerKeys\":[\"requestedDeliveryDate\"]");

        // Still pinned to version 1 the document reads as before; moved onto version 2 (a reprocess with configMode
        // current re-pins it) the value is under the new key, and the next correction stores it there.
        var pinned = (await reviewer.GetFromJsonAsync<DocumentDetail>($"/api/v1/documents/{id}", ApiFactory.Json, Ct))!;
        JsonNode.Parse(pinned.Canonical!.Value.GetRawText())!["requestedDeliveryDate"]!["value"]!.GetValue<string>().ShouldBe("2026-12-01");
        factory.Run(db => db.Documents.Single(d => d.Id == id).DocumentTypeVersionId = v2.Id);
        var moved = (await reviewer.GetFromJsonAsync<DocumentDetail>($"/api/v1/documents/{id}", ApiFactory.Json, Ct))!;
        var tree = JsonNode.Parse(moved.Canonical!.Value.GetRawText())!.AsObject();
        tree["wantedBy"]!["value"]!.GetValue<string>().ShouldBe("2026-12-01");
        tree.ContainsKey("requestedDeliveryDate").ShouldBeFalse();
        (await Patch(reviewer, id, new FieldCorrection("orderDate", V("2026-11-20"), null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = JsonNode.Parse(factory.Query(db => db.Documents.Single(d => d.Id == id).CanonicalJson)!)!.AsObject();
        stored["wantedBy"]!["value"]!.GetValue<string>().ShouldBe("2026-12-01");
        stored.ContainsKey("requestedDeliveryDate").ShouldBeFalse();
        v1.ShouldNotBe(v2.Id);
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

    [Fact]
    public async Task Within_a_priority_the_queue_and_the_inbox_sort_by_due_time_across_pages()
    {
        var tenant = NewTenant("Queue due", configurable: false);
        var start = factory.Clock.GetUtcNow();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var seeded = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, ReviewTask = true, CreatedAt = start.AddMinutes(i) });
            ids.Add(seeded.DocumentId);
        }

        var settled = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, Status = DocProc.Domain.Documents.DocumentStatus.Approved, CreatedAt = start.AddMinutes(10) });
        factory.Run(db =>
        {
            // Two urgent specials lists: the younger one is due first. A normal task with a due time comes before the
            // normal ones without, which keep their age order.
            var older = db.ReviewTasks.Single(t => t.DocumentId == ids[0]);
            older.Priority = ReviewPriority.Urgent;
            older.SlaDueAt = start.AddHours(8);
            var younger = db.ReviewTasks.Single(t => t.DocumentId == ids[1]);
            younger.Priority = ReviewPriority.Urgent;
            younger.SlaDueAt = start.AddHours(2);
            db.ReviewTasks.Single(t => t.DocumentId == ids[4]).SlaDueAt = start.AddHours(30);
            db.ReviewTasks.Single(t => t.DocumentId == ids[2]).SlaDueAt = null;
            db.ReviewTasks.Single(t => t.DocumentId == ids[3]).SlaDueAt = null;
        });
        using var reviewer = Reviewer(tenant);

        var queue = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = (await reviewer.GetFromJsonAsync<Page<ReviewTaskDto>>("/api/v1/review-tasks?limit=1" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)), ApiFactory.Json, Ct))!;
            queue.AddRange(page.Items.Select(t => t.DocumentId));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        queue.ShouldBe([ids[1], ids[0], ids[4], ids[2], ids[3]], "priority, then the due time (none last), then age (docs/11 §6.2)");

        // The inbox's "Urgent first" is the server's order across every page, not only the loaded one; documents
        // without a review task come last, newest first.
        var inbox = new List<Guid>();
        cursor = null;
        do
        {
            var page = (await reviewer.GetFromJsonAsync<Page<DocumentSummary>>("/api/v1/documents?sort=reviewDue&limit=2" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)), ApiFactory.Json, Ct))!;
            inbox.AddRange(page.Items.Select(d => d.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        inbox.ShouldBe([ids[1], ids[0], ids[4], ids[3], ids[2], settled.DocumentId], "within a priority and no due time the inbox keeps its newest-first order");
        (await reviewer.GetAsync("/api/v1/documents?sort=total", Ct)).StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        var first = (await reviewer.GetFromJsonAsync<Page<DocumentSummary>>("/api/v1/documents?sort=reviewDue&limit=1", ApiFactory.Json, Ct))!.Items.Single();
        first.ReviewPriority.ShouldBe(ReviewPriority.Urgent);
        first.ReviewDueAt.ShouldBe(start.AddHours(2));
    }
}
