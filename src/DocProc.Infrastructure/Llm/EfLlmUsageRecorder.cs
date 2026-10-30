using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm.Gateway;

namespace DocProc.Infrastructure.Llm;

/// <summary>
/// <see cref="ILlmUsageRecorder"/> writing one <see cref="LlmUsageEntry"/> per call (docs/08 "Cost attribution"). It
/// opens its own platform-scoped context so a row is written whatever scope the calling stage runs in, and before the
/// stage applies the result, which is what makes a crash between call and write reconcilable (docs/03 "Idempotency").
/// </summary>
public sealed class EfLlmUsageRecorder(ISystemDbContextFactory contexts) : ILlmUsageRecorder
{
    public async Task RecordAsync(LlmUsageRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var db = contexts.CreatePlatformScoped();
        db.LlmUsage.Add(new LlmUsageEntry
        {
            TenantId = record.TenantId,
            DocumentId = record.DocumentId,
            Stage = record.Stage,
            ConfigId = record.ConfigId,
            KeyOwner = record.KeyOwner,
            Provider = record.Provider,
            Model = record.Model,
            PromptVersion = record.PromptVersion,
            SchemaVersion = record.SchemaVersion,
            InputTokens = record.Usage.InputTokens,
            OutputTokens = record.Usage.OutputTokens,
            CacheReadTokens = record.Usage.CacheReadTokens,
            CacheWriteTokens = record.Usage.CacheWriteTokens,
            ReasoningTokens = record.Usage.ReasoningTokens,
            EstimatedCostUsd = record.EstimatedCostUsd,
            LatencyMs = (int)Math.Min(int.MaxValue, Math.Max(0, record.Latency.TotalMilliseconds)),
            StopReason = record.StopReason,
            ProviderRequestId = record.ProviderRequestId,
            RequestHash = record.RequestHash,
            Error = record.Error,
            At = record.At,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
