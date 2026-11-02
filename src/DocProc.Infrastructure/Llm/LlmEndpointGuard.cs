using DocProc.Infrastructure.Http;
using DocProc.Llm;
using DocProc.Llm.Providers;

namespace DocProc.Infrastructure.Llm;

/// <summary>
/// The LLM path's use of <see cref="TenantEndpointGuard"/>: how the provider adapters check a configuration before
/// they call it and how they report a refused connection (docs/08 "Endpoint restrictions").
/// </summary>
public static class LlmEndpointGuard
{
    /// <summary>The refusal as the error every adapter reports: the configuration is unusable until the tenant fixes it.</summary>
    public static LlmException ToLlmException(BlockedEndpointException blocked) =>
        new(LlmErrorKind.Unavailable, TenantEndpointGuard.BlockedMessage, inner: blocked);

    /// <summary>
    /// Throws when <paramref name="config"/> is a tenant configuration whose endpoint
    /// <see cref="TenantEndpointGuard.Validate(Uri)"/> refuses (a row created before the check existed); the connect
    /// step would refuse it anyway.
    /// </summary>
    public static void EnsureAllowed(LlmProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Scope == ConfigScope.Tenant && config.Endpoint is { } endpoint && TenantEndpointGuard.Validate(endpoint) is not null)
        {
            throw ToLlmException(new BlockedEndpointException());
        }
    }
}
