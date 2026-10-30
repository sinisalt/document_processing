namespace DocProc.Application.Abstractions;

/// <summary>
/// Where credentials live (docs/08 "Key handling"): Azure Key Vault in production, a file store or memory locally.
/// The database only ever holds the reference; callers that write a secret (the BYOK endpoints)
/// and callers that read one (the gateway's credential store) share this one interface so there is a single code
/// path for platform and tenant keys. Implementations never log the secret value.
/// </summary>
public interface ISecretStore
{
    /// <summary>Creates or overwrites the secret stored under <paramref name="reference"/>.</summary>
    Task SetAsync(string reference, string secret, CancellationToken cancellationToken);

    /// <summary>The secret stored under <paramref name="reference"/>, or null when nothing is stored there.</summary>
    Task<string?> GetAsync(string reference, CancellationToken cancellationToken);

    /// <summary>Removes the secret stored under <paramref name="reference"/>; a reference nothing is stored under is not an error.</summary>
    Task DeleteAsync(string reference, CancellationToken cancellationToken);
}
