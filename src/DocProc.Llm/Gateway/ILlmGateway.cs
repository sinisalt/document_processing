using DocProc.Domain.Documents;
using DocProc.Llm.Providers;

namespace DocProc.Llm.Gateway;

/// <summary>What the pipeline calls. It never sees a vendor, a key or a config.</summary>
public interface ILlmGateway
{
    Task<LlmResult> CompleteAsync(Guid tenantId, LlmRequest request, CancellationToken cancellationToken);
}

/// <summary>Everything the gateway needs to know about a tenant's provider setup, loaded per call.</summary>
public sealed record TenantProviderSetup(
    TenantLlmPolicy? Policy,
    IReadOnlyDictionary<Guid, LlmProviderConfig> TenantConfigs,
    LlmProviderConfig? PlatformDefault,
    decimal SpentThisMonthUsd);

public interface ILlmPolicyStore
{
    Task<TenantProviderSetup> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Resolves a config's secret reference to the credential, from Key Vault in production.</summary>
public interface ILlmCredentialStore
{
    Task<LlmCredential> GetAsync(LlmProviderConfig config, CancellationToken cancellationToken);
}

/// <summary>One row per call (docs/08 "Cost attribution and limits"). Never contains prompt or document content.</summary>
public sealed record LlmUsageRecord(
    Guid TenantId,
    Guid? DocumentId,
    LlmStage Stage,
    Guid ConfigId,
    KeyOwner KeyOwner,
    LlmProviderKind Provider,
    string Model,
    string PromptVersion,
    string? SchemaVersion,
    LlmUsage Usage,
    decimal? EstimatedCostUsd,
    TimeSpan Latency,
    string StopReason,
    string? ProviderRequestId,
    string RequestHash,
    DateTimeOffset At,
    LlmErrorKind? Error = null);

public interface ILlmUsageRecorder
{
    Task RecordAsync(LlmUsageRecord record, CancellationToken cancellationToken);
}
