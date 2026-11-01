namespace DocProc.Llm.Providers;

/// <summary>
/// The only thing the pipeline knows about a vendor. An instance is bound to one <see cref="LlmProviderConfig"/>
/// and its credential; the gateway owns the mapping from tenant and stage to provider.
/// </summary>
public interface ILlmProvider
{
    LlmProviderKind Kind { get; }

    LlmCapabilities Capabilities { get; }

    Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);

    /// <summary>Optional; throws <see cref="LlmException"/> with <see cref="LlmErrorKind.InvalidRequest"/> when <see cref="LlmCapabilities.Batch"/> is false.</summary>
    Task<BatchHandle> SubmitBatchAsync(IReadOnlyList<LlmRequest> requests, CancellationToken cancellationToken);

    Task<BatchResult> GetBatchAsync(BatchHandle handle, CancellationToken cancellationToken);

    /// <summary>Minimal call used to validate a credential when a tenant adds it (docs/08 "Key handling" step 3).</summary>
    Task ValidateCredentialAsync(CancellationToken cancellationToken);
}

/// <summary>The resolved secret for one provider configuration. Never logged, never persisted outside the vault.</summary>
public sealed record LlmCredential(string? ApiKey = null, bool UseManagedIdentity = false)
{
    public static readonly LlmCredential None = new(ApiKey: null);

    public override string ToString() => UseManagedIdentity ? "LlmCredential(managed identity)" : "LlmCredential(api key ****)";
}

/// <summary>Creates a provider for a configuration and its credential. Implemented per adapter; the gateway caches instances per config id.</summary>
public interface ILlmProviderFactory
{
    bool Supports(LlmProviderKind kind);

    ILlmProvider Create(LlmProviderConfig config, LlmCredential credential);
}
