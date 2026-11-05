using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Text;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Mvc;

namespace DocProc.Api.Tests;

/// <summary>
/// The tenant fallback identification rules API (docs/11 §6.6, §8.5, §18; P5): created by a <c>ConfigAdmin</c> by hand or
/// from a document in review, checked in tenant scope (no <c>supplierValue</c>), tested at save against the tenant's
/// approved documents without the suppliers that have layouts ("would have matched N documents of this type, M others"),
/// Draft edits with If-Match, activation refused while other types matched unless confirmed, retiring the previous
/// version, renaming, disabling, the <c>supplierLayouts</c> switch, roles and tenant isolation.
/// </summary>
public sealed class TypeIdentificationRuleEndpointsTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions TextLayerJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() }, IgnoreReadOnlyProperties = true };

    private readonly ApiFactory _factory;

    public TypeIdentificationRuleEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.Run(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantA).Settings.Features!.SupplierLayouts = true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Admin(Guid? tenant = null) => _factory.ClientFor(new TokenSpec("tenant-admin", [Roles.TenantAdmin], (tenant ?? ApiFactory.TenantA).ToString()));

    private HttpClient Reviewer() => _factory.ClientFor(new TokenSpec("tenant-a-reviewer", [Roles.Reviewer], ApiFactory.TenantA.ToString()));

    private static string Layer(params (double Y, string Text)[] lines)
    {
        var words = new List<PositionedWord>();
        foreach (var (y, text) in lines)
        {
            var x = 60.0;
            foreach (var word in text.Split(' '))
            {
                words.Add(new PositionedWord(word, new BoundingBox(x, y, x + (word.Length * 6), y + 10)));
                x += (word.Length * 6) + 4;
            }
        }

        return JsonSerializer.Serialize(new TextLayer(TextLayerSource.PdfText, [new TextPage(1, 1000, 1000, words)]), TextLayerJson);
    }

    private static JsonElement Rule(string json) => JsonDocument.Parse(json).RootElement;

    private static string TitleRule(string title) => $$"""{ "all": [ { "text": "{{title}}", "page": 1, "region": "title" } ] }""";

    private async Task<Guid> SeedAsync(string title, DocumentTypeKey type, DocumentStatus status = DocumentStatus.Approved, string? supplier = "METRO Deutschland GmbH")
    {
        var seeded = await _factory.SeedDocumentAsync(new DocumentSeed { Status = status, Type = type, SupplierName = supplier, TextLayerJson = Layer((40, "Kopfzeile"), (150, title), (400, "Text")) });
        return seeded.DocumentId;
    }

    private static async Task<TypeIdentificationRuleResponse> ReadAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<TypeIdentificationRuleResponse>(ApiFactory.Json, Ct))!;
    }

    [Fact]
    public async Task A_rule_is_tested_at_save_and_activated_only_when_it_matched_no_other_type_or_that_was_confirmed()
    {
        var title = $"Sammelrechnung{Guid.NewGuid():N}"[..24];
        await SeedAsync(title, DocumentTypeKey.Invoice);
        await SeedAsync(title, DocumentTypeKey.Invoice);
        await SeedAsync(title, DocumentTypeKey.CreditNote);
        await SeedAsync(title, DocumentTypeKey.Invoice, status: DocumentStatus.NeedsReview); // not approved: not tested
        using var admin = Admin();

        var created = await ReadAsync(await admin.PostAsJsonAsync("/api/v1/document-types/Invoice/identification-rules", new CreateTypeIdentificationRuleRequest("Sammelrechnung im Titel", Rule(TitleRule(title))), Ct), HttpStatusCode.Created);
        created.DocumentTypeKey.ShouldBe("Invoice");
        created.Status.ShouldBe("Active");
        created.ActiveVersion.ShouldBeNull();
        var draft = created.Versions.ShouldHaveSingleItem();
        draft.State.ShouldBe("Draft");
        draft.Describe.ShouldContain(title);
        var test = draft.Test.ShouldNotBeNull();
        test.SameType.ShouldBe(2);
        test.OtherTypes.ShouldBe(1);
        test.ByType["CreditNote"].ShouldBe(1);
        test.Summary.ShouldStartWith("would have matched 2 Invoice documents, 1 other document");
        test.OtherTypeMatches.ShouldHaveSingleItem().TypeKey.ShouldBe("CreditNote");

        // Matched another type: activation needs the confirmation.
        var refused = await admin.PostAsJsonAsync($"/api/v1/identification-rules/{created.Id}/versions/1:activate", new ActivateTypeIdentificationRuleRequest(), Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("1 other document");
        var active = await ReadAsync(await admin.PostAsJsonAsync($"/api/v1/identification-rules/{created.Id}/versions/1:activate", new ActivateTypeIdentificationRuleRequest(ConfirmOtherTypes: true), Ct), HttpStatusCode.OK);
        active.ActiveVersion.ShouldBe(1);
        active.Versions.Single().State.ShouldBe("Active");
        active.Versions.Single().ApprovedBy.ShouldNotBeNull();

        // A new version is a Draft, edited with If-Match; activating it retires version 1.
        var v2 = await admin.PostAsJsonAsync($"/api/v1/identification-rules/{created.Id}/versions", new TypeIdentificationRuleVersionRequest(Rule($$"""{ "all": [ { "text": "{{title}}" } ], "none": [ { "text": "Gutschrift" } ] }""")), Ct);
        v2.StatusCode.ShouldBe(HttpStatusCode.Created, await v2.Content.ReadAsStringAsync(Ct));
        (await admin.PostAsJsonAsync($"/api/v1/identification-rules/{created.Id}/versions", new TypeIdentificationRuleVersionRequest(Rule(TitleRule(title))), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "one Draft at a time");
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/identification-rules/{created.Id}/versions/2") { Content = JsonContent.Create(new TypeIdentificationRuleVersionRequest(Rule(TitleRule(title)))) };
        (await admin.SendAsync(put, Ct)).StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
        var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/identification-rules/{created.Id}/versions/2") { Content = JsonContent.Create(new TypeIdentificationRuleVersionRequest(Rule(TitleRule(title)))) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"7\"");
        (await admin.SendAsync(stale, Ct)).StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        var ok = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/identification-rules/{created.Id}/versions/2") { Content = JsonContent.Create(new TypeIdentificationRuleVersionRequest(Rule(TitleRule(title)))) };
        ok.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        var saved = await admin.SendAsync(ok, Ct);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
        (await saved.Content.ReadFromJsonAsync<TypeIdentificationRuleVersionResponse>(ApiFactory.Json, Ct))!.Revision.ShouldBe(2);
        var put1 = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/identification-rules/{created.Id}/versions/1") { Content = JsonContent.Create(new TypeIdentificationRuleVersionRequest(Rule(TitleRule(title)))) };
        put1.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        (await admin.SendAsync(put1, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "a version out of Draft never changes");

        var second = await ReadAsync(await admin.PostAsJsonAsync($"/api/v1/identification-rules/{created.Id}/versions/2:activate", new ActivateTypeIdentificationRuleRequest(ConfirmOtherTypes: true), Ct), HttpStatusCode.OK);
        second.ActiveVersion.ShouldBe(2);
        second.Versions.Select(v => v.State).ShouldBe(["Retired", "Active"]);

        // Rename, priority, disable, list by type.
        var patched = await ReadAsync(await admin.PatchAsJsonAsync($"/api/v1/identification-rules/{created.Id}", new PatchTypeIdentificationRuleRequest("Umbenannt", 120), Ct), HttpStatusCode.OK);
        patched.Name.ShouldBe("Umbenannt");
        patched.Priority.ShouldBe(120);
        (await ReadAsync(await admin.PostAsync($"/api/v1/identification-rules/{created.Id}:disable", null, Ct), HttpStatusCode.OK)).Status.ShouldBe("Disabled");
        using var reviewer = Reviewer();
        var listed = await reviewer.GetFromJsonAsync<List<TypeIdentificationRuleResponse>>("/api/v1/document-types/Invoice/identification-rules", ApiFactory.Json, Ct);
        listed!.ShouldContain(r => r.Id == created.Id);
    }

    [Fact]
    public async Task The_test_leaves_out_suppliers_that_have_layouts_and_excluded_suppliers()
    {
        var title = $"Lieferliste{Guid.NewGuid():N}"[..20];
        var withLayouts = $"Layouts {Guid.NewGuid():N}";
        var excluded = $"Excluded {Guid.NewGuid():N}";
        await SeedAsync(title, DocumentTypeKey.Invoice);
        await SeedAsync(title, DocumentTypeKey.Invoice, supplier: withLayouts);
        await SeedAsync(title, DocumentTypeKey.Invoice, supplier: excluded);
        await SeedAsync(title, DocumentTypeKey.Invoice, supplier: null);
        Guid excludedId = default;
        _factory.Run(db =>
        {
            var owner = db.Suppliers.Single(s => s.Name == withLayouts);
            db.SupplierLayouts.Add(new SupplierLayout { Id = Guid.CreateVersion7(), TenantId = ApiFactory.TenantA, Scope = SupplierLayoutScope.Supplier, SupplierId = owner.Id, OwnerId = owner.Id, DocumentTypeKey = "Invoice", LayoutKey = "own-layout", Name = "Own", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            excludedId = db.Suppliers.Single(s => s.Name == excluded).Id;
        });
        using var admin = Admin();

        var created = await ReadAsync(await admin.PostAsJsonAsync("/api/v1/document-types/Invoice/identification-rules", new CreateTypeIdentificationRuleRequest("Lieferliste", Rule(TitleRule(title)), ExcludedSupplierIds: [excludedId]), Ct), HttpStatusCode.Created);

        var test = created.Versions.Single().Test!;
        test.Matched.ShouldBe(2, "the plain supplier's document and the one without a supplier");
        created.Versions.Single().ExcludedSupplierIds.ShouldBe([excludedId]);
        (await ReadAsync(await admin.PostAsJsonAsync($"/api/v1/identification-rules/{created.Id}/versions/1:activate", new ActivateTypeIdentificationRuleRequest(), Ct), HttpStatusCode.OK)).ActiveVersion.ShouldBe(1, "no other type matched: no confirmation needed");

        var retested = await admin.PostAsync($"/api/v1/identification-rules/{created.Id}/versions/1:test", null, Ct);
        retested.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await retested.Content.ReadFromJsonAsync<TypeIdentificationRuleVersionResponse>(ApiFactory.Json, Ct))!.Test!.Matched.ShouldBe(2);
    }

    [Fact]
    public async Task Rules_are_checked_in_tenant_scope_and_need_a_known_type()
    {
        using var admin = Admin();
        async Task<string> Refused(string path, object body)
        {
            var response = await admin.PostAsJsonAsync(path, body, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            return await response.Content.ReadAsStringAsync(Ct);
        }

        (await Refused("/api/v1/document-types/Invoice/identification-rules", new CreateTypeIdentificationRuleRequest("VAT", Rule("""{ "all": [ { "text": "Rechnung" }, { "supplierValue": "vatId" } ] }""")))).ShouldContain("cannot name a supplier value");
        (await Refused("/api/v1/document-types/Invoice/identification-rules", new CreateTypeIdentificationRuleRequest("None only", Rule("""{ "none": [ { "text": "Rechnung" } ] }""")))).ShouldContain("positive");
        (await Refused("/api/v1/document-types/no-such-type/identification-rules", new CreateTypeIdentificationRuleRequest("X", Rule(TitleRule("X"))))).ShouldContain("no published type");
        (await Refused("/api/v1/document-types/Invoice/identification-rules", new CreateTypeIdentificationRuleRequest("", Rule(TitleRule("X"))))).ShouldContain("name");
        (await Refused("/api/v1/document-types/Invoice/identification-rules", new CreateTypeIdentificationRuleRequest("X", Rule(TitleRule("X")), Priority: 5000))).ShouldContain("priority");
    }

    [Fact]
    public async Task Use_for_all_suppliers_without_own_layouts_saves_a_draft_from_the_document_that_must_hold_on_it()
    {
        var title = $"Preisliste{Guid.NewGuid():N}"[..20];
        var document = await SeedAsync(title, DocumentTypeKey.Invoice, status: DocumentStatus.NeedsReview);
        using var admin = Admin();

        var created = await ReadAsync(await admin.PostAsJsonAsync($"/api/v1/documents/{document}/identification-rules", new CreateTypeIdentificationRuleFromDocumentRequest(Rule(TitleRule(title))), Ct), HttpStatusCode.Created);
        created.CreatedFromDocumentId.ShouldBe(document);
        created.DocumentTypeKey.ShouldBe("Invoice");
        created.Name.ShouldContain(title);
        created.Versions.Single().State.ShouldBe("Draft");

        var notHolding = await admin.PostAsJsonAsync($"/api/v1/documents/{document}/identification-rules", new CreateTypeIdentificationRuleFromDocumentRequest(Rule(TitleRule("Lieferschein"))), Ct);
        notHolding.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await notHolding.Content.ReadAsStringAsync(Ct)).ShouldContain("does not hold on this document");

        // A reviewer without ConfigAdmin cannot.
        using var reviewer = Reviewer();
        (await reviewer.PostAsJsonAsync($"/api/v1/documents/{document}/identification-rules", new CreateTypeIdentificationRuleFromDocumentRequest(Rule(TitleRule(title))), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Without_supplierLayouts_the_routes_are_forbidden_and_another_tenant_sees_nothing()
    {
        using var b = Admin(ApiFactory.TenantB);
        var response = await b.PostAsJsonAsync("/api/v1/document-types/Invoice/identification-rules", new CreateTypeIdentificationRuleRequest("X", Rule(TitleRule("X"))), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Detail!.ShouldContain("supplierLayouts");

        using var a = Admin();
        var created = await ReadAsync(await a.PostAsJsonAsync("/api/v1/document-types/Invoice/identification-rules", new CreateTypeIdentificationRuleRequest("Tenant A", Rule(TitleRule($"Nur{Guid.NewGuid():N}"[..12]))), Ct), HttpStatusCode.Created);
        _factory.Run(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantB).Settings.Features!.SupplierLayouts = true);
        try
        {
            (await b.GetAsync($"/api/v1/identification-rules/{created.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await b.GetFromJsonAsync<List<TypeIdentificationRuleResponse>>("/api/v1/document-types/Invoice/identification-rules", ApiFactory.Json, Ct))!.ShouldNotContain(r => r.Id == created.Id);
        }
        finally
        {
            _factory.Run(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantB).Settings.Features!.SupplierLayouts = false);
        }
    }
}
