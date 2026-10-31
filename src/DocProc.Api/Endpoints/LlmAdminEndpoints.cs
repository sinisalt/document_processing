using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DocProc.Api.Auth;
using DocProc.Application.Abstractions;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using DocProc.Llm;
using DocProc.Llm.Providers;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

/// <summary>A provider configuration without its credential: only the last four characters identify the key (docs/08 "Key handling").</summary>
public sealed record LlmProviderResponse(
    Guid Id,
    string Scope,
    Guid? TenantId,
    string Kind,
    string? Endpoint,
    string? ApiVersion,
    string AuthType,
    IReadOnlyDictionary<string, string> Models,
    decimal? MonthlySpendCapUsd,
    string DataResidency,
    string Status,
    string? KeyLast4,
    DateTimeOffset? ValidatedAt,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static LlmProviderResponse From(LlmProviderConfigRecord c) => new(
        c.Id, c.Scope.ToString(), c.TenantId, c.Kind.ToString(), c.Endpoint, c.ApiVersion, c.AuthType.ToString(),
        new Dictionary<string, string>(c.Models, StringComparer.Ordinal), c.MonthlySpendCapUsd, c.DataResidency.ToString(), c.Status.ToString(),
        c.KeyLast4, c.ValidatedAt, c.LastError, c.CreatedAt, c.UpdatedAt);
}

/// <summary><see cref="ApiKey"/> is write-only: it goes to the secret store and is never returned or persisted in SQL.</summary>
public sealed record CreateLlmProviderRequest(
    string Kind,
    string? Endpoint,
    string? ApiVersion,
    string ApiKey,
    IReadOnlyDictionary<string, string>? Models,
    decimal? MonthlySpendCapUsd,
    string? DataResidency);

public sealed record LlmPolicyResponse(Guid? DefaultConfigId, IReadOnlyDictionary<string, Guid> StageOverrides, bool FallbackToPlatformKey, bool AllowPlatformKeyAtAll)
{
    internal static LlmPolicyResponse From(TenantLlmPolicyRecord p) =>
        new(p.DefaultConfigId, new Dictionary<string, Guid>(p.StageOverrides, StringComparer.Ordinal), p.FallbackToPlatformKey, p.AllowPlatformKeyAtAll);

    internal static LlmPolicyResponse Default() => new(null, new Dictionary<string, Guid>(StringComparer.Ordinal), FallbackToPlatformKey: false, AllowPlatformKeyAtAll: true);
}

public sealed record PutLlmPolicyRequest(Guid? DefaultConfigId, IReadOnlyDictionary<string, Guid>? StageOverrides, bool FallbackToPlatformKey);

/// <summary>Usage of one group (a day, a stage, a model or a tenant) in the requested window.</summary>
public sealed record LlmUsageBucket(string Key, int Calls, int Errors, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, long ReasoningTokens, decimal EstimatedCostUsd);

public sealed record LlmUsageResponse(DateOnly From, DateOnly To, string GroupBy, IReadOnlyList<LlmUsageBucket> Buckets, LlmUsageBucket Total);

/// <summary>
/// BYOK for tenants and the platform's own provider configurations (docs/08). Tenant endpoints run on the request
/// context for the resolved tenant; the platform ones (<c>/llm/*</c>) on a platform-scoped context. The credential
/// itself goes to <see cref="ISecretStore"/> under <c>tenant/{tenantId}/{configId}</c> or <c>platform/{configId}</c>;
/// SQL keeps the reference, the last four characters and a fingerprint, and nothing here ever returns the key.
/// </summary>
public static class LlmAdminEndpoints
{
    private const int DefaultUsageWindowDays = 30;
    private const int FingerprintLength = 16;

    /// <summary>Shorter than this and <c>keyLast4</c> would reveal most of the key; no real provider key is this short.</summary>
    public const int MinApiKeyLength = 8;

    public const int MaxApiKeyLength = 4096;
    private static readonly string[] TenantGroupings = ["day", "stage", "model"];
    private static readonly string[] PlatformGroupings = ["tenant", "model", "day"];

    public static RouteGroupBuilder MapLlmAdmin(this RouteGroupBuilder group)
    {
        MapTenantPolicy(group);
        MapTenantProviders(group);
        MapTenantUsage(group);
        MapPlatform(group);
        return group;
    }

    private static void MapTenantPolicy(RouteGroupBuilder group)
    {
        var policy = group.MapGroup("/llm-policy").RequireAuthorization(Policies.TenantAdmin).WithTags("LLM");

        policy.MapGet("/", async Task<Results<Ok<LlmPolicyResponse>, ProblemHttpResult>> ([FromServices] DocProcDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "LLM policies") is { } problem)
            {
                return problem;
            }

            var row = await db.TenantLlmPolicies.AsNoTracking().SingleOrDefaultAsync(ct);
            return TypedResults.Ok(row is null ? LlmPolicyResponse.Default() : LlmPolicyResponse.From(row));
        })
        .WithName("GetLlmPolicy")
        .WithSummary("Which provider configuration each stage of the current tenant uses and whether the platform key may stand in.");

        policy.MapPut("/", async Task<Results<Ok<LlmPolicyResponse>, ValidationProblem, ProblemHttpResult>> (PutLlmPolicyRequest request, [FromServices] DocProcDbContext db, ITenantContext tenant, TimeProvider clock, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "LLM policies") is { } problem)
            {
                return problem;
            }

            var errors = new Errors();
            var overrides = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var (stageName, configId) in request.StageOverrides ?? new Dictionary<string, Guid>(StringComparer.Ordinal))
            {
                if (!TryParseEnum<LlmStage>(stageName, out var stage))
                {
                    errors.Add("stageOverrides", $"Unknown stage '{stageName}'. Allowed: {Names<LlmStage>()}.");
                    break;
                }

                overrides[stage.ToString()] = configId;
            }

            var referenced = overrides.Values.ToHashSet();
            if (request.DefaultConfigId is { } defaultId)
            {
                referenced.Add(defaultId);
            }

            if (referenced.Count > 0)
            {
                var tenantId = tenant.TenantId!.Value;
                var owned = await db.LlmProviderConfigs.Where(c => c.TenantId == tenantId && referenced.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct);
                var foreign = referenced.Except(owned).ToList();
                if (foreign.Count > 0)
                {
                    errors.Add("configId", $"Configuration(s) {string.Join(", ", foreign)} do not belong to this tenant.");
                }
            }

            if (errors.ToProblem() is { } invalid)
            {
                return invalid;
            }

            var row = await db.TenantLlmPolicies.SingleOrDefaultAsync(ct);
            if (row is null)
            {
                if (await CurrentTenantAsync(db, ct) is null)
                {
                    return UnknownTenant();
                }

                row = new TenantLlmPolicyRecord { TenantId = tenant.TenantId!.Value };
                db.TenantLlmPolicies.Add(row);
            }

            row.DefaultConfigId = request.DefaultConfigId;
            row.StageOverrides = overrides;
            row.FallbackToPlatformKey = request.FallbackToPlatformKey;
            row.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(LlmPolicyResponse.From(row));
        })
        .WithName("PutLlmPolicy")
        .WithSummary("Sets the default configuration, per-stage overrides and platform-key fallback; every id must be one of the tenant's own configurations.");
    }

    private static void MapTenantProviders(RouteGroupBuilder group)
    {
        var providers = group.MapGroup("/llm-providers").RequireAuthorization(Policies.TenantAdmin).WithTags("LLM");

        providers.MapGet("/", async Task<Results<Ok<IReadOnlyList<LlmProviderResponse>>, ProblemHttpResult>> ([FromServices] DocProcDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "LLM providers") is { } problem)
            {
                return problem;
            }

            var tenantId = tenant.TenantId!.Value;
            var rows = await db.LlmProviderConfigs.AsNoTracking().Where(c => c.TenantId == tenantId).ToListAsync(ct);
            return TypedResults.Ok<IReadOnlyList<LlmProviderResponse>>(rows.OrderBy(c => c.CreatedAt).Select(LlmProviderResponse.From).ToList());
        })
        .WithName("ListLlmProviders")
        .WithSummary("The tenant's own (BYOK) provider configurations, without credentials.");

        providers.MapPost("/", async Task<Results<Created<LlmProviderResponse>, ValidationProblem, ProblemHttpResult>> (CreateLlmProviderRequest request, ClaimsPrincipal user, [FromServices] DocProcDbContext db, ITenantContext tenant, ISecretStore secrets, TimeProvider clock, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "LLM providers") is { } problem)
            {
                return problem;
            }

            if (Validate(request, ConfigScope.Tenant) is { } invalid)
            {
                return invalid;
            }

            if (await CurrentTenantAsync(db, ct) is null)
            {
                return UnknownTenant();
            }

            var tenantId = tenant.TenantId!.Value;
            var record = Build(request, ConfigScope.Tenant, tenantId, Actor(user), clock.GetUtcNow());
            record.SecretRef = $"tenant/{tenantId}/{record.Id}";
            await StoreAsync(db, record, request.ApiKey, secrets, ct);
            return TypedResults.Created($"/api/v1/llm-providers/{record.Id}", LlmProviderResponse.From(record));
        })
        .WithName("CreateLlmProvider")
        .WithSummary("Adds a BYOK provider: the key goes to the secret store, SQL keeps its reference, last four characters and fingerprint.");

        providers.MapPost("/{id:guid}:disable", (Guid id, [FromServices] DocProcDbContext db, ITenantContext tenant, TimeProvider clock, CancellationToken ct) =>
                SetTenantStatusAsync(id, ProviderConfigStatus.Disabled, db, tenant, clock, ct))
            .WithName("DisableLlmProvider")
            .WithSummary("Disables a configuration; stages that resolve to it wait or fall back per the policy.");

        providers.MapPost("/{id:guid}:enable", (Guid id, [FromServices] DocProcDbContext db, ITenantContext tenant, TimeProvider clock, CancellationToken ct) =>
                SetTenantStatusAsync(id, ProviderConfigStatus.Active, db, tenant, clock, ct))
            .WithName("EnableLlmProvider")
            .WithSummary("Re-enables a disabled or invalid configuration.");

        providers.MapDelete("/{id:guid}", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (Guid id, [FromServices] DocProcDbContext db, ITenantContext tenant, ISecretStore secrets, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "LLM providers") is { } problem)
            {
                return problem;
            }

            var tenantId = tenant.TenantId!.Value;
            var record = await db.LlmProviderConfigs.SingleOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct);
            if (record is null)
            {
                return TypedResults.NotFound();
            }

            var policy = await db.TenantLlmPolicies.AsNoTracking().SingleOrDefaultAsync(ct);
            if (policy is not null && (policy.DefaultConfigId == id || policy.StageOverrides.ContainsValue(id)))
            {
                return Conflict("Configuration in use", "The LLM policy references this configuration; point the policy elsewhere first.");
            }

            // The row first, the key second: a failed save keeps a working configuration, a failed delete leaves an
            // unreferenced secret, never a configuration whose reference points at nothing.
            db.LlmProviderConfigs.Remove(record);
            await db.SaveChangesAsync(ct);
            if (record.SecretRef is { } reference)
            {
                await secrets.DeleteAsync(reference, ct);
            }

            return TypedResults.NoContent();
        })
        .WithName("DeleteLlmProvider")
        .WithSummary("Deletes a configuration the policy does not reference, and its key from the secret store; usage rows keep its id for cost history.");
    }

    private static void MapTenantUsage(RouteGroupBuilder group)
    {
        group.MapGet("/llm-usage", async Task<Results<Ok<LlmUsageResponse>, ValidationProblem, ProblemHttpResult>> (DateOnly? from, DateOnly? to, string? groupBy, [FromServices] DocProcDbContext db, ITenantContext tenant, TimeProvider clock, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "LLM usage") is { } problem)
            {
                return problem;
            }

            var window = Window(from, to, groupBy ?? "day", TenantGroupings, clock);
            if (window.Problem is not null)
            {
                return window.Problem;
            }

            return TypedResults.Ok(await AggregateAsync(db, window, ct));
        })
        .RequireAuthorization(Policies.Read)
        .WithTags("LLM")
        .WithName("GetLlmUsage")
        .WithSummary("Calls, tokens and estimated cost of the current tenant per day, stage or model (inclusive UTC dates; the last 30 days by default).");
    }

    private static void MapPlatform(RouteGroupBuilder group)
    {
        var llm = group.MapGroup("/llm").RequireAuthorization(Policies.PlatformAdmin).WithTags("LLM");

        llm.MapGet("/providers", async Task<Ok<IReadOnlyList<LlmProviderResponse>>> ([FromServices] ISystemDbContextFactory factory, CancellationToken ct) =>
        {
            await using var db = factory.CreatePlatformScoped();
            var rows = await db.LlmProviderConfigs.AsNoTracking().Where(c => c.Scope == ConfigScope.Platform).ToListAsync(ct);
            return TypedResults.Ok<IReadOnlyList<LlmProviderResponse>>(rows.OrderBy(c => c.CreatedAt).Select(LlmProviderResponse.From).ToList());
        })
        .WithName("ListPlatformLlmProviders")
        .WithSummary("The platform's default provider configurations (per data residency), without credentials.");

        llm.MapPost("/providers", async Task<Results<Created<LlmProviderResponse>, ValidationProblem>> (CreateLlmProviderRequest request, ClaimsPrincipal user, [FromServices] ISystemDbContextFactory factory, ISecretStore secrets, TimeProvider clock, CancellationToken ct) =>
        {
            if (Validate(request, ConfigScope.Platform) is { } invalid)
            {
                return invalid;
            }

            await using var db = factory.CreatePlatformScoped();
            var record = Build(request, ConfigScope.Platform, tenantId: null, Actor(user), clock.GetUtcNow());
            record.SecretRef = $"platform/{record.Id}";
            await StoreAsync(db, record, request.ApiKey, secrets, ct);
            return TypedResults.Created($"/api/v1/llm/providers/{record.Id}", LlmProviderResponse.From(record));
        })
        .WithName("CreatePlatformLlmProvider")
        .WithSummary("Adds a platform provider configuration; the key goes to the secret store under platform scope.");

        llm.MapPost("/providers/{id:guid}:disable", (Guid id, [FromServices] ISystemDbContextFactory factory, TimeProvider clock, CancellationToken ct) =>
                SetPlatformStatusAsync(id, ProviderConfigStatus.Disabled, factory, clock, ct))
            .WithName("DisablePlatformLlmProvider")
            .WithSummary("Kill switch: no call goes to this platform configuration until it is enabled again.");

        llm.MapPost("/providers/{id:guid}:enable", (Guid id, [FromServices] ISystemDbContextFactory factory, TimeProvider clock, CancellationToken ct) =>
                SetPlatformStatusAsync(id, ProviderConfigStatus.Active, factory, clock, ct))
            .WithName("EnablePlatformLlmProvider")
            .WithSummary("Re-enables a platform configuration.");

        llm.MapGet("/usage", async Task<Results<Ok<LlmUsageResponse>, ValidationProblem>> (DateOnly? from, DateOnly? to, string? groupBy, [FromServices] ISystemDbContextFactory factory, TimeProvider clock, CancellationToken ct) =>
        {
            var window = Window(from, to, groupBy ?? "tenant", PlatformGroupings, clock);
            if (window.Problem is not null)
            {
                return window.Problem;
            }

            await using var db = factory.CreatePlatformScoped();
            return TypedResults.Ok(await AggregateAsync(db, window, ct));
        })
        .WithName("GetPlatformLlmUsage")
        .WithSummary("Calls, tokens and estimated cost across tenants per tenant, model or day (inclusive UTC dates; the last 30 days by default).");
    }

    /// <summary>
    /// A tenant's endpoint must also pass <see cref="TenantEndpointGuard"/> (public https hosts only): the worker sends
    /// the tenant's key and documents to it from inside the platform's network. A platform configuration may point
    /// anywhere, plain http and private addresses included, for self-hosted models (docs/08 "Endpoint restrictions").
    /// </summary>
    private static ValidationProblem? Validate(CreateLlmProviderRequest request, ConfigScope scope)
    {
        var errors = new Errors().Optional("apiVersion", request.ApiVersion, 32);
        if (!TryParseEnum<LlmProviderKind>(request.Kind, out var kind) || kind == LlmProviderKind.Replay)
        {
            errors.Add("kind", $"Unknown provider kind. Allowed: {string.Join(", ", Enum.GetNames<LlmProviderKind>().Where(n => n != nameof(LlmProviderKind.Replay)))}.");
        }

        if (request.Endpoint is not null && (request.Endpoint.Length > 500 || !IsAbsoluteHttpUri(request.Endpoint)))
        {
            errors.Add("endpoint", "endpoint must be an absolute http(s) URL of at most 500 characters.");
        }
        else if (scope == ConfigScope.Tenant && request.Endpoint is not null && TenantEndpointGuard.Validate(request.Endpoint.Trim()) is { } refused)
        {
            errors.Add("endpoint", refused);
        }

        if (string.IsNullOrWhiteSpace(request.ApiKey) || request.ApiKey.Length is < MinApiKeyLength or > MaxApiKeyLength || request.ApiKey.Any(char.IsWhiteSpace))
        {
            errors.Add("apiKey", $"apiKey is required, between {MinApiKeyLength} and {MaxApiKeyLength} characters, and may not contain whitespace.");
        }

        if (request.Models is null || request.Models.Count == 0)
        {
            errors.Add("models", $"At least one stage model is required. Stages: {Names<LlmStage>()}.");
        }
        else
        {
            var seen = new HashSet<LlmStage>();
            foreach (var (stageName, model) in request.Models)
            {
                if (!TryParseEnum<LlmStage>(stageName, out var stage))
                {
                    errors.Add("models", $"Unknown stage '{stageName}'. Allowed: {Names<LlmStage>()}.");
                    break;
                }

                if (!seen.Add(stage))
                {
                    // Stage names are matched case-insensitively, so "extract" and "Extract" would silently collapse
                    // into one entry and the client could not tell which model won.
                    errors.Add("models", $"Stage '{stage}' is listed more than once.");
                    break;
                }

                if (string.IsNullOrWhiteSpace(model) || model.Length > 100)
                {
                    errors.Add("models", $"The model for stage '{stageName}' is required and at most 100 characters.");
                    break;
                }
            }
        }

        if (request.MonthlySpendCapUsd is { } cap && cap <= 0)
        {
            errors.Add("monthlySpendCapUsd", "The spend cap must be a positive amount.");
        }

        if (request.DataResidency is not null && !TryParseEnum<DataResidency>(request.DataResidency, out _))
        {
            errors.Add("dataResidency", $"Unknown data residency. Allowed: {Names<DataResidency>()}.");
        }

        return errors.ToProblem();
    }

    /// <summary>The record from a validated request; the secret reference is set by the caller because it carries the scope.</summary>
    private static LlmProviderConfigRecord Build(CreateLlmProviderRequest request, ConfigScope scope, Guid? tenantId, string? createdBy, DateTimeOffset now)
    {
        var models = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (stageName, model) in request.Models!)
        {
            models[ParseEnum<LlmStage>(stageName).ToString()] = model.Trim();
        }

        var kind = ParseEnum<LlmProviderKind>(request.Kind);
        var residency = request.DataResidency is null ? DataResidency.Eu : ParseEnum<DataResidency>(request.DataResidency);
        var key = request.ApiKey;
        return new LlmProviderConfigRecord
        {
            Id = Guid.CreateVersion7(),
            Scope = scope,
            TenantId = tenantId,
            Kind = kind,
            Endpoint = TrimToNull(request.Endpoint),
            ApiVersion = TrimToNull(request.ApiVersion),
            AuthType = LlmAuthType.ApiKey,
            KeyLast4 = key[^4..],
            KeyFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..FingerprintLength],
            Models = models,
            MonthlySpendCapUsd = request.MonthlySpendCapUsd,
            DataResidency = residency,
            Status = ProviderConfigStatus.Active,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>The secret first, the row second: a failed save leaves an unreferenced secret, never a configuration without one.</summary>
    private static async Task StoreAsync(DocProcDbContext db, LlmProviderConfigRecord record, string apiKey, ISecretStore secrets, CancellationToken ct)
    {
        await secrets.SetAsync(record.SecretRef!, apiKey, ct);
        db.LlmProviderConfigs.Add(record);
        await db.SaveChangesAsync(ct);
    }

    private static async Task<Results<Ok<LlmProviderResponse>, NotFound, ProblemHttpResult>> SetTenantStatusAsync(Guid id, ProviderConfigStatus status, DocProcDbContext db, ITenantContext tenant, TimeProvider clock, CancellationToken ct)
    {
        if (RequireTenant(tenant, "LLM providers") is { } problem)
        {
            return problem;
        }

        var tenantId = tenant.TenantId!.Value;
        var record = await db.LlmProviderConfigs.SingleOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct);
        if (record is null)
        {
            return TypedResults.NotFound();
        }

        await SetStatusAsync(db, record, status, clock, ct);
        return TypedResults.Ok(LlmProviderResponse.From(record));
    }

    private static async Task<Results<Ok<LlmProviderResponse>, NotFound>> SetPlatformStatusAsync(Guid id, ProviderConfigStatus status, ISystemDbContextFactory factory, TimeProvider clock, CancellationToken ct)
    {
        await using var db = factory.CreatePlatformScoped();
        var record = await db.LlmProviderConfigs.SingleOrDefaultAsync(c => c.Id == id && c.Scope == ConfigScope.Platform, ct);
        if (record is null)
        {
            return TypedResults.NotFound();
        }

        await SetStatusAsync(db, record, status, clock, ct);
        return TypedResults.Ok(LlmProviderResponse.From(record));
    }

    private static async Task SetStatusAsync(DocProcDbContext db, LlmProviderConfigRecord record, ProviderConfigStatus status, TimeProvider clock, CancellationToken ct)
    {
        if (record.Status == status)
        {
            return;
        }

        record.Status = status;
        if (status == ProviderConfigStatus.Active)
        {
            record.LastError = null;
        }

        record.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private static string? Actor(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name;

    private sealed record UsageWindow(DateOnly From, DateOnly To, string GroupBy, ValidationProblem? Problem);

    private static UsageWindow Window(DateOnly? from, DateOnly? to, string groupBy, string[] allowed, TimeProvider clock)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var end = to ?? today;
        var start = from ?? end.AddDays(-DefaultUsageWindowDays);
        var grouping = groupBy.Trim().ToLowerInvariant();
        var errors = new Errors();
        if (start > end)
        {
            errors.Add("from", "from must not be after to.");
        }

        if (!allowed.Contains(grouping, StringComparer.Ordinal))
        {
            errors.Add("groupBy", $"Unknown grouping. Allowed: {string.Join(", ", allowed)}.");
        }

        return new UsageWindow(start, end, grouping, errors.ToProblem());
    }

    /// <summary>
    /// Filters the window in SQL (on SQL Server; the development SQLite provider cannot compare DateTimeOffset, so
    /// there the visible rows are filtered in memory) and groups in memory, so the grouping is identical on both
    /// providers. A window is at most a few thousand rows per tenant.
    /// </summary>
    private static async Task<LlmUsageResponse> AggregateAsync(DocProcDbContext db, UsageWindow window, CancellationToken ct)
    {
        var startAt = new DateTimeOffset(window.From.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var endAt = new DateTimeOffset(window.To.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var usage = db.LlmUsage.AsNoTracking();
        if (!UsesSqlite(db))
        {
            usage = usage.Where(u => u.At >= startAt && u.At < endAt);
        }

        var rows = await usage
            .Select(u => new { u.TenantId, u.At, u.Stage, u.Model, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens, u.ReasoningTokens, u.EstimatedCostUsd, u.Error })
            .ToListAsync(ct);

        var buckets = rows
            .Where(u => u.At >= startAt && u.At < endAt)
            .GroupBy(u => window.GroupBy switch
            {
                "day" => u.At.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "stage" => u.Stage.ToString(),
                "model" => u.Model,
                _ => u.TenantId.ToString(),
            }, StringComparer.Ordinal)
            .Select(g => new LlmUsageBucket(
                g.Key,
                g.Count(),
                g.Count(u => u.Error is not null),
                g.Sum(u => (long)u.InputTokens),
                g.Sum(u => (long)u.OutputTokens),
                g.Sum(u => (long)u.CacheReadTokens),
                g.Sum(u => (long)u.CacheWriteTokens),
                g.Sum(u => (long)u.ReasoningTokens),
                g.Sum(u => u.EstimatedCostUsd ?? 0m)))
            .OrderBy(b => b.Key, StringComparer.Ordinal)
            .ToList();

        var total = new LlmUsageBucket(
            "total",
            buckets.Sum(b => b.Calls),
            buckets.Sum(b => b.Errors),
            buckets.Sum(b => b.InputTokens),
            buckets.Sum(b => b.OutputTokens),
            buckets.Sum(b => b.CacheReadTokens),
            buckets.Sum(b => b.CacheWriteTokens),
            buckets.Sum(b => b.ReasoningTokens),
            buckets.Sum(b => b.EstimatedCostUsd));
        return new LlmUsageResponse(window.From, window.To, window.GroupBy, buckets, total);
    }
}
