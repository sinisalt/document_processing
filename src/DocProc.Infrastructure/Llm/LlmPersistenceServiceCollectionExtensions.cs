using DocProc.Application.Abstractions;
using DocProc.Llm.Gateway;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace DocProc.Infrastructure.Llm;

/// <summary>Host wiring for the gateway ports the LLM project leaves to the host (docs/08): policy, credentials, usage.</summary>
public static class LlmPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="EfLlmPolicyStore"/>, <see cref="SecretStoreLlmCredentialStore"/> and
    /// <see cref="EfLlmUsageRecorder"/> as singletons. Needs persistence (<c>ISystemDbContextFactory</c>) and the host's
    /// <c>IConfiguration</c>; the credential store reads keys from the host's <see cref="ISecretStore"/> (where the API
    /// writes BYOK and platform keys) when one is registered, then from configuration. A host that already registered a
    /// port (the Key Vault credential store, a test fake) keeps it.
    /// </summary>
    public static IServiceCollection AddDocProcLlmPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ILlmPolicyStore, EfLlmPolicyStore>();
        services.TryAddSingleton<ILlmCredentialStore>(sp => new SecretStoreLlmCredentialStore(
            sp.GetService<ISecretStore>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetService<ILogger<SecretStoreLlmCredentialStore>>()));
        services.TryAddSingleton<ILlmUsageRecorder, EfLlmUsageRecorder>();
        return services;
    }
}
