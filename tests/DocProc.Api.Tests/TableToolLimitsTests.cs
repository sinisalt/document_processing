using System.Net;
using System.Net.Http.Json;
using DocProc.Api.Documents;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Api.Tests;

/// <summary>The API host with a table read budget of nothing: every read runs past it.</summary>
public sealed class NoBudgetApiFactory : ApiFactory
{
    protected override void ConfigureServices(IServiceCollection services) => services.Configure<TableToolOptions>(o => o.ReadBudget = TimeSpan.Zero);
}

/// <summary>
/// The limits on the table tool in the API every tenant shares (docs/11 §12.4; <see cref="TableToolOptions"/>): a pattern
/// whose counted repetitions multiply is refused before it runs, and the endpoints carry the per-tenant concurrency limit.
/// </summary>
public sealed class TableToolLimitsTests(ApiFactory factory) : MapModeTestBase(factory), IClassFixture<ApiFactory>
{
    /// <summary>Twenty-three characters, about ten thousand automaton nodes: seconds of CPU and hundreds of MB per line.</summary>
    private const string Explosive = @"(?:[^§]{0,99}\d){0,99}§";

    [Fact]
    public async Task A_pattern_that_unrolls_too_far_is_refused_before_it_runs()
    {
        var tenant = NewTenant("Table pattern limits");
        var id = await SeedAsync(tenant, PriceListPages.Layer(pages: 2, rowsPerPage: 3), pages: 2);
        using var reviewer = Reviewer(tenant);
        await SetTypeAsync(reviewer, id);
        var box = new { x0 = 40, y0 = 95, x1 = 995, y1 = 940 };
        var columns = new[] { new { x0 = 50, x1 = 150, field = "supplierSku" } };

        foreach (var (key, body) in new (string, object)[]
        {
            ("stopAt[0]", new { page = 1, bBox = box, columns, stopAt = new[] { Explosive } }),
            ("skipRows[1]", new { page = 1, bBox = box, columns, skipRows = new[] { "^Seite", Explosive } }),
            ("stopAt[0]", new { page = 1, bBox = box, stopAt = new[] { "(.{0,99}){0,99}Q" } }),
        })
        {
            var response = await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), body, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
            var errors = (await ProblemOf(response)).GetProperty("errors");
            errors.GetProperty(key)[0].GetString()!.ShouldContain("repeats too much");
        }

        // A plain skip line is fine.
        var fine = await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), new { page = 1, bBox = box, columns, skipRows = new[] { @"^Seite\s+\d+\s+von\s+\d+" } }, Ct);
        fine.StatusCode.ShouldBe(HttpStatusCode.OK, await fine.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public void The_table_tool_and_region_ocr_run_a_few_at_a_time_per_tenant()
    {
        _ = Factory.Server;
        var endpoints = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        foreach (var name in new[] { "ReadDocumentTable", "ReadDocumentRegion" })
        {
            var endpoint = endpoints.Single(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
            endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>().ShouldNotBeNull(name).PolicyName.ShouldBe(TableCapture.RateLimitPolicy);
        }

        var options = Factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<TableToolOptions>>().Value;
        options.ReadBudget.ShouldBe(TimeSpan.FromSeconds(10));
        options.ConcurrentReadsPerTenant.ShouldBe(2);
    }
}

/// <summary>A table read past its time budget answers 422 with its code, for the proposal and for a mapping alike.</summary>
public sealed class TableReadBudgetTests(NoBudgetApiFactory factory) : MapModeTestBase(factory), IClassFixture<NoBudgetApiFactory>
{
    [Fact]
    public async Task A_read_past_the_budget_answers_422()
    {
        var tenant = NewTenant("Table budget");
        var id = await SeedAsync(tenant, PriceListPages.Layer(pages: 2, rowsPerPage: 3), pages: 2);
        using var reviewer = Reviewer(tenant);
        await SetTypeAsync(reviewer, id);

        foreach (var body in new[] { PriceListPages.Mapping(pages: "1-*"), new { page = 1, bBox = new { x0 = 40, y0 = 95, x1 = 995, y1 = 940 } } })
        {
            var response = await reviewer.PostAsJsonAsync(Doc(id, "/regions:table"), body, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Ct));
            (await ProblemOf(response)).GetProperty("code").GetString().ShouldBe(TableCapture.TooSlow);
        }

        Factory.Query(db => db.Corrections.Count(c => c.DocumentId == id && c.FieldPath == "lines")).ShouldBe(0);
    }
}
