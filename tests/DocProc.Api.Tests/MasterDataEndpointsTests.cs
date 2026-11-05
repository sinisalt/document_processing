using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;
using DocProc.Infrastructure.MasterData;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Api.Tests;

/// <summary>
/// Master data from the systems a tenant plugs in (docs/11 §6.8, §14): <c>POST /suppliers:import</c> from CSV with a
/// column map or from JSON, a dry run by default, matched by the ERP vendor id, the VAT id and the IBAN under a conflict
/// policy per attribute, idempotent, with a report kept as a run; master-data sources (an upload source run with its
/// records through its JSONata mapping, an http source that must name an http destination, a cron schedule).
/// </summary>
public sealed class MasterDataEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Csv = """
        Kreditor;Name;USt-IdNr;IBAN
        V-10042;Nordwind Großhandel GmbH;DE312456789;DE44200505501234567890
        V-20077;Südhof Feinkost GmbH;DE298765432;
        V-30001;Bergquell Getränke KG;not-a-vat-id-that-is-far-too-long-to-be-one;DE00 bad
        """;

    private static readonly Dictionary<string, string> Columns = new()
    {
        ["attributes.erpVendorId"] = "Kreditor",
        ["name"] = "Name",
        ["vatId"] = "USt-IdNr",
        ["ibans"] = "IBAN",
    };

    private Guid TenantWithNordwind(string name)
    {
        var tenant = factory.CreateTenant(name);
        var now = factory.Clock.GetUtcNow();
        factory.Run(db => db.Suppliers.Add(new Supplier { Id = Guid.CreateVersion7(), TenantId = tenant, Name = "Nordwind Großhandel GmbH", NormalizedName = DocProc.Domain.Common.Identifiers.NormalizeName("Nordwind Großhandel GmbH"), VatId = "DE312456789", CreatedAt = now, UpdatedAt = now }));
        return tenant;
    }

    [Fact]
    public async Task A_csv_import_is_a_dry_run_first_then_applies_and_a_second_run_changes_nothing()
    {
        var tenant = TenantWithNordwind("Import");
        using var admin = factory.TenantAdminOf(tenant);

        var dry = await admin.PostAsJsonAsync("/api/v1/suppliers:import", new { format = "csv", content = Csv, columns = Columns }, Ct);
        dry.StatusCode.ShouldBe(HttpStatusCode.OK, await dry.Content.ReadAsStringAsync(Ct));
        var report = (await dry.Content.ReadFromJsonAsync<MasterDataRunDto>(ApiFactory.Json, Ct))!;
        report.DryRun.ShouldBeTrue("a dry run is the default");
        (report.Created, report.Updated, report.Errors).ShouldBe((1, 1, 1));
        var rows = report.Report.GetProperty("rows").EnumerateArray().ToList();
        rows[0].GetProperty("action").GetString().ShouldBe(MasterDataActions.Update);
        rows[0].GetProperty("matchedBy").GetString().ShouldBe(DocProc.Infrastructure.MasterData.MatchKeys.VatId, "no supplier has the vendor id yet; the VAT id finds Nordwind");
        rows[0].GetProperty("changes").EnumerateArray().Select(c => c.GetString()).ShouldBe(["ibans", "attributes.erpVendorId"], ignoreOrder: true);
        rows[2].GetProperty("action").GetString().ShouldBe(MasterDataActions.Error);
        factory.Query(db => db.Suppliers.Count(s => s.TenantId == tenant)).ShouldBe(1, "a dry run changes nothing");

        var applied = (await (await admin.PostAsJsonAsync("/api/v1/suppliers:import", new { format = "csv", content = Csv, columns = Columns, dryRun = false }, Ct)).Content.ReadFromJsonAsync<MasterDataRunDto>(ApiFactory.Json, Ct))!;
        (applied.Created, applied.Updated, applied.Errors).ShouldBe((1, 1, 1));
        var nordwind = factory.Query(db => db.Suppliers.Single(s => s.TenantId == tenant && s.VatId == "DE312456789"));
        nordwind.AttributesJson!.ShouldContain("V-10042");
        nordwind.Ibans.ShouldBe(["DE44200505501234567890"]);
        factory.Query(db => db.Suppliers.Single(s => s.TenantId == tenant && s.VatId == "DE298765432")).Name.ShouldBe("Südhof Feinkost GmbH");

        var again = (await (await admin.PostAsJsonAsync("/api/v1/suppliers:import", new { format = "csv", content = Csv, columns = Columns, dryRun = false }, Ct)).Content.ReadFromJsonAsync<MasterDataRunDto>(ApiFactory.Json, Ct))!;
        (again.Created, again.Updated, again.Unchanged).ShouldBe((0, 0, 2), "the same file twice changes nothing");
        again.Report.GetProperty("rows")[0].GetProperty("matchedBy").GetString().ShouldBe(DocProc.Infrastructure.MasterData.MatchKeys.ErpVendorId, "the vendor id is the first match key now that it is stored");
        factory.Query(db => db.MasterDataRuns.Count(r => r.TenantId == tenant && r.Trigger == MasterDataTrigger.Import)).ShouldBe(3, "every import keeps its report");
    }

    [Fact]
    public async Task The_conflict_policy_keeps_local_values_or_flags_them_and_bad_requests_are_refused()
    {
        var tenant = TenantWithNordwind("Import policy");
        using var admin = factory.TenantAdminOf(tenant);
        var records = new[] { new { name = "NORDWIND GH", vatId = "DE312456789", currency = "EUR", attributes = new Dictionary<string, object> { ["erpVendorId"] = "V-10042" } } };

        var flagged = (await (await admin.PostAsJsonAsync("/api/v1/suppliers:import", new { format = "json", records, conflictPolicy = new Dictionary<string, string> { ["name"] = "flag" }, dryRun = false }, Ct)).Content.ReadFromJsonAsync<MasterDataRunDto>(ApiFactory.Json, Ct))!;
        flagged.Conflicts.ShouldBe(1);
        flagged.Updated.ShouldBe(1, "the currency and the vendor id were empty and are filled");
        flagged.Report.GetProperty("rows")[0].GetProperty("conflicts")[0].GetString()!.ShouldContain("name");
        factory.Query(db => db.Suppliers.Single(s => s.TenantId == tenant)).Name.ShouldBe("Nordwind Großhandel GmbH", "a flagged attribute is not changed");

        var kept = (await (await admin.PostAsJsonAsync("/api/v1/suppliers:import", new { format = "json", records, conflictPolicy = new Dictionary<string, string> { ["*"] = "localWins" }, dryRun = false }, Ct)).Content.ReadFromJsonAsync<MasterDataRunDto>(ApiFactory.Json, Ct))!;
        (kept.Conflicts, kept.Unchanged).ShouldBe((0, 1));

        foreach (var bad in new object[]
        {
            new { format = "csv", content = Csv },
            new { format = "csv", content = Csv, columns = new Dictionary<string, string> { ["emailDomains"] = "Name" } },
            new { format = "csv", content = Csv, columns = new Dictionary<string, string> { ["name"] = "Firma" } },
            new { format = "json", records = new { name = "x" } },
            new { format = "xml" },
            new { format = "json", records, matchKeys = new[] { "name" } },
            new { format = "json", records, conflictPolicy = new Dictionary<string, string> { ["name"] = "merge" } },
        })
        {
            (await admin.PostAsJsonAsync("/api/v1/suppliers:import", bad, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, JsonSerializer.Serialize(bad));
        }

        using var reviewer = factory.ClientFor(new TokenSpec("rev", [Roles.Reviewer], tenant.ToString()));
        (await reviewer.PostAsJsonAsync("/api/v1/suppliers:import", new { format = "json", records }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "supplier master data is a tenant admin's");
    }

    [Fact]
    public async Task An_upload_source_maps_its_records_with_jsonata_and_its_runs_are_listed()
    {
        var tenant = TenantWithNordwind("Sources");
        using var admin = factory.TenantAdminOf(tenant);
        foreach (var bad in new object[]
        {
            new { name = "erp", transport = "ftp" },
            new { name = "erp", transport = "http" },
            new { name = "erp", transport = "upload", schedule = "every day" },
            new { name = "erp", transport = "upload", mapping = "$.{ name: " },
            new { name = "erp", transport = "upload", target = "articles" },
        })
        {
            (await admin.PostAsJsonAsync("/api/v1/master-data-sources", bad, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, JsonSerializer.Serialize(bad));
        }

        var created = await admin.PostAsJsonAsync("/api/v1/master-data-sources", new
        {
            name = "ERP vendors",
            transport = "upload",
            mapping = """vendors.{ "name": name1, "vatId": ustId, "attributes": { "erpVendorId": lifnr } }""",
        }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var source = (await created.Content.ReadFromJsonAsync<MasterDataSourceDto>(ApiFactory.Json, Ct))!;
        source.MatchKeys.ShouldBeEmpty("none given: the default order, attributes.erpVendorId, vatId, iban");
        (await admin.PostAsJsonAsync("/api/v1/master-data-sources", new { name = "ERP vendors", transport = "upload" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var records = new { vendors = new[] { new { Name1 = "Nordwind Großhandel GmbH", UstId = "DE312456789", Lifnr = "V-10042" }, new { Name1 = "Bergquell Getränke KG", UstId = "DE287654321", Lifnr = "V-30001" } } };
        var dry = (await (await admin.PostAsJsonAsync($"/api/v1/master-data-sources/{source.Id}:dry-run", new { records }, Ct)).Content.ReadFromJsonAsync<MasterDataRunDto>(ApiFactory.Json, Ct))!;
        (dry.DryRun, dry.Created, dry.Updated).ShouldBe((true, 1, 1), dry.Report.ToString());
        var run = await admin.PostAsJsonAsync($"/api/v1/master-data-sources/{source.Id}:run", new { records }, Ct);
        run.StatusCode.ShouldBe(HttpStatusCode.OK, await run.Content.ReadAsStringAsync(Ct));
        var applied = (await run.Content.ReadFromJsonAsync<MasterDataRunDto>(ApiFactory.Json, Ct))!;
        (applied.DryRun, applied.Status, applied.Created).ShouldBe((false, MasterDataRunStatus.Succeeded, 1));
        factory.Query(db => db.Suppliers.Count(s => s.TenantId == tenant)).ShouldBe(2);

        (await admin.PostAsJsonAsync($"/api/v1/master-data-sources/{source.Id}:run", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "an upload source runs with the records in the request");

        // Records the mapping cannot read are a failed run that changes nothing.
        var failed = (await (await admin.PostAsJsonAsync($"/api/v1/master-data-sources/{source.Id}:run", new { records = new { vendors = "none" } }, Ct)).Content.ReadFromJsonAsync<MasterDataRunDto>(ApiFactory.Json, Ct))!;
        failed.Status.ShouldBe(MasterDataRunStatus.Failed);

        var runs = (await admin.GetFromJsonAsync<List<MasterDataRunDto>>($"/api/v1/master-data-sources/{source.Id}/runs", ApiFactory.Json, Ct))!;
        runs.Count.ShouldBe(3);
        runs.ShouldContain(r => r.Id == failed.Id && r.Status == MasterDataRunStatus.Failed);
        (await admin.GetFromJsonAsync<MasterDataRunDto>($"/api/v1/master-data-runs/{applied.Id}", ApiFactory.Json, Ct))!.Created.ShouldBe(1);
        var after = (await admin.GetFromJsonAsync<MasterDataSourceDto>($"/api/v1/master-data-sources/{source.Id}", ApiFactory.Json, Ct))!;
        after.LastRunAt.ShouldNotBeNull();
        after.NextRunAt.ShouldBeNull("an upload source has no schedule");
        (await admin.PostAsJsonAsync("/api/v1/master-data-sources", new { name = "scheduled upload", transport = "upload", schedule = "15 3 * * *" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "only an http source runs on a schedule");

        // An http source reads an http destination of the tenant on its schedule (in the tenant's time zone).
        var destination = Guid.CreateVersion7();
        var now = factory.Clock.GetUtcNow();
        factory.Run(db => db.OutputDestinations.Add(new OutputDestination { Id = destination, TenantId = tenant, Name = "erp-vendors", Transport = OutputTransports.Http, ConfigJson = """{"urlTemplate":"https://erp.example/api/vendors"}""", CreatedBy = "test", CreatedAt = now, UpdatedAt = now }));
        var scheduled = await admin.PostAsJsonAsync("/api/v1/master-data-sources", new { name = "ERP sync", transport = "http", destinationId = destination, schedule = "CRON_TZ=Europe/Berlin 15 3 * * *" }, Ct);
        scheduled.StatusCode.ShouldBe(HttpStatusCode.Created, await scheduled.Content.ReadAsStringAsync(Ct));
        (await scheduled.Content.ReadFromJsonAsync<MasterDataSourceDto>(ApiFactory.Json, Ct))!.NextRunAt.ShouldBe(new DateTimeOffset(2026, 10, 31, 2, 15, 0, TimeSpan.Zero), "03:15 in Berlin (summer time until 25 October, so UTC+1 on the 31st)");
        (await admin.PostAsJsonAsync("/api/v1/master-data-sources", new { name = "ERP sync 2", transport = "http", destinationId = Guid.NewGuid() }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "not a destination of the tenant");

        // At most every 15 minutes on any day it runs, whatever day it is saved on.
        foreach (var often in new[] { "* * * * *", "* * * * 0", "* * 6 * *", "*/5 3 * * 1-5" })
        {
            var refused = await admin.PostAsJsonAsync("/api/v1/master-data-sources", new { name = "ERP sync often", transport = "http", destinationId = destination, schedule = often }, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, often);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("at most every 15 minutes");
        }

        // The destination a source reads cannot be removed under it: a 409 that names the source, not a 500.
        factory.Run(db => db.Tenants.Single(t => t.Id == tenant).Settings.Features!.Outputs = true);
        var inUse = await admin.DeleteAsync($"/api/v1/destinations/{destination}", Ct);
        inUse.StatusCode.ShouldBe(HttpStatusCode.Conflict, await inUse.Content.ReadAsStringAsync(Ct));
        (await inUse.Content.ReadAsStringAsync(Ct)).ShouldContain("ERP sync");
        factory.Query(db => db.OutputDestinations.Count(d => d.Id == destination)).ShouldBe(1);

        // Another tenant sees nothing of it.
        using var other = factory.TenantAdminOf(factory.CreateTenant("Other sources"));
        (await other.GetAsync($"/api/v1/master-data-sources/{source.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.GetFromJsonAsync<List<MasterDataSourceDto>>("/api/v1/master-data-sources", ApiFactory.Json, Ct))!.ShouldBeEmpty();

        (await admin.DeleteAsync($"/api/v1/master-data-sources/{source.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        factory.Query(db => db.MasterDataRuns.Count(r => r.SourceId == source.Id)).ShouldBe(0, "its runs go with it");
    }
}
