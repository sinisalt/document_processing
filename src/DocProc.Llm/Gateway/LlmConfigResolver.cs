using DocProc.Domain.Documents;
using DocProc.Llm.Providers;

namespace DocProc.Llm.Gateway;

/// <summary>The configuration chosen for a call and who pays for it.</summary>
public sealed record ResolvedProvider(LlmProviderConfig Config, KeyOwner KeyOwner, string Model, bool UsedFallback);

/// <summary>
/// Pure resolution of "which config serves this tenant's stage": tenant stage override → tenant default →
/// platform default, with the tenant policy deciding whether the platform key may stand in (docs/08).
/// </summary>
public static class LlmConfigResolver
{
    public static ResolvedProvider Resolve(
        LlmStage stage,
        TenantLlmPolicy? policy,
        IReadOnlyDictionary<Guid, LlmProviderConfig> tenantConfigs,
        LlmProviderConfig? platformDefault)
    {
        ArgumentNullException.ThrowIfNull(tenantConfigs);

        Guid? tenantConfigId = policy is not null && policy.StageOverrides.TryGetValue(stage, out var overrideId)
            ? overrideId
            : policy?.DefaultConfigId;
        string? tenantProblem = null;

        if (tenantConfigId is { } id)
        {
            if (!tenantConfigs.TryGetValue(id, out var tenantConfig))
            {
                tenantProblem = $"Tenant provider configuration {id} was not found.";
            }
            else if (!tenantConfig.IsUsable)
            {
                tenantProblem = $"Tenant provider configuration {id} is {tenantConfig.Status}" +
                                (tenantConfig.LastError is null ? "." : $": {tenantConfig.LastError}");
            }
            else if (tenantConfig.ModelFor(stage) is not { } model)
            {
                tenantProblem = $"Tenant provider configuration {id} has no model for stage {stage}.";
            }
            else
            {
                return new ResolvedProvider(tenantConfig, KeyOwner.Tenant, model, UsedFallback: false);
            }

            if (!policy!.FallbackToPlatformKey || !policy.AllowPlatformKeyAtAll)
            {
                throw new LlmException(LlmErrorKind.Unavailable, tenantProblem + " The tenant policy does not allow the platform key as fallback.");
            }
        }
        else if (policy is { AllowPlatformKeyAtAll: false })
        {
            throw new LlmException(LlmErrorKind.Unavailable, "The tenant requires its own provider key but none is configured.");
        }

        if (platformDefault is null || !platformDefault.IsUsable)
        {
            throw new LlmException(LlmErrorKind.Unavailable, tenantProblem is null
                ? "No platform provider configuration is available."
                : tenantProblem + " No platform provider configuration is available as fallback.");
        }

        if (platformDefault.ModelFor(stage) is not { } platformModel)
        {
            throw new LlmException(LlmErrorKind.Unavailable, $"The platform provider configuration has no model for stage {stage}.");
        }

        return new ResolvedProvider(platformDefault, KeyOwner.Platform, platformModel, UsedFallback: tenantProblem is not null);
    }
}
