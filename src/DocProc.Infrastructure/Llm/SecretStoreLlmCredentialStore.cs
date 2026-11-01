using System.Text;
using DocProc.Application.Abstractions;
using DocProc.Llm;
using DocProc.Llm.Gateway;
using DocProc.Llm.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocProc.Infrastructure.Llm;

/// <summary>
/// <see cref="ILlmCredentialStore"/> of docs/08 "Key handling": one code path for platform and tenant keys. A
/// configuration's secret reference is looked up first in <see cref="ISecretStore"/>, where <c>POST /llm-providers</c>
/// and <c>POST /llm/providers</c> write the key (the file store locally, Key Vault later), then in the host
/// configuration (<c>Llm:Secrets:&lt;secretRef&gt;</c>, i.e. user secrets or <c>Llm__Secrets__&lt;secretRef&gt;</c>) and
/// the environment variable <c>LLM_SECRET_&lt;SECRET_REF&gt;</c> (upper snake case), which is how a developer supplies
/// the seeded platform reference (<c>llm-platform-eu</c>). A managed-identity configuration needs no secret, and
/// neither does the replay provider.
/// <para>
/// A missing key fails with an <see cref="LlmErrorKind.Auth"/> error whose message reaches the document's
/// <c>failureReason</c>, which the tenant reads, so it names only the configuration; the internal reference and where
/// an operator can put the key go to the log.
/// </para>
/// </summary>
public sealed class SecretStoreLlmCredentialStore(
    ISecretStore? secrets,
    IConfiguration configuration,
    ILogger<SecretStoreLlmCredentialStore>? logger = null) : ILlmCredentialStore
{
    public const string SectionName = "Llm:Secrets";
    public const string EnvironmentVariablePrefix = "LLM_SECRET_";

    private readonly ILogger _logger = logger ?? NullLogger<SecretStoreLlmCredentialStore>.Instance;

    public async Task<LlmCredential> GetAsync(LlmProviderConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();

        if (config.Auth.Type == LlmAuthType.ManagedIdentity)
        {
            return new LlmCredential(UseManagedIdentity: true);
        }

        if (config.Kind == LlmProviderKind.Replay)
        {
            return LlmCredential.None;
        }

        var secretRef = config.Auth.SecretRef;
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            throw new LlmException(LlmErrorKind.Auth, $"Provider configuration {config.Id} ({config.Kind}) has no stored key.");
        }

        var key = secrets is null ? null : await secrets.GetAsync(secretRef, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            key = configuration[$"{SectionName}:{secretRef}"];
        }

        var variable = EnvironmentVariableFor(secretRef);
        if (string.IsNullOrWhiteSpace(key))
        {
            key = Environment.GetEnvironmentVariable(variable);
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            _logger.LogWarning(
                "No key is stored for provider configuration {ConfigId} ({Kind}) under secret reference {SecretRef}: not in the secret store, not in configuration '{ConfigurationKey}' (user secrets) and not in the environment variable {EnvironmentVariable}.",
                config.Id,
                config.Kind,
                secretRef,
                $"{SectionName}:{secretRef}",
                variable);
            throw new LlmException(
                LlmErrorKind.Auth,
                $"No key is stored for provider configuration {config.Id} ({config.Kind}).");
        }

        return new LlmCredential(key.Trim());
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
