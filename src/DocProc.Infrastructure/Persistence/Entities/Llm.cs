using DocProc.Domain.Documents;
using DocProc.Llm;
using DocProc.Llm.Providers;

namespace DocProc.Infrastructure.Persistence.Entities;

/// <summary>Persisted <see cref="LlmProviderConfig"/> (docs/08). The key itself is in Key Vault under <see cref="SecretRef"/>.</summary>
public sealed class LlmProviderConfigRecord
{
    public Guid Id { get; set; }
    public ConfigScope Scope { get; set; }
    public Guid? TenantId { get; set; }
    public LlmProviderKind Kind { get; set; }
    public string? Endpoint { get; set; }
    public string? ApiVersion { get; set; }
    public LlmAuthType AuthType { get; set; }
    public string? SecretRef { get; set; }
    public string? KeyLast4 { get; set; }
    public string? KeyFingerprint { get; set; }

    /// <summary>Stage → model id or deployment name.</summary>
    public Dictionary<string, string> Models { get; set; } = [];

    public decimal? MonthlySpendCapUsd { get; set; }
    public int MaxConcurrency { get; set; } = 4;
    public int MaxRequestsPerMinute { get; set; } = 60;
    public DataResidency DataResidency { get; set; } = DataResidency.Eu;
    public bool AllowBatch { get; set; }
    public ProviderConfigStatus Status { get; set; } = ProviderConfigStatus.Active;
    public DateTimeOffset? ValidatedAt { get; set; }
    public string? LastError { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Tenant? Tenant { get; set; }

    public LlmProviderConfig ToDomain() => new()
    {
        Id = Id,
        Scope = Scope,
        TenantId = TenantId,
        Kind = Kind,
        Endpoint = Endpoint is null ? null : new Uri(Endpoint),
        ApiVersion = ApiVersion,
        Auth = new LlmAuth(AuthType, SecretRef, KeyLast4, KeyFingerprint),
        Models = Models.ToDictionary(kv => Enum.Parse<LlmStage>(kv.Key, ignoreCase: true), kv => kv.Value),
        Limits = new LlmLimits(MonthlySpendCapUsd, MaxConcurrency, MaxRequestsPerMinute),
        DataResidency = DataResidency,
        AllowBatch = AllowBatch,
        Status = Status,
        ValidatedAt = ValidatedAt,
        LastError = LastError,
    };
}

/// <summary>Persisted <see cref="TenantLlmPolicy"/>; one row per tenant.</summary>
public sealed class TenantLlmPolicyRecord : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid? DefaultConfigId { get; set; }
    public Dictionary<string, Guid> StageOverrides { get; set; } = [];
    public bool FallbackToPlatformKey { get; set; }
    public bool AllowPlatformKeyAtAll { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; }

    public Tenant? Tenant { get; set; }
    public LlmProviderConfigRecord? DefaultConfig { get; set; }

    public TenantLlmPolicy ToDomain() => new()
    {
        TenantId = TenantId,
        DefaultConfigId = DefaultConfigId,
        StageOverrides = StageOverrides.ToDictionary(kv => Enum.Parse<LlmStage>(kv.Key, ignoreCase: true), kv => kv.Value),
        FallbackToPlatformKey = FallbackToPlatformKey,
        AllowPlatformKeyAtAll = AllowPlatformKeyAtAll,
    };
}

/// <summary>One row per LLM call (docs/08 "Cost attribution"). Never holds prompt or document content.</summary>
public sealed class LlmUsageEntry : ITenantScoped
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? DocumentId { get; set; }
    public LlmStage Stage { get; set; }
    public Guid ConfigId { get; set; }
    public KeyOwner KeyOwner { get; set; }
    public LlmProviderKind Provider { get; set; }
    public required string Model { get; set; }
    public required string PromptVersion { get; set; }
    public string? SchemaVersion { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheWriteTokens { get; set; }
    public int ReasoningTokens { get; set; }
    public decimal? EstimatedCostUsd { get; set; }
    public int LatencyMs { get; set; }
    public required string StopReason { get; set; }
    public string? ProviderRequestId { get; set; }
    public required string RequestHash { get; set; }
    public LlmErrorKind? Error { get; set; }
    public DateTimeOffset At { get; set; }
}
