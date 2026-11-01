namespace DocProc.Application.Abstractions;

/// <summary>Hashing helper shared by ingestion and blob storage so duplicate detection and blob keys agree.</summary>
public static class ContentHash
{
    public static string Sha256Hex(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));

    public static async Task<string> Sha256HexAsync(Stream content, CancellationToken cancellationToken)
    {
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
