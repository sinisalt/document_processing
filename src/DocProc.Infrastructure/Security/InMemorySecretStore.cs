using System.Collections.Concurrent;
using DocProc.Application.Abstractions;

namespace DocProc.Infrastructure.Security;

/// <summary>Secrets held in process memory only; for tests and throw-away hosts.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public int Count => _secrets.Count;

    public Task SetAsync(string reference, string secret, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(secret);
        _secrets[reference] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return Task.FromResult(_secrets.TryGetValue(reference, out var secret) ? secret : null);
    }
}
