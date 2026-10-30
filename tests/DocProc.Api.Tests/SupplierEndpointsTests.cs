using System.Net;
using System.Net.Http.Json;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;
using DocProc.Domain.Common;
using DocProc.Domain.Documents;

namespace DocProc.Api.Tests;

public sealed class SupplierEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Uri SuppliersUri = new("/api/v1/suppliers", UriKind.Relative);
    private static readonly string TenantA = ApiFactory.TenantA.ToString();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient TenantAdminClient() => factory.ClientFor(new TokenSpec("tenant-a-admin", [Roles.TenantAdmin], TenantA));

    private static Uri SupplierUri(Guid id) => new($"/api/v1/suppliers/{id}", UriKind.Relative);

    private static CreateSupplierRequest NewSupplier(string name) => new(name, null, null, null, null, null, null, null);

    [Fact]
    public async Task Create_folds_the_name_exactly_like_the_domain_and_round_trips_every_field()
    {
        using var admin = TenantAdminClient();
        var request = new CreateSupplierRequest("Getränke Hoffmann", "de 123 456 789", "12/345/67890", ["DE89 3704 0044 0532 0130 00"], ["Hoffmann.de", ""], ["Hoffmann", "Hoffmann"], "EUR", true);

        var response = await admin.PostAsJsonAsync(SuppliersUri, request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<SupplierResponse>(Ct))!;
        created.NormalizedName.ShouldBe(Identifiers.NormalizeName("Getränke Hoffmann"));
        created.NormalizedName.ShouldBe("GETRANKE HOFFMANN");
        created.VatId.ShouldBe("DE123456789");
        created.TaxId.ShouldBe("12/345/67890");
        created.Ibans.ShouldBe(["DE89370400440532013000"]);
        created.EmailDomains.ShouldBe(["hoffmann.de"]);
        created.Aliases.ShouldBe(["Hoffmann"]);
        created.DefaultCurrency.ShouldBe("EUR");
        created.AlwaysReview.ShouldBeTrue();
        response.Headers.Location!.ToString().ShouldEndWith(created.Id.ToString());

        (await admin.GetFromJsonAsync<SupplierResponse>(SupplierUri(created.Id), Ct)).ShouldMatch(created);
        factory.Query(db => db.Suppliers.Single(s => s.Id == created.Id).TenantId).ShouldBe(ApiFactory.TenantA);

        (await admin.PostAsJsonAsync(SuppliersUri, NewSupplier("GETRÄNKE   HOFFMANN"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "same folded name");
        var bad = await admin.PostAsJsonAsync(SuppliersUri, new CreateSupplierRequest("***", null, null, null, null, null, "euro", null), Ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await bad.Content.ReadAsStringAsync(Ct);
        body.ShouldContain("\"name\"");
        body.ShouldContain("\"defaultCurrency\"");
    }

    [Fact]
    public async Task Patch_updates_fields_and_refolds_the_name()
    {
        using var admin = TenantAdminClient();
        var created = (await (await admin.PostAsJsonAsync(SuppliersUri, NewSupplier("Bio-Hof Müller"), Ct)).Content.ReadFromJsonAsync<SupplierResponse>(Ct))!;

        var patched = await admin.PatchAsJsonAsync(SupplierUri(created.Id), new PatchSupplierRequest("Biohof Müller GmbH", "DE111", null, null, ["mueller.bio"], null, null, true), Ct);

        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = (await patched.Content.ReadFromJsonAsync<SupplierResponse>(Ct))!;
        after.Name.ShouldBe("Biohof Müller GmbH");
        after.NormalizedName.ShouldBe(Identifiers.NormalizeName("Biohof Müller GmbH"));
        after.VatId.ShouldBe("DE111");
        after.EmailDomains.ShouldBe(["mueller.bio"]);
        after.AlwaysReview.ShouldBeTrue();
        after.UpdatedAt.ShouldBe(factory.Clock.GetUtcNow());

        var cleared = (await (await admin.PatchAsJsonAsync(SupplierUri(created.Id), new PatchSupplierRequest(null, "", null, null, null, null, null, null), Ct)).Content.ReadFromJsonAsync<SupplierResponse>(Ct))!;
        cleared.VatId.ShouldBeNull();
        cleared.Name.ShouldBe("Biohof Müller GmbH", "absent fields are untouched");

        (await admin.PatchAsJsonAsync(SupplierUri(Guid.NewGuid()), new PatchSupplierRequest("x", null, null, null, null, null, null, null), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_searches_by_folded_name_or_vat_id_and_pages_in_name_order()
    {
        using var admin = TenantAdminClient();
        foreach (var name in new[] { "Zebra Catering", "Äpfel & Birnen", "Metro Deutschland" })
        {
            (await admin.PostAsJsonAsync(SuppliersUri, NewSupplier(name), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        (await admin.PostAsJsonAsync(SuppliersUri, new CreateSupplierRequest("Konzum", "HR62226620908", null, null, null, null, null, null), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var byName = (await admin.GetFromJsonAsync<Page<SupplierResponse>>(new Uri("/api/v1/suppliers?q=apfel", UriKind.Relative), Ct))!;
        byName.Items.Select(s => s.Name).ShouldBe(["Äpfel & Birnen"]);

        var byVat = (await admin.GetFromJsonAsync<Page<SupplierResponse>>(new Uri("/api/v1/suppliers?q=hr 622 266 209 08", UriKind.Relative), Ct))!;
        byVat.Items.Select(s => s.Name).ShouldContain("Konzum");

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var uri = new Uri("/api/v1/suppliers?limit=2" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)), UriKind.Relative);
            var page = (await admin.GetFromJsonAsync<Page<SupplierResponse>>(uri, Ct))!;
            page.Items.Count.ShouldBeLessThanOrEqualTo(2);
            seen.AddRange(page.Items.Select(s => s.NormalizedName));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        seen.ShouldBe(seen.Order(StringComparer.Ordinal).ToList());
        seen.Distinct().Count().ShouldBe(seen.Count);
        seen.ShouldContain("ZEBRA CATERING");
    }

    [Fact]
    public async Task Tenant_b_cannot_see_or_change_tenant_a_suppliers()
    {
        using var adminA = TenantAdminClient();
        var created = (await (await adminA.PostAsJsonAsync(SuppliersUri, NewSupplier("Only in A"), Ct)).Content.ReadFromJsonAsync<SupplierResponse>(Ct))!;

        using var adminB = factory.ClientFor(new TokenSpec("tenant-b-admin", [Roles.TenantAdmin], ApiFactory.TenantB.ToString()));
        (await adminB.GetFromJsonAsync<Page<SupplierResponse>>(SuppliersUri, Ct))!.Items.ShouldNotContain(s => s.Id == created.Id);
        (await adminB.GetAsync(SupplierUri(created.Id), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminB.GetAsync(new Uri($"/api/v1/suppliers/{created.Id}/activity", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminB.DeleteAsync(SupplierUri(created.Id), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminB.PostAsJsonAsync(SuppliersUri, NewSupplier("Only in A"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created, "the same name is free in another tenant");

        // A SuperAdmin acting inside tenant B through X-Tenant-Id sees the same thing as B's admin.
        using var acting = factory.ClientFor(new TokenSpec("superadmin", [Roles.SuperAdmin]), tenantHeader: ApiFactory.TenantB.ToString());
        (await acting.GetAsync(SupplierUri(created.Id), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        factory.Query(db => db.Suppliers.Count(s => s.NormalizedName == "ONLY IN A")).ShouldBe(2);
    }

    [Fact]
    public async Task Delete_refuses_a_referenced_supplier_and_activity_lists_its_documents()
    {
        using var admin = TenantAdminClient();
        var created = (await (await admin.PostAsJsonAsync(SuppliersUri, NewSupplier("With documents"), Ct)).Content.ReadFromJsonAsync<SupplierResponse>(Ct))!;
        var now = factory.Clock.GetUtcNow();
        factory.Run(db =>
        {
            var channel = new DocProc.Infrastructure.Persistence.Entities.IngestionChannel { Id = Guid.CreateVersion7(), TenantId = ApiFactory.TenantA, Name = "seed", Type = DocProc.Infrastructure.Persistence.Entities.ChannelType.ApiUpload, CreatedAt = now };
            var ingestion = TestData.Ingestion(ApiFactory.TenantA, channel.Id, now);
            var item = TestData.Item(ingestion, now);
            db.IngestionChannels.Add(channel);
            db.Ingestions.Add(ingestion);
            db.IngestionItems.Add(item);
            for (var i = 0; i < 25; i++)
            {
                db.Documents.Add(TestData.Document(item, created.Id, $"INV-{i:000}", 100m + i, now.AddMinutes(i)));
            }
        });

        var activity = (await admin.GetFromJsonAsync<List<SupplierActivityItem>>(new Uri($"/api/v1/suppliers/{created.Id}/activity", UriKind.Relative), Ct))!;
        activity.Count.ShouldBe(20);
        activity[0].DocumentNumber.ShouldBe("INV-024", "newest first");
        activity[0].TotalGross.ShouldBe(124m);
        activity[0].Status.ShouldBe(nameof(DocumentStatus.Approved));
        activity[0].Type.ShouldBe(nameof(DocumentType.Invoice));
        activity[0].IssueDate.ShouldBe(DateOnly.FromDateTime(now.UtcDateTime));

        (await admin.DeleteAsync(SupplierUri(created.Id), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var unused = (await (await admin.PostAsJsonAsync(SuppliersUri, NewSupplier("Unused"), Ct)).Content.ReadFromJsonAsync<SupplierResponse>(Ct))!;
        (await admin.PutAsJsonAsync(new Uri("/api/v1/settings", UriKind.Relative), new TenantSettingsDto(0.9, [unused.Id], 10, true), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.DeleteAsync(SupplierUri(unused.Id), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.GetFromJsonAsync<TenantSettingsResponse>(new Uri("/api/v1/settings", UriKind.Relative), Ct))!.Settings.AlwaysReviewSupplierIds.ShouldBeEmpty("the deleted supplier left the always-review list");
        (await admin.GetAsync(SupplierUri(unused.Id), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var viewer = factory.ClientFor(new TokenSpec("viewer", [Roles.Viewer], TenantA));
        (await viewer.GetAsync(SuppliersUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await viewer.DeleteAsync(SupplierUri(created.Id), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
