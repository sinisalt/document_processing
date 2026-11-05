using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Api.Auth;
using DocProc.Application.Configuration;
using DocProc.Application.Outputs;
using DocProc.Definitions;
using DocProc.Definitions.Pipelines;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Llm;
using DocProc.Infrastructure.Outputs.Lookups;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using DocProc.Llm;
using DocProc.Llm.Gateway;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

/// <summary>One step kind of the catalogue (docs/11 §10.2 <c>GET /step-kinds</c>): what the UI renders a parameter form from.</summary>
public sealed record StepKindResponse(string Kind, string Phase, string Description, JsonElement ParamsSchema, bool Fixed, bool Insertable, bool Runs, string DefaultId);

/// <summary>One step of a resolved pipeline: its id, kind, phase, parameters, whether it runs, and the layer that last changed it.</summary>
public sealed record ResolvedStepResponse(string Id, string Kind, string Phase, JsonElement Params, bool Enabled, string Source, bool Fixed, bool Runs);

/// <summary>A refusal of a layer of overrides: where, and why.</summary>
public sealed record PipelineProblemResponse(string Path, string Message);

/// <summary>
/// The pipeline a document of the type would run (docs/11 §18 <c>GET /document-types/{key}/resolved-pipeline</c>):
/// its hash, whether the tenant is on <c>configurableSteps</c>, every step per phase, the overrides skipped (named a step
/// this pipeline does not have) and, when the layers were refused, why (the type's own steps run then).
/// </summary>
public sealed record ResolvedPipelineResponse(string Hash, bool Configurable, bool Overridden, IReadOnlyList<ResolvedStepResponse> Steps, IReadOnlyList<string> Skipped, IReadOnlyList<PipelineProblemResponse> Refused);

/// <summary>A pipeline as it ran (<c>GET /pipelines/{hash}</c>): the steps a document with this <c>pipelineHash</c> ran.</summary>
public sealed record PipelineSnapshotResponse(string Hash, DateTimeOffset CreatedAt, JsonElement Steps);

/// <summary>One LLM budget (docs/11 §16): <c>day</c>, <c>month</c> (UTC) or <c>document</c>; every stage, or one (<c>Classify</c>, <c>Split</c>, <c>Extract</c>, <c>Synthesize</c>).</summary>
public sealed record LlmBudgetDto(string Period, decimal LimitUsd, string? Stage = null);

/// <summary>A budget with what was spent in its current period (a document budget has no period total).</summary>
public sealed record LlmBudgetResponse(string Period, decimal LimitUsd, string? Stage, decimal? SpentUsd, DateTimeOffset? PeriodStart);

public sealed record LlmBudgetsResponse(IReadOnlyList<LlmBudgetResponse> Budgets);

/// <summary>The tenant's LLM budgets, as a whole: the list replaces the stored one (an empty list removes every budget).</summary>
public sealed record PutLlmBudgetsRequest(IReadOnlyList<LlmBudgetDto>? Budgets);

/// <summary>One rule kind of this deployment (<c>GET /rule-kinds</c>, docs/11 §18): what the type editor's rule form is drawn from.</summary>
public sealed record RuleKindResponse(string Kind, string Phase, string Description, JsonElement ParamsSchema, IReadOnlyList<string> PathParams, IReadOnlyList<string> ExpressionParams);

/// <summary>
/// <c>POST /pipelines/lookups:test</c>: an <c>http-lookup</c> step's parameters, tried against one document (the
/// processing UI's "Test call"). <c>StepId</c> names the step in the request headers (default <c>lookupTest</c>).
/// </summary>
public sealed record LookupTestRequest(Guid DocumentId, JsonElement Params, string? StepId = null);

/// <summary>
/// What the test call gave: whether it succeeded, the field assignments the response mapping made (nothing is written
/// to the document and nothing is stored), or the reason it failed with the destination's status and the attempts.
/// </summary>
public sealed record LookupTestResponse(bool Succeeded, JsonElement Assignments, string? Reason, string? Message, int? HttpStatus, int Attempts);

/// <summary>
/// The configurable steps of P5 (docs/11 §10): the step-kind catalogue, the resolved pipeline of a type (for a supplier
/// and a layout version), the pipelines documents ran (by hash), and the tenant's LLM budgets (§16).
/// </summary>
public static class PipelineEndpoints
{
    /// <summary>The most budgets a tenant keeps.</summary>
    public const int MaxBudgets = 12;

    public const decimal MaxBudgetUsd = 1_000_000m;

    public static RouteGroupBuilder MapPipelines(this RouteGroupBuilder group)
    {
        group.MapGet("/step-kinds", () => TypedResults.Ok<IReadOnlyList<StepKindResponse>>(
                [.. PipelinePhases.All.SelectMany(StepKindCatalogue.OfPhase).Select(k => new StepKindResponse(k.Kind, k.Phase, k.Description, JsonSerializer.SerializeToElement(k.ParamsSchema), k.Fixed, k.Insertable, k.Runs, k.DefaultId))]))
            .RequireAuthorization(Policies.Read)
            .WithTags("Pipelines")
            .WithName("ListStepKinds")
            .WithSummary("The step kinds per phase with their parameter schemas (docs/11 §10.2).");

        group.MapGet("/document-types/{key}/resolved-pipeline", ResolvedAsync)
            .RequireAuthorization(Policies.Read)
            .WithTags("Pipelines")
            .WithName("GetResolvedPipeline")
            .WithSummary("The pipeline a document of the type runs, for a supplier and a layout version (docs/11 §10.1).");

        group.MapGet("/pipelines/{hash}", SnapshotAsync)
            .RequireAuthorization(Policies.Read)
            .WithTags("Pipelines")
            .WithName("GetPipelineSnapshot")
            .WithSummary("The steps of a pipeline documents ran, by its hash (Documents.pipelineHash).");

        group.MapGet("/rule-kinds", () => TypedResults.Ok<IReadOnlyList<RuleKindResponse>>(
                [.. RuleKinds.Available.Values.OrderBy(k => k.Phase, StringComparer.Ordinal).ThenBy(k => k.Kind, StringComparer.Ordinal)
                    .Select(k => new RuleKindResponse(k.Kind, k.Phase, k.Description, JsonSerializer.SerializeToElement(k.ParamsSchema), k.PathParams, RuleKinds.ExpressionParams.GetValueOrDefault(k.Kind) ?? []))]))
            .RequireAuthorization(Policies.Read)
            .WithTags("Pipelines")
            .WithName("ListRuleKinds")
            .WithSummary("The rule kinds a definition may name, with their parameter schemas (docs/11 §11, §18).");

        group.MapPost("/pipelines/lookups:test", TestLookupAsync)
            .RequireAuthorization(Policies.ConfigAdmin)
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024))
            .WithTags("Pipelines")
            .WithName("TestLookup")
            .WithSummary("Calls an http-lookup step's destination for one document and maps the answer, without writing or storing anything (docs/11 §10.2, §19).");

        var budgets = group.MapGroup("/llm-budgets").RequireAuthorization(Policies.TenantAdmin).WithTags("LLM");
        budgets.MapGet("/", ListBudgetsAsync).WithName("ListLlmBudgets").WithSummary("The tenant's LLM budgets with what was spent in each current period (docs/11 §16).");
        budgets.MapPut("/", PutBudgetsAsync).WithName("PutLlmBudgets").WithSummary("Replaces the tenant's LLM budgets; a call that would exceed one is not made (LLM_BUDGET_EXCEEDED).");
        return group;
    }

    private static async Task<Results<Ok<ResolvedPipelineResponse>, NotFound, ValidationProblem, ProblemHttpResult>> ResolvedAsync(
        string key,
        Guid? supplierId,
        Guid? layoutVersionId,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] IEffectiveConfigResolver configs,
        [FromServices] IPipelineConfigResolver pipelines,
        CancellationToken ct)
    {
        if (RequireTenant(tenant, "Pipelines") is { } problem)
        {
            return problem;
        }

        if (!DocumentTypeKey.TryParse(key, out var type) || type.IsUnknown)
        {
            return TypedResults.NotFound();
        }

        var errors = new Errors();
        if (supplierId is { } sid && !await db.Suppliers.AnyAsync(s => s.Id == sid, ct))
        {
            errors.Add("supplierId", $"No supplier {sid} in this tenant.");
        }

        if (layoutVersionId is { } vid && !await db.SupplierLayoutVersions.AnyAsync(v => v.Id == vid, ct))
        {
            errors.Add("layoutVersionId", $"No layout version {vid} in this tenant.");
        }

        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        var tenantId = tenant.TenantId!.Value;
        var config = await configs.ResolveAsync(new EffectiveConfigRequest(tenantId, type, supplierId), ct);
        if (config.Type is null)
        {
            return TypedResults.NotFound();
        }

        var resolved = await pipelines.ResolveAsync(new PipelineRequest(tenantId, config.Type, supplierId, layoutVersionId), ct);
        return TypedResults.Ok(Response(resolved));
    }

    internal static ResolvedPipelineResponse Response(PipelineConfig config)
    {
        var pipeline = config.Pipeline;
        return new ResolvedPipelineResponse(
            pipeline.Hash,
            config.Configurable,
            pipeline.Overridden,
            [.. pipeline.Steps.Select(s => new ResolvedStepResponse(s.Id, s.Kind, s.Phase, JsonSerializer.SerializeToElement(s.Params), s.Enabled, s.Source, s.Fixed, s.Runs))],
            [.. pipeline.Notes.Select(n => $"{n.Source}{(n.Reference is { } r ? ":" + r : string.Empty)}[{n.Index}]: {n.Message}")],
            [.. config.Problems.Select(p => new PipelineProblemResponse(p.Path, p.Message))]);
    }

    private static async Task<Results<Ok<LookupTestResponse>, NotFound, ValidationProblem, ProblemHttpResult>> TestLookupAsync(
        LookupTestRequest request,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IOutputRenderer renderer,
        [FromServices] IServiceProvider services,
        CancellationToken ct)
    {
        if (RequireTenant(tenant, "Pipelines") is { } problem)
        {
            return problem;
        }

        var tenantId = tenant.TenantId!.Value;
        if (!await features.IsEnabledAsync(tenantId, TenantFeature.ConfigurableSteps, ct))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Feature not enabled", detail: "The 'configurableSteps' feature is off for this tenant; a tenant admin switches it on in the settings (features.configurableSteps).");
        }

        var errors = new Errors();
        var stepId = request.StepId ?? "lookupTest";
        if (!PipelineResolver.IsStepId(stepId))
        {
            errors.Add("stepId", "A step id is a letter followed by letters, digits, '.', '_' or '-' (at most 64).");
        }

        var parameters = request.Params.ValueKind == JsonValueKind.Object ? JsonNode.Parse(request.Params.GetRawText()) as JsonObject : null;
        if (parameters is null)
        {
            errors.Add("params", "params is the step's parameter object.");
        }
        else
        {
            foreach (var invalid in PipelineResolver.CheckParams(StepKindCatalogue.HttpLookup, parameters, "params", partial: false))
            {
                errors.Add(invalid.Path, invalid.Message);
            }

            // The checks a supplier's override gets at save: an http destination of the tenant, expressions that parse.
            var asOverride = new PipelineOverride { Op = PipelineOverrideOps.InsertAfter, Step = "llm", With = new StepDefinition { Id = "lookupTest", Kind = StepKindCatalogue.HttpLookup, Params = parameters } };
            var checks = new Errors();
            await PipelineChecks.CheckLookupsAsync(db, renderer, [asOverride], "step", checks, ct);
            foreach (var (field, messages) in checks.Fields())
            {
                foreach (var message in messages)
                {
                    errors.Add(field.Replace("step[0].with.params", "params", StringComparison.Ordinal), message);
                }
            }
        }

        if (errors.ToProblem() is { } invalidRequest)
        {
            return invalidRequest;
        }

        var lookups = services.GetService<HttpLookupService>();
        var views = services.GetService<IDocumentDataViewBuilder>();
        if (lookups is null || views is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Outputs not available", detail: "This host has no outputs (destinations) to call.");
        }

        var view = await views.BuildAsync(new DataViewRequest(request.DocumentId), ct);
        if (view is null)
        {
            return TypedResults.NotFound();
        }

        var step = new ResolvedStep(stepId, StepKindCatalogue.HttpLookup, PipelinePhases.Extract, parameters!, Enabled: true, PipelineSources.Supplier, Fixed: false, Runs: true);
        var outcome = await lookups.RunAsync(new LookupCall(tenantId, request.DocumentId, view.DocumentVersion, view.TypeKey, step, view.View, view.Today) { DryRun = true }, ct);
        var assignments = new JsonObject(outcome.Assignments.Select(a => KeyValuePair.Create(a.Key, a.Value?.DeepClone())));
        return TypedResults.Ok(new LookupTestResponse(outcome.Succeeded, JsonSerializer.SerializeToElement(assignments), outcome.Reason, outcome.Message, outcome.HttpStatus, outcome.Attempts));
    }

    private static async Task<Results<Ok<PipelineSnapshotResponse>, NotFound, ProblemHttpResult>> SnapshotAsync(string hash, ITenantContext tenant, [FromServices] DocProcDbContext db, CancellationToken ct)
    {
        if (RequireTenant(tenant, "Pipelines") is { } problem)
        {
            return problem;
        }

        var row = hash.Length == 32 ? await db.PipelineSnapshots.AsNoTracking().FirstOrDefaultAsync(p => p.Hash == hash, ct) : null;
        if (row is null)
        {
            return TypedResults.NotFound();
        }

        using var steps = JsonDocument.Parse(row.StepsJson);
        return TypedResults.Ok(new PipelineSnapshotResponse(row.Hash, row.CreatedAt, steps.RootElement.Clone()));
    }

    private static async Task<Results<Ok<LlmBudgetsResponse>, ProblemHttpResult>> ListBudgetsAsync(ITenantContext tenant, [FromServices] DocProcDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (RequireTenant(tenant, "LLM budgets") is { } problem)
        {
            return problem;
        }

        return TypedResults.Ok(await BudgetsAsync(db, tenant.TenantId!.Value, clock.GetUtcNow(), ct));
    }

    private static async Task<Results<Ok<LlmBudgetsResponse>, ValidationProblem, ProblemHttpResult>> PutBudgetsAsync(
        PutLlmBudgetsRequest request,
        ITenantContext tenant,
        ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (RequireTenant(tenant, "LLM budgets") is { } problem)
        {
            return problem;
        }

        var errors = new Errors();
        var budgets = request.Budgets ?? [];
        if (budgets.Count > MaxBudgets)
        {
            errors.Add("budgets", $"A tenant keeps at most {MaxBudgets} budgets.");
        }

        var parsed = new List<(LlmBudgetPeriod Period, LlmStage? Stage, decimal Limit)>();
        for (var i = 0; i < budgets.Count && errors.Count == 0; i++)
        {
            var budget = budgets[i];
            var at = $"budgets[{i}]";
            if (!TryPeriod(budget.Period, out var period))
            {
                errors.Add($"{at}.period", "period is day, month or document.");
                continue;
            }

            LlmStage? stage = null;
            if (budget.Stage is { } stageName)
            {
                if (!Enum.TryParse<LlmStage>(stageName, ignoreCase: false, out var parsedStage) || !Enum.IsDefined(parsedStage))
                {
                    errors.Add($"{at}.stage", "stage is Classify, Split, Extract or Synthesize (or left out for every stage).");
                    continue;
                }

                stage = parsedStage;
            }

            if (budget.LimitUsd is < 0 or > MaxBudgetUsd || decimal.Round(budget.LimitUsd, 4) != budget.LimitUsd)
            {
                errors.Add($"{at}.limitUsd", $"limitUsd is 0 to {MaxBudgetUsd:0} USD with at most 4 decimals (0 keeps the stage from calling the LLM at all).");
                continue;
            }

            if (parsed.Any(p => p.Period == period && p.Stage == stage))
            {
                errors.Add(at, "Each period and stage has one budget.");
                continue;
            }

            parsed.Add((period, stage, budget.LimitUsd));
        }

        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        var tenantId = tenant.TenantId!.Value;
        var now = clock.GetUtcNow();
        var by = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub") ?? user.Identity?.Name;
        var stored = await db.LlmBudgets.ToListAsync(ct);
        foreach (var row in stored.Where(r => !parsed.Any(p => p.Period == r.Period && p.Stage == r.Stage)))
        {
            db.LlmBudgets.Remove(row);
        }

        foreach (var (period, stage, limit) in parsed)
        {
            var row = stored.FirstOrDefault(r => r.Period == period && r.Stage == stage);
            if (row is null)
            {
                db.LlmBudgets.Add(new LlmBudgetRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, Period = period, Stage = stage, LimitUsd = limit, UpdatedBy = by, CreatedAt = now, UpdatedAt = now });
            }
            else if (row.LimitUsd != limit)
            {
                row.LimitUsd = limit;
                row.UpdatedBy = by;
                row.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await BudgetsAsync(db, tenantId, now, ct));
    }

    private static async Task<LlmBudgetsResponse> BudgetsAsync(DocProcDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.LlmBudgets.AsNoTracking().ToListAsync(ct);
        var result = new List<LlmBudgetResponse>();
        foreach (var row in rows.OrderBy(r => r.Period).ThenBy(r => r.Stage is null ? -1 : (int)r.Stage.Value))
        {
            var spent = row.Period == LlmBudgetPeriod.Document ? (decimal?)null : await EfLlmBudgetGuard.SpentAsync(db, tenantId, row.ToDomain(), null, now, ct);
            result.Add(new LlmBudgetResponse(PeriodName(row.Period), row.LimitUsd, row.Stage?.ToString(), spent, EfLlmBudgetGuard.PeriodStart(row.Period, now)));
        }

        return new LlmBudgetsResponse(result);
    }

    private static bool TryPeriod(string? text, out LlmBudgetPeriod period)
    {
        switch (text)
        {
            case "day":
                period = LlmBudgetPeriod.Day;
                return true;
            case "month":
                period = LlmBudgetPeriod.Month;
                return true;
            case "document":
                period = LlmBudgetPeriod.Document;
                return true;
            default:
                period = default;
                return false;
        }
    }

    private static string PeriodName(LlmBudgetPeriod period) => period switch
    {
        LlmBudgetPeriod.Day => "day",
        LlmBudgetPeriod.Month => "month",
        _ => "document",
    };
}

/// <summary>
/// The save-time checks of <c>pipelineOverrides</c> that need the tenant's data (docs/11 §10.1, §10.2): an
/// <c>http-lookup</c> step names an http destination of the tenant, and its JSONata expressions parse (checked by the
/// sandboxed renderer, not run).
/// </summary>
internal static class PipelineChecks
{
    public static async Task CheckLookupsAsync(DocProcDbContext db, IOutputRenderer renderer, IReadOnlyList<PipelineOverride> overrides, string at, Errors errors, CancellationToken ct)
    {
        for (var i = 0; i < overrides.Count; i++)
        {
            if (overrides[i].With is not { Kind: StepKindCatalogue.HttpLookup, Params: { } parameters })
            {
                continue;
            }

            var path = $"{at}[{i}].with.params";
            if (parameters["destinationId"] is JsonValue idValue && idValue.TryGetValue<string>(out var idText) && Guid.TryParse(idText, out var destinationId)
                && !await db.OutputDestinations.AnyAsync(d => d.Id == destinationId && d.Transport == OutputTransports.Http, ct))
            {
                errors.Add($"{path}.destinationId", $"No http destination {destinationId} in this tenant.");
            }

            foreach (var name in new[] { "request", "response" })
            {
                if (parameters[name] is JsonValue expression && expression.TryGetValue<string>(out var text))
                {
                    var check = await renderer.CheckAsync(text, null, ct);
                    if (check.Error is { } error)
                    {
                        errors.Add($"{path}.{name}", $"The JSONata expression does not parse ({error.Kind}): {error.Message}");
                    }
                }
            }
        }
    }
}
