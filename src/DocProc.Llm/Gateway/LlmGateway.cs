using System.Diagnostics;
using DocProc.Llm.Providers;

namespace DocProc.Llm.Gateway;

/// <summary>
/// Resolves the provider for a tenant and stage, enforces the spend cap, calls the adapter and records usage
/// (docs/08 "Provider abstraction"). Rate limiting per config is applied by the worker's concurrency limiter around
/// this class; batch submission goes through <see cref="ILlmProvider"/> directly from the eval and backfill jobs.
/// </summary>
public sealed class LlmGateway(
    ILlmPolicyStore policies,
    ILlmCredentialStore credentials,
    IEnumerable<ILlmProviderFactory> factories,
    ILlmUsageRecorder usage,
    PriceTable prices,
    TimeProvider clock) : ILlmGateway
{
    private readonly IReadOnlyList<ILlmProviderFactory> _factories = factories.ToList();

    public async Task<LlmResult> CompleteAsync(Guid tenantId, LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var setup = await policies.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var resolved = LlmConfigResolver.Resolve(request.Stage, setup.Policy, setup.TenantConfigs, setup.PlatformDefault);

        if (resolved.Config.Limits.MonthlySpendCapUsd is { } cap && setup.SpentThisMonthUsd >= cap)
        {
            throw new LlmException(LlmErrorKind.Quota, $"Provider configuration {resolved.Config.Id} reached its monthly spend cap of {cap:0.00} USD.");
        }

        var factory = _factories.FirstOrDefault(f => f.Supports(resolved.Config.Kind))
            ?? throw new LlmException(LlmErrorKind.Unavailable, $"No adapter is registered for provider kind {resolved.Config.Kind}.");

        var credential = await credentials.GetAsync(resolved.Config, cancellationToken).ConfigureAwait(false);
        var provider = factory.Create(resolved.Config, credential);
        var effective = request with { Model = resolved.Model };
        var hash = RequestHasher.Hash(effective);
        var started = Stopwatch.GetTimestamp();

        try
        {
            var result = await provider.CompleteAsync(effective, cancellationToken).ConfigureAwait(false);
            var latency = result.Latency == TimeSpan.Zero ? Stopwatch.GetElapsedTime(started) : result.Latency;
            await usage.RecordAsync(
                Record(tenantId, effective, resolved, result.Usage, result.Model, result.StopReason, result.ProviderRequestId, latency, hash, error: null),
                cancellationToken).ConfigureAwait(false);
            return result with { Latency = latency };
        }
        catch (LlmException ex)
        {
            await usage.RecordAsync(
                Record(tenantId, effective, resolved, LlmUsage.Zero, effective.Model, "error", null, Stopwatch.GetElapsedTime(started), hash, ex.Kind),
                cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private LlmUsageRecord Record(
        Guid tenantId,
        LlmRequest request,
        ResolvedProvider resolved,
        LlmUsage used,
        string model,
        string stopReason,
        string? providerRequestId,
        TimeSpan latency,
        string hash,
        LlmErrorKind? error) =>
        new(
            tenantId,
            request.DocumentId,
            request.Stage,
            resolved.Config.Id,
            resolved.KeyOwner,
            resolved.Config.Kind,
            model,
            request.Prompt.CacheKey,
            request.OutputSchema?.ToString(),
            used,
            prices.Estimate(model, used, request.Options.AllowBatch),
            latency,
            stopReason,
            providerRequestId,
            hash,
            clock.GetUtcNow(),
            error);
}
