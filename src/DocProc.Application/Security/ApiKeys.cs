using System.Security.Cryptography;
using System.Text;

namespace DocProc.Application.Security;

/// <summary>What a machine client may do with a key (docs/05 "Roles": machine clients are scoped to ingest and/or read).</summary>
public static class ApiKeyScopes
{
    public const string Ingest = "ingest";
    public const string Read = "read";

    public static readonly IReadOnlyList<string> All = [Ingest, Read];

    public static bool IsKnown(string scope) => All.Contains(scope, StringComparer.Ordinal);
}

/// <summary>A freshly generated key: the full secret is shown to the caller once; only prefix and hash are stored.</summary>
public sealed record GeneratedApiKey(string Key, string Prefix, string Hash);

/// <summary>
/// Key format: <c>dpk_&lt;prefix&gt;_&lt;secret&gt;</c>. The prefix identifies the key in the UI and logs; the secret is 32 random
/// bytes. Keys are never stored: the database holds SHA-256(key) and the lookup is by hash.
/// </summary>
public static class ApiKeys
{
    public const string KeyPrefix = "dpk_";
    public const int PrefixLength = 8;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";

    public static GeneratedApiKey Generate()
    {
        var prefix = RandomString(PrefixLength);
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var key = $"{KeyPrefix}{prefix}_{secret}";
        return new GeneratedApiKey(key, prefix, Hash(key));
    }

    /// <summary>Lower-case hex SHA-256 of the full key string; stored in a fixed 64-character column.</summary>
    public static string Hash(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>Cheap rejection before touching the database: right prefix, plausible length, no whitespace.</summary>
    public static bool IsWellFormed(string? key) =>
        key is { Length: >= 40 and <= 120 }
        && key.StartsWith(KeyPrefix, StringComparison.Ordinal)
        && key.IndexOf('_', KeyPrefix.Length) == KeyPrefix.Length + PrefixLength
        && !key.Any(char.IsWhiteSpace);

    /// <summary>The identifying prefix of a well-formed key, for logs and error messages.</summary>
    public static string PrefixOf(string key) => key.Substring(KeyPrefix.Length, PrefixLength);

    private static string RandomString(int length) =>
        string.Create(length, Alphabet, (span, alphabet) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            }
        });

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The caller behind a valid key.</summary>
public sealed record ApiKeyIdentity(Guid KeyId, Guid TenantId, string Name, string Prefix, IReadOnlyList<string> Scopes);

/// <summary>Looks a presented key up; null for unknown, revoked, or belonging to a tenant that is not active.</summary>
public interface IApiKeyValidator
{
    Task<ApiKeyIdentity?> ValidateAsync(string presentedKey, CancellationToken cancellationToken);
}
