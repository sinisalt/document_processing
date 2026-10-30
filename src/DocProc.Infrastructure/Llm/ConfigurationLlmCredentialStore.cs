using System.Text;
using DocProc.Llm;
using DocProc.Llm.Gateway;
using DocProc.Llm.Providers;
using Microsoft.Extensions.Configuration;

namespace DocProc.Infrastructure.Llm;

/// <summary>
/// <see cref="ILlmCredentialStore"/> that resolves a configuration's secret reference against the host configuration
/// (<c>Llm:Secrets:&lt;secretRef&gt;</c>, i.e. user secrets or <c>Llm__Secrets__&lt;secretRef&gt;</c>) or the environment
/// variable <c>LLM_SECRET_&lt;SECRET_REF&gt;</c> (upper snake case). This is the local and CI path of docs/08 "Key
/// handling"; the Key Vault store that replaces it in Azure is a later block. A managed-identity configuration needs no
/// secret, and neither does the replay provider.
/// </summary>
public sealed class ConfigurationLlmCredentialStore(IConfiguration configuration) : ILlmCredentialStore
{
    public const string SectionName = "Llm:Secrets";
    public const string EnvironmentVariablePrefix = "LLM_SECRET_";

    public Task<LlmCredential> GetAsync(LlmProviderConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();

        if (config.Auth.Type == LlmAuthType.ManagedIdentity)
        {
            return Task.FromResult(new LlmCredential(UseManagedIdentity: true));
        }

        if (config.Kind == LlmProviderKind.Replay)
        {
            return Task.FromResult(LlmCredential.None);
        }

        var secretRef = config.Auth.SecretRef;
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            throw new LlmException(LlmErrorKind.Auth, $"Provider configuration {config.Id} ({config.Kind}) has no secret reference, so no key can be resolved.");
        }

        var key = configuration[$"{SectionName}:{secretRef}"];
        var variable = EnvironmentVariableFor(secretRef);
        if (string.IsNullOrWhiteSpace(key))
        {
            key = Environment.GetEnvironmentVariable(variable);
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new LlmException(
                LlmErrorKind.Auth,
                $"No key is stored for secret reference '{secretRef}' of provider configuration {config.Id} ({config.Kind}). " +
                $"Set configuration '{SectionName}:{secretRef}' (user secrets) or the environment variable {variable}.");
        }

        return Task.FromResult(new LlmCredential(key.Trim()));
    }

    /// <summary>The environment variable for a secret reference: <c>llm-platform-eu</c> becomes <c>LLM_SECRET_LLM_PLATFORM_EU</c>.</summary>
    public static string EnvironmentVariableFor(string secretRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);
        var sb = new StringBuilder(EnvironmentVariablePrefix, EnvironmentVariablePrefix.Length + secretRef.Length);
        foreach (var c in secretRef)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        }

        return sb.ToString();
    }
}
