using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;
using DocProc.Definitions.Pipelines;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Mvc;

namespace DocProc.Api.Tests;

/// <summary>
/// The API of P5 part A (docs/11 §10, §16, §18): <c>GET /step-kinds</c>, the resolved pipeline of a type for a supplier,
/// the pipeline snapshots, <c>settings.pipelineOverrides</c> checked when a supplier or a group is saved (fixed steps,
/// parameters, the <c>http-lookup</c> destination and expressions, the <c>configurableSteps</c> switch, never from
/// review), the switch in the tenant settings, and the LLM budgets with their roles.
/// </summary>
public sealed class PipelineEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PipelineEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.Run(db =>
        {
            var features = db.Tenants.Single(t => t.Id == ApiFactory.TenantA).Settings.Features!;
            features.SourceRouting = true;
            features.ConfigurableSteps = true;
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Admin(Guid? tenant = null) => _factory.ClientFor(new TokenSpec("tenant-admin", [Roles.TenantAdmin], (tenant ?? ApiFactory.TenantA).ToString()));

    private HttpClient Reviewer() => _factory.ClientFor(new TokenSpec("tenant-a-reviewer", [Roles.Reviewer], ApiFactory.TenantA.ToString()));

    [Fact]
    public async Task Step_kinds_list_every_phase_with_its_parameter_schema()
    {
        using var reviewer = Reviewer();
        var kinds = (await reviewer.GetFromJsonAsync<List<StepKindResponse>>("/api/v1/step-kinds", ApiFactory.Json, Ct))!;

        kinds.Select(k => k.Phase).Distinct().ShouldBe(PipelinePhases.All);
        var identification = kinds.Single(k => k.Kind == StepKindCatalogue.ClassifyIdentification);
        identification.Fixed.ShouldBeTrue();
        var lookup = kinds.Single(k => k.Kind == StepKindCatalogue.HttpLookup);
        lookup.Insertable.ShouldBeTrue();
        lookup.ParamsSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldBe(["destinationId", "response"]);
    }

    [Fact]
    public async Task The_resolved_pipeline_of_a_type_applies_the_supplier_s_overrides_and_its_hash_reads_back()
    {
        using var admin = Admin();
        var supplier = await SupplierAsync(admin, """{"pipelineOverrides":[{"op":"disable","step":"duplicate"},{"op":"param","step":"llm","params":{"effort":"high"}},{"op":"disable","step":"uom"}]}""");

        var own = (await admin.GetFromJsonAsync<ResolvedPipelineResponse>("/api/v1/document-types/Invoice/resolved-pipeline", ApiFactory.Json, Ct))!;
        var resolved = (await admin.GetFromJsonAsync<ResolvedPipelineResponse>($"/api/v1/document-types/Invoice/resolved-pipeline?supplierId={supplier.Id}", ApiFactory.Json, Ct))!;

        own.Configurable.ShouldBeTrue();
        own.Overridden.ShouldBeFalse();
        resolved.Overridden.ShouldBeTrue();
        resolved.Hash.ShouldNotBe(own.Hash);
        resolved.Steps.Single(s => s.Id == "duplicate").Enabled.ShouldBeFalse();
        var llm = resolved.Steps.Single(s => s.Id == "llm");
        llm.Source.ShouldBe(PipelineSources.Supplier);
        llm.Params.GetProperty("effort").GetString().ShouldBe("high");
        resolved.Skipped.ShouldHaveSingleItem().ShouldContain("'uom'");
        resolved.Refused.ShouldBeEmpty();

        var snapshot = (await admin.GetFromJsonAsync<PipelineSnapshotResponse>($"/api/v1/pipelines/{resolved.Hash}", ApiFactory.Json, Ct))!;
        snapshot.Steps.EnumerateArray().Single(s => s.GetProperty("id").GetString() == "duplicate").GetProperty("enabled").GetBoolean().ShouldBeFalse();
        (await admin.GetAsync($"/api/v1/pipelines/{new string('0', 32)}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var b = Admin(ApiFactory.TenantB);
        (await b.GetAsync($"/api/v1/pipelines/{resolved.Hash}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "a snapshot is the tenant's own");

        (await admin.GetAsync("/api/v1/document-types/NoSuchType/resolved-pipeline", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetAsync($"/api/v1/document-types/Invoice/resolved-pipeline?supplierId={Guid.NewGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Overrides_that_touch_a_fixed_step_or_misfit_a_kind_are_refused_at_save_with_the_place()
    {
        using var admin = Admin();
        var supplier = await SupplierAsync(admin, null);
        foreach (var (overrides, field) in new[]
        {
            ("""[{"op":"disable","step":"classify.identification"}]""", "settings.pipelineOverrides[0].step"),
            ("""[{"op":"replace","step":"classify.identification","with":{"id":"x","kind":"classify.keyword"}}]""", "settings.pipelineOverrides[0].step"),
            ("""[{"op":"insertAfter","step":"split.llm","with":{"id":"early","kind":"http-lookup","params":{"destinationId":"00000000-0000-0000-0000-000000000001","response":"{}"}}}]""", "settings.pipelineOverrides[0].step"),
            ("""[{"op":"disable","step":"review-gate"}]""", "settings.pipelineOverrides[0].step"),
            ("""[{"op":"param","step":"llm","params":{"effort":"maximal"}}]""", "settings.pipelineOverrides[0].params"),
            ("""[{"op":"remove","step":"llm"}]""", "settings.pipelineOverrides[0].op"),
        })
        {
            var refused = await PatchAsync(admin, supplier.Id, $$"""{"pipelineOverrides":{{overrides}}}""");
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, overrides);
            (await refused.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors.Keys.ShouldContain(field, overrides);
        }

        var unknownMember = await PatchAsync(admin, supplier.Id, """{"pipelineOverrides":[{"op":"disable","step":"llm","why":"x"}]}""");
        unknownMember.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_http_lookup_needs_an_http_destination_of_the_tenant_and_expressions_that_parse()
    {
        using var admin = Admin();
        var supplier = await SupplierAsync(admin, null);
        var destination = Guid.CreateVersion7();
        _factory.Run(db => db.OutputDestinations.Add(new OutputDestination
        {
            Id = destination,
            TenantId = ApiFactory.TenantA,
            Name = "erp-vendors-" + destination.ToString("N")[..8],
            Transport = OutputTransports.Http,
            ConfigJson = """{"urlTemplate":"https://erp.example/vendors?vat={{output.vat}}"}""",
            CreatedBy = "test",
        }));

        var missing = await PatchAsync(admin, supplier.Id, Lookup(Guid.NewGuid(), "{\"ext.vendor\": response.id}"));
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await missing.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors.Keys.ShouldContain("settings.pipelineOverrides[0].with.params.destinationId");

        var syntax = await PatchAsync(admin, supplier.Id, Lookup(destination, "{\"ext.vendor\": response.id"));
        syntax.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await syntax.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors.Keys.ShouldContain("settings.pipelineOverrides[0].with.params.response");

        var saved = await PatchAsync(admin, supplier.Id, Lookup(destination, "{\"ext.vendor\": response.id}"));
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
        var settings = (await saved.Content.ReadFromJsonAsync<SupplierResponse>(ApiFactory.Json, Ct))!.Settings;
        settings.GetProperty("pipelineOverrides")[0].GetProperty("with").GetProperty("kind").GetString().ShouldBe("http-lookup");
    }

    [Fact]
    public async Task Without_configurableSteps_overrides_are_refused_unless_sent_back_as_stored()
    {
        using var b = Admin(ApiFactory.TenantB);
        _factory.Run(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantB).Settings.Features!.SourceRouting = true);
        var created = await b.PostAsJsonAsync("/api/v1/suppliers", new CreateSupplierRequest("Off-" + Guid.NewGuid().ToString("N")[..8], null, null, null, null, null, null, null) { Settings = JsonDocument.Parse("""{"pipelineOverrides":[{"op":"disable","step":"llm"}]}""").RootElement }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await created.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors["settings.pipelineOverrides"].ShouldContain(e => e.Contains("configurableSteps", StringComparison.Ordinal));

        // Stored before the switch went off: sending it back unchanged is not a change.
        using var admin = Admin();
        var supplier = await SupplierAsync(admin, """{"pipelineOverrides":[{"op":"disable","step":"llm"}]}""");
        _factory.Run(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantA).Settings.Features!.ConfigurableSteps = false);
        try
        {
            (await PatchAsync(admin, supplier.Id, """{"pipelineOverrides":[{"op":"disable","step":"llm"}]}""")).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await PatchAsync(admin, supplier.Id, """{"pipelineOverrides":[{"op":"disable","step":"duplicate"}]}""")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            _factory.Run(db => db.Tenants.Single(t => t.Id == ApiFactory.TenantA).Settings.Features!.ConfigurableSteps = true);
        }
    }

    [Fact]
    public async Task Group_settings_take_overrides_too_and_they_apply_before_the_supplier_s()
    {
        using var admin = Admin();
        var group = await admin.PostAsJsonAsync("/api/v1/supplier-groups", new CreateSupplierGroupRequest("Group-" + Guid.NewGuid().ToString("N")[..8], null, JsonDocument.Parse("""{"pipelineOverrides":[{"op":"param","step":"llm","params":{"effort":"low"}},{"op":"disable","step":"duplicate"}]}""").RootElement), Ct);
        group.StatusCode.ShouldBe(HttpStatusCode.Created, await group.Content.ReadAsStringAsync(Ct));
        var groupId = (await group.Content.ReadFromJsonAsync<SupplierGroupResponse>(ApiFactory.Json, Ct))!.Id;
        var supplier = await SupplierAsync(admin, """{"pipelineOverrides":[{"op":"param","step":"llm","params":{"effort":"high"}}]}""");
        (await admin.PatchAsJsonAsync($"/api/v1/suppliers/{supplier.Id}", new PatchSupplierRequest(null, null, null, null, null, null, null, null) { GroupId = groupId }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var resolved = (await admin.GetFromJsonAsync<ResolvedPipelineResponse>($"/api/v1/document-types/Invoice/resolved-pipeline?supplierId={supplier.Id}", ApiFactory.Json, Ct))!;
        resolved.Steps.Single(s => s.Id == "llm").Params.GetProperty("effort").GetString().ShouldBe("high");
        resolved.Steps.Single(s => s.Id == "duplicate").Source.ShouldBe(PipelineSources.Group);

        var bad = await admin.PatchAsJsonAsync($"/api/v1/supplier-groups/{groupId}", new PatchSupplierGroupRequest(null, null, JsonDocument.Parse("""{"pipelineOverrides":[{"op":"disable","step":"classify.identification"}]}""").RootElement), Ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_settings_carry_the_configurableSteps_switch()
    {
        using var b = Admin(ApiFactory.TenantB);
        var current = (await b.GetFromJsonAsync<TenantSettingsResponse>("/api/v1/settings", ApiFactory.Json, Ct))!.Settings;
        current.Features!.ConfigurableSteps.ShouldBeFalse();

        var saved = await b.PutAsJsonAsync("/api/v1/settings", current with { Features = current.Features with { ConfigurableSteps = true } }, Ct);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await saved.Content.ReadFromJsonAsync<TenantSettingsResponse>(ApiFactory.Json, Ct))!.Settings.Features!.ConfigurableSteps.ShouldBeTrue();
        (await b.PutAsJsonAsync("/api/v1/settings", current, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Llm_budgets_are_replaced_as_a_whole_checked_and_shown_with_their_spend()
    {
        using var admin = Admin(ApiFactory.TenantB);
        var empty = (await admin.GetFromJsonAsync<LlmBudgetsResponse>("/api/v1/llm-budgets", ApiFactory.Json, Ct))!;
        empty.Budgets.ShouldBeEmpty();

        foreach (var bad in new[]
        {
            new LlmBudgetDto("week", 1m),
            new LlmBudgetDto("day", -1m),
            new LlmBudgetDto("day", 1.00001m),
            new LlmBudgetDto("day", 1m, "Ocr"),
        })
        {
            (await admin.PutAsJsonAsync("/api/v1/llm-budgets", new PutLlmBudgetsRequest([bad]), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, bad.ToString());
        }

        (await admin.PutAsJsonAsync("/api/v1/llm-budgets", new PutLlmBudgetsRequest([new("day", 1m), new("day", 2m)]), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "one budget per period and stage");

        var saved = await admin.PutAsJsonAsync("/api/v1/llm-budgets", new PutLlmBudgetsRequest([new("month", 50m), new("day", 0m, "Extract"), new("document", 0.25m)]), Ct);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
        var budgets = (await saved.Content.ReadFromJsonAsync<LlmBudgetsResponse>(ApiFactory.Json, Ct))!.Budgets;
        budgets.Select(x => (x.Period, x.Stage, x.LimitUsd)).ShouldBe([("day", "Extract", 0m), ("month", null, 50m), ("document", null, 0.25m)]);
        budgets.Single(x => x.Period == "month").SpentUsd.ShouldBe(0m);
        budgets.Single(x => x.Period == "document").SpentUsd.ShouldBeNull();

        (await admin.PutAsJsonAsync("/api/v1/llm-budgets", new PutLlmBudgetsRequest([new("month", 60m)]), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetFromJsonAsync<LlmBudgetsResponse>("/api/v1/llm-budgets", ApiFactory.Json, Ct))!.Budgets.ShouldHaveSingleItem().LimitUsd.ShouldBe(60m);

        // Tenant A sees none of tenant B's budgets, and a reviewer may not read or set them.
        using var a = Admin();
        (await a.GetFromJsonAsync<LlmBudgetsResponse>("/api/v1/llm-budgets", ApiFactory.Json, Ct))!.Budgets.ShouldNotContain(x => x.LimitUsd == 60m);
        using var reviewer = Reviewer();
        (await reviewer.GetAsync("/api/v1/llm-budgets", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await reviewer.PutAsJsonAsync("/api/v1/llm-budgets", new PutLlmBudgetsRequest([]), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await admin.PutAsJsonAsync("/api/v1/llm-budgets", new PutLlmBudgetsRequest([]), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<SupplierResponse> SupplierAsync(HttpClient client, string? settings)
    {
        var request = new CreateSupplierRequest("Steps-" + Guid.NewGuid().ToString("N")[..10], null, null, null, null, null, null, null)
        {
            Settings = settings is null ? null : JsonDocument.Parse(settings).RootElement,
        };
        var response = await client.PostAsJsonAsync("/api/v1/suppliers", request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<SupplierResponse>(ApiFactory.Json, Ct))!;
    }

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid supplierId, string settings) =>
        client.PatchAsJsonAsync($"/api/v1/suppliers/{supplierId}", new PatchSupplierRequest(null, null, null, null, null, null, null, null) { Settings = JsonDocument.Parse(settings).RootElement }, Ct);

    private static string Lookup(Guid destination, string response) => new JsonObject
    {
        ["pipelineOverrides"] = new JsonArray(new JsonObject
        {
            ["op"] = "insertAfter",
            ["step"] = "map",
            ["with"] = new JsonObject
            {
                ["id"] = "erpVendor",
                ["kind"] = "http-lookup",
                ["params"] = new JsonObject { ["destinationId"] = destination.ToString("D"), ["request"] = "{\"vat\": supplier.vatId}", ["response"] = response },
            },
        }),
    }.ToJsonString();
}
