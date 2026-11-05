using System.Net;
using System.Net.Http.Json;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;
using DocProc.Domain.Common;
using DocProc.Domain.Documents;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

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
        var codes = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("errorCodes");
        codes.GetProperty("defaultCurrency").GetProperty("code").GetString().ShouldBe("currencyCode");

        var blank = await admin.PostAsJsonAsync(SuppliersUri, new CreateSupplierRequest(" ", null, null, null, null, null, null, null), Ct);
        var required = System.Text.Json.JsonDocument.Parse(await blank.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("errorCodes").GetProperty("name");
        required.GetProperty("code").GetString().ShouldBe("requiredMaxLength");
        required.GetProperty("params").GetProperty("maxLength").GetString().ShouldBe(SupplierEndpoints.MaxNameLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The field's code and parameters in a 400's <c>errorCodes</c>.</summary>
    private static async Task<(string Code, System.Text.Json.JsonElement Params)> ErrorCodeOf(HttpResponseMessage response, string field)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, field);
        using var problem = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var entry = problem.RootElement.GetProperty("errorCodes").GetProperty(field);
        return (entry.GetProperty("code").GetString()!, entry.GetProperty("params").Clone());
    }

    [Fact]
    public async Task The_ibans_email_domains_and_aliases_of_a_supplier_are_capped_and_ibans_and_domains_checked()
    {
        using var admin = TenantAdminClient();
        static CreateSupplierRequest With(IReadOnlyList<string>? ibans = null, IReadOnlyList<string>? domains = null, IReadOnlyList<string>? aliases = null) =>
            new("Listen GmbH", null, null, ibans, domains, aliases, null, null);
        string Iban(int i) => $"DE{i % 100:00}37040044{i:0000000000}";

        foreach (var (request, field, code) in new[]
        {
            (With(ibans: [.. Enumerable.Range(0, SupplierEndpoints.MaxIbans + 1).Select(Iban)]), "ibans", "maxItems"),
            (With(ibans: ["not an iban at all"]), "ibans", "iban"),
            (With(ibans: ["DE89 3704"]), "ibans", "iban"),
            (With(ibans: [new string('D', 222)]), "ibans", "maxLength"),
            (With(domains: [.. Enumerable.Range(0, SupplierEndpoints.MaxEmailDomains + 1).Select(i => $"d{i}.example")]), "emailDomains", "maxItems"),
            (With(domains: ["@nordwind.example"]), "emailDomains", "emailDomain"),
            (With(domains: ["nordwind"]), "emailDomains", "emailDomain"),
            (With(domains: ["info@nordwind.example"]), "emailDomains", "emailDomain"),
            (With(aliases: [.. Enumerable.Range(0, SupplierEndpoints.MaxAliases + 1).Select(i => $"Alias {i}")]), "aliases", "maxItems"),
            (With(aliases: [new string('a', SupplierEndpoints.MaxNameLength + 1)]), "aliases", "maxLength"),
        })
        {
            var refused = await admin.PostAsJsonAsync(SuppliersUri, request, Ct);
            (await ErrorCodeOf(refused, field)).Code.ShouldBe(code, $"{field}: {code}");
        }

        factory.Query(db => db.Suppliers.Any(s => s.Name == "Listen GmbH")).ShouldBeFalse("nothing was stored");
        var tooMany = await admin.PostAsJsonAsync(SuppliersUri, With(ibans: [.. Enumerable.Range(0, SupplierEndpoints.MaxIbans + 1).Select(Iban)]), Ct);
        (await ErrorCodeOf(tooMany, "ibans")).Params.GetProperty("maxItems").GetString().ShouldBe(SupplierEndpoints.MaxIbans.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // At the caps, with a repeat that counts once and IBANs grouped as printed.
        var full = With(
            ibans: [.. Enumerable.Range(0, SupplierEndpoints.MaxIbans).Select(Iban), "DE00 3704 0044 0000 0000 00"],
            domains: [.. Enumerable.Range(0, SupplierEndpoints.MaxEmailDomains).Select(i => $"d{i}.example"), "D0.Example"],
            aliases: [.. Enumerable.Range(0, SupplierEndpoints.MaxAliases).Select(i => $"Alias {i}")]);
        var created = await admin.PostAsJsonAsync(SuppliersUri, full, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var supplier = (await created.Content.ReadFromJsonAsync<SupplierResponse>(Ct))!;
        supplier.Ibans.Count.ShouldBe(SupplierEndpoints.MaxIbans);
        supplier.Ibans[0].ShouldBe("DE0037040044" + "0000000000");
        supplier.EmailDomains.Count.ShouldBe(SupplierEndpoints.MaxEmailDomains);
        supplier.Aliases.Count.ShouldBe(SupplierEndpoints.MaxAliases);

        // PATCH checks the lists it is given, and leaves the others as they are.
        var patch = await admin.PatchAsJsonAsync(SupplierUri(supplier.Id), new PatchSupplierRequest(null, null, null, ["NOTANIBANATALL"], null, null, null, null), Ct);
        (await ErrorCodeOf(patch, "ibans")).Code.ShouldBe("iban");
        var domains = await admin.PatchAsJsonAsync(SupplierUri(supplier.Id), new PatchSupplierRequest(null, null, null, null, ["nordwind .example"], null, null, null), Ct);
        (await ErrorCodeOf(domains, "emailDomains")).Code.ShouldBe("emailDomain");
        var ok = await admin.PatchAsJsonAsync(SupplierUri(supplier.Id), new PatchSupplierRequest(null, null, null, ["AT61 1904 3002 3457 3201"], ["müller-bio.example"], null, null, null), Ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync(Ct));
        var after = (await ok.Content.ReadFromJsonAsync<SupplierResponse>(Ct))!;
        after.Ibans.ShouldBe(["AT611904300234573201"]);
        after.EmailDomains.ShouldBe(["müller-bio.example"]);
        after.Aliases.Count.ShouldBe(SupplierEndpoints.MaxAliases);
    }

    /// <summary>
    /// A supplier stored before the caps (P1) with longer lists and entries the format checks now refuse. The portal
    /// sends every list with every edit, so a list sent back as stored is not checked; a changed one is, and the code
    /// names the refused entry.
    /// </summary>
    [Fact]
    public async Task A_supplier_stored_before_the_caps_keeps_its_lists_through_an_edit_that_sends_them_back()
    {
        using var admin = TenantAdminClient();
        var now = factory.Clock.GetUtcNow();
        var id = Guid.CreateVersion7();
        List<string> legacyIbans = [.. Enumerable.Range(0, 30).Select(i => $"DE{i:00}370400440000{i:000000}"), "NOTANIBANATALL"];
        List<string> legacyDomains = [.. Enumerable.Range(0, 25).Select(i => $"d{i}.legacy.example"), "@legacy.example"];
        List<string> legacyAliases = [.. Enumerable.Range(0, 60).Select(i => $"Legacy {i}")];
        factory.Run(db => db.Suppliers.Add(new DocProc.Infrastructure.Persistence.Entities.Supplier
        {
            Id = id,
            TenantId = ApiFactory.TenantA,
            Name = "Altbestand KG",
            NormalizedName = Identifiers.NormalizeName("Altbestand KG"),
            Ibans = [.. legacyIbans],
            EmailDomains = [.. legacyDomains],
            Aliases = [.. legacyAliases],
            CreatedAt = now,
            UpdatedAt = now,
        }));

        var stored = (await admin.GetFromJsonAsync<SupplierResponse>(SupplierUri(id), Ct))!;
        stored.Ibans.Count.ShouldBe(31);

        // The portal's save: every list as GET returned it (one IBAN regrouped as typed), with a new name and review setting.
        List<string> asTyped = ["DE00 3704 0044 0000 0000 00", .. stored.Ibans.Skip(1)];
        var portal = await admin.PatchAsJsonAsync(SupplierUri(id), new PatchSupplierRequest("Altbestand KG & Co", null, null, asTyped, stored.EmailDomains, stored.Aliases, null, true), Ct);
        portal.StatusCode.ShouldBe(HttpStatusCode.OK, await portal.Content.ReadAsStringAsync(Ct));
        var edited = (await portal.Content.ReadFromJsonAsync<SupplierResponse>(Ct))!;
        edited.Name.ShouldBe("Altbestand KG & Co");
        edited.AlwaysReview.ShouldBeTrue();
        factory.Query(db => db.Suppliers.Single(s => s.Id == id).Ibans).ShouldBe(legacyIbans);
        factory.Query(db => db.Suppliers.Single(s => s.Id == id).EmailDomains).ShouldBe(legacyDomains);
        factory.Query(db => db.Suppliers.Single(s => s.Id == id).Aliases).ShouldBe(legacyAliases);

        // A changed list is checked whole: still over the cap, then the entry the format check refuses, named.
        var trimmedByOne = await admin.PatchAsJsonAsync(SupplierUri(id), new PatchSupplierRequest(null, null, null, legacyIbans.Skip(1).ToList(), null, null, null, null), Ct);
        (await ErrorCodeOf(trimmedByOne, "ibans")).Code.ShouldBe("maxItems");
        var underTheCap = await admin.PatchAsJsonAsync(SupplierUri(id), new PatchSupplierRequest(null, null, null, legacyIbans.Skip(20).ToList(), legacyDomains.Skip(10).ToList(), null, null, null), Ct);
        var (ibanCode, ibanParams) = await ErrorCodeOf(underTheCap, "ibans");
        ibanCode.ShouldBe("iban");
        ibanParams.GetProperty("value").GetString().ShouldBe("NOTANIBANATALL");
        using (var problem = System.Text.Json.JsonDocument.Parse(await underTheCap.Content.ReadAsStringAsync(Ct)))
        {
            var domain = problem.RootElement.GetProperty("errorCodes").GetProperty("emailDomains");
            domain.GetProperty("code").GetString().ShouldBe("emailDomain");
            domain.GetProperty("params").GetProperty("value").GetString().ShouldBe("@legacy.example");
        }

        factory.Query(db => db.Suppliers.Single(s => s.Id == id).Ibans.Count).ShouldBe(31, "a refused PATCH changes nothing");

        // Trimmed and fixed: stored as cleaned, the untouched aliases stay as they were.
        var fixedLists = await admin.PatchAsJsonAsync(SupplierUri(id), new PatchSupplierRequest(null, null, null, legacyIbans.Skip(20).SkipLast(1).ToList(), legacyDomains.Skip(10).SkipLast(1).ToList(), stored.Aliases, null, null), Ct);
        fixedLists.StatusCode.ShouldBe(HttpStatusCode.OK, await fixedLists.Content.ReadAsStringAsync(Ct));
        var after = (await fixedLists.Content.ReadFromJsonAsync<SupplierResponse>(Ct))!;
        after.Ibans.Count.ShouldBe(10);
        after.EmailDomains.Count.ShouldBe(15);
        after.Aliases.Count.ShouldBe(60);
    }

    [Fact]
    public void Supplier_writes_and_the_review_actions_refuse_a_body_larger_than_a_legitimate_one()
    {
        _ = factory.Server;
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        foreach (var (name, limit) in new[]
        {
            ("CreateSupplier", SupplierEndpoints.MaxRequestBytes),
            ("UpdateSupplier", SupplierEndpoints.MaxRequestBytes),
            ("SetDocumentType", DocumentEndpoints.MaxReviewActionRequestBytes),
            ("AssignDocumentSupplier", DocumentEndpoints.MaxReviewActionRequestBytes),
            ("CreateSupplierFromDocument", DocumentEndpoints.MaxReviewActionRequestBytes),
            ("SplitDocument", DocumentEndpoints.MaxReviewActionRequestBytes),
            ("JoinDocuments", DocumentEndpoints.MaxReviewActionRequestBytes),
        })
        {
            var endpoint = endpoints.Single(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
            endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>().ShouldNotBeNull(name).MaxRequestBodySize.ShouldBe(limit, name);
        }

        // A split of the most parts it may make, each with a type and a page list, fits with room to spare.
        var split = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { parts = Enumerable.Range(1, DocumentEndpoints.MaxParts).Select(i => new { pages = $"{i}-{i + 1},{i + 3}", typeKey = "std.delivery-note" }) });
        split.Length.ShouldBeLessThan(DocumentEndpoints.MaxReviewActionRequestBytes / 4);
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
    public async Task A_name_that_folds_to_more_than_300_characters_is_refused()
    {
        using var admin = TenantAdminClient();
        var name = new string('ß', SupplierEndpoints.MaxNameLength);
        Identifiers.NormalizeName(name).Length.ShouldBe(2 * SupplierEndpoints.MaxNameLength, "ß folds to SS");

        var created = await admin.PostAsJsonAsync(SuppliersUri, NewSupplier(name), Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await created.Content.ReadAsStringAsync(Ct)).ShouldContain("\"name\"");

        var existing = (await (await admin.PostAsJsonAsync(SuppliersUri, NewSupplier("Short enough"), Ct)).Content.ReadFromJsonAsync<SupplierResponse>(Ct))!;
        var patched = await admin.PatchAsJsonAsync(SupplierUri(existing.Id), new PatchSupplierRequest(name, null, null, null, null, null, null, null), Ct);
        patched.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await patched.Content.ReadAsStringAsync(Ct)).ShouldContain("\"name\"");
        factory.Query(db => db.Suppliers.Single(s => s.Id == existing.Id).Name).ShouldBe("Short enough");

        // Exactly at the limit after folding is fine.
        var atLimit = new string('ß', SupplierEndpoints.MaxNameLength / 2);
        (await admin.PostAsJsonAsync(SuppliersUri, NewSupplier(atLimit), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task List_searches_by_folded_name_or_vat_id_and_pages_in_name_order()
    {
        // A tenant of its own, so the exact search result cannot pick up other tests' suppliers.
        var tenant = factory.CreateTenant("Supplier search");
        using var admin = factory.TenantAdminOf(tenant);
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

        seen.ShouldBe(["APFEL BIRNEN", "KONZUM", "METRO DEUTSCHLAND", "ZEBRA CATERING"]);
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
        activity[0].Type.ShouldBe(nameof(DocumentTypeKey.Invoice));
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

    /// <summary>The profile's foreign key is NO ACTION (SQL Server allows one cascade path per table), so the endpoint refuses first.</summary>
    [Fact]
    public async Task Delete_refuses_a_supplier_an_extractor_profile_belongs_to()
    {
        using var admin = TenantAdminClient();
        var created = (await (await admin.PostAsJsonAsync(SuppliersUri, NewSupplier("With a profile"), Ct)).Content.ReadFromJsonAsync<SupplierResponse>(Ct))!;
        var now = factory.Clock.GetUtcNow();
        factory.Run(db => db.ExtractorProfiles.Add(new DocProc.Infrastructure.Persistence.Entities.ExtractorProfile
        {
            Id = Guid.CreateVersion7(),
            SupplierId = created.Id,
            TenantId = ApiFactory.TenantA,
            DocumentType = "Invoice",
            CreatedAt = now,
            UpdatedAt = now,
        }));

        var response = await admin.DeleteAsync(SupplierUri(created.Id), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Extractor profiles reference this supplier");
        factory.Query(db => db.Suppliers.Any(s => s.Id == created.Id)).ShouldBeTrue();
    }
}
