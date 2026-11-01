using System.Collections.Frozen;

namespace DocProc.Llm.Providers;

public enum ConfigScope
{
    Platform,
    Tenant,
}

public enum LlmAuthType
{
    ApiKey,
    ManagedIdentity,
    EntraClientCredentials,
}

public enum ProviderConfigStatus
{
    Active,
    Disabled,
    Invalid,
}

public enum DataResidency
{
    Eu,
    Us,
    TenantOwned,
}

/// <summary>Only a reference to the secret is stored; the key itself lives in Key Vault (docs/08 "Key handling").</summary>
public sealed record LlmAuth(LlmAuthType Type, string? SecretRef, string? KeyLast4 = null, string? KeyFingerprint = null);

public sealed record LlmLimits(decimal? MonthlySpendCapUsd = null, int MaxConcurrency = 4, int MaxRequestsPerMinute = 60);

/// <summary>A provider endpoint plus credentials and the model to use per stage (docs/08 "Provider configuration model").</summary>
public sealed record LlmProviderConfig
{
    public required Guid Id { get; init; }

    public required ConfigScope Scope { get; init; }

    public Guid? TenantId { get; init; }

    public required LlmProviderKind Kind { get; init; }

    public Uri? Endpoint { get; init; }

    public string? ApiVersion { get; init; }

    public required LlmAuth Auth { get; init; }

    /// <summary>Model id or Azure deployment name per stage. A missing stage means this config cannot serve it.</summary>
    public IReadOnlyDictionary<LlmStage, string> Models { get; init; } = FrozenDictionary<LlmStage, string>.Empty;

    public LlmLimits Limits { get; init; } = new();

    public DataResidency DataResidency { get; init; } = DataResidency.Eu;

    public bool AllowBatch { get; init; }

    public ProviderConfigStatus Status { get; init; } = ProviderConfigStatus.Active;

    public DateTimeOffset? ValidatedAt { get; init; }

    public string? LastError { get; init; }

    public bool IsUsable => Status == ProviderConfigStatus.Active;

    public bool Serves(LlmStage stage) => Models.ContainsKey(stage);

    public string? ModelFor(LlmStage stage) => Models.GetValueOrDefault(stage);
}

/// <summary>Which configuration a tenant's stages use and whether the platform key may stand in (docs/08).</summary>
public sealed record TenantLlmPolicy
{
    public required Guid TenantId { get; init; }

    public Guid? DefaultConfigId { get; init; }

    public IReadOnlyDictionary<LlmStage, Guid> StageOverrides { get; init; } = FrozenDictionary<LlmStage, Guid>.Empty;

    /// <summary>When the tenant's own config is unusable, may the platform key be used? Default false for BYOK tenants.</summary>
    public bool FallbackToPlatformKey { get; init; }

    /// <summary>Some customers require "our key or nothing".</summary>
    public bool AllowPlatformKeyAtAll { get; init; } = true;

    public bool IsByok => DefaultConfigId is not null || StageOverrides.Count > 0;
}
