using System.Text.RegularExpressions;
using DocProc.Application.Abstractions;

namespace DocProc.Infrastructure.Blobs;

/// <summary>
/// The content-addressed key scheme of docs/03 "Idempotency": <c>{tenant}/{sha256}</c>, with the tenant as a
/// lower-case GUID and the hash as 64 lower-case hex characters. Everything that reaches a store is checked against
/// this shape, which is also what keeps user-supplied references from escaping a tenant prefix or the store root.
/// </summary>
public static partial class BlobKeys
{
    public const int Sha256HexLength = 64;

    /// <summary>Builds the key for content with the given digest.</summary>
    public static string ForContent(Guid tenantId, string sha256)
    {
        if (sha256 is null || !Sha256Pattern().IsMatch(sha256))
        {
            throw new ArgumentException("Expected 64 lower-case hex characters.", nameof(sha256));
        }

        return $"{tenantId:D}/{sha256}";
    }

    /// <summary>The digest part of a key that passed <see cref="EnsureValidKey"/>.</summary>
    public static string Sha256Of(string key) => key[(key.Length - Sha256HexLength)..];

    /// <summary>Azure container naming rules, which the file-system store applies too so both layouts agree.</summary>
    public static void EnsureValidContainer(string container)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);
        if (!ContainerPattern().IsMatch(container))
        {
            throw new ArgumentException($"'{container}' is not a valid container name.", nameof(container));
        }
    }

    public static void EnsureValidKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!KeyPattern().IsMatch(key))
        {
            throw new ArgumentException($"'{key}' is not a content-addressed key of the form {{tenant}}/{{sha256}}.", nameof(key));
        }
    }

    public static void EnsureValid(BlobRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        EnsureValidContainer(reference.Container);
        EnsureValidKey(reference.Key);
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){2,62}$")]
    private static partial Regex ContainerPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{64}$")]
    private static partial Regex KeyPattern();
}
