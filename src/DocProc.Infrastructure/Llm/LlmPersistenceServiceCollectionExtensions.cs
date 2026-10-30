using DocProc.Llm.Gateway;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocProc.Infrastructure.Llm;

/// <summary>Host wiring for the gateway ports the LLM project leaves to the host (docs/08): policy, credentials, usage.</summary>
public static class LlmPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="EfLlmPolicyStore"/>, <see cref="ConfigurationLlmCredentialStore"/> and
    /// <see cref="EfLlmUsageRecorder"/> as singletons. Needs persistence (<c>ISystemDbContextFactory</c>) and the host's
    /// <c>IConfiguration</c>. A host that already registered a port (the Key Vault credential store, a test fake) keeps it.
    /// </summary>
    public static IServiceCollection AddDocProcLlmPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ILlmPolicyStore, EfLlmPolicyStore>();
        services.TryAddSingleton<ILlmCredentialStore, ConfigurationLlmCredentialStore>();
        services.TryAddSingleton<ILlmUsageRecorder, EfLlmUsageRecorder>();
        return services;
    }
}
