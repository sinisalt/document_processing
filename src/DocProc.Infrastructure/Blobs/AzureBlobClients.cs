using Azure.Identity;
using Azure.Storage.Blobs;

namespace DocProc.Infrastructure.Blobs;

/// <summary>How an <see cref="AzureBlobStore"/> reaches its account: an SDK connection string, or a service URI with a token credential.</summary>
public sealed record AzureBlobEndpoint(string? ConnectionString, Uri? ServiceUri)
{
    /// <summary>True when the account is reached with <c>DefaultAzureCredential</c> (managed identity, developer sign-in).</summary>
    public bool UsesTokenCredential => ServiceUri is not null;
}

/// <summary>
/// Builds the <see cref="BlobServiceClient"/> from <see cref="BlobStoreOptions"/>. The Aspire AppHost injects
/// <c>ConnectionStrings:blobs</c> as a full connection string for Azurite but as the account's blob endpoint for a
/// real storage account, so both shapes are recognised; an explicit <c>Blobs:ServiceUri</c> works the same way.
/// </summary>
public static class AzureBlobClients
{
    private const string SharedAccessSignatureSetting = "SharedAccessSignature";
    private static readonly string[] SigningKeySettings = ["AccountKey", "UseDevelopmentStorage"];

    public static BlobServiceClient Create(BlobStoreOptions options)
    {
        var endpoint = Resolve(options);
        return endpoint.ServiceUri is { } serviceUri
            ? new BlobServiceClient(serviceUri, new DefaultAzureCredential())
            : new BlobServiceClient(endpoint.ConnectionString);
    }

    /// <summary>Decides, without touching the network, how the configured values will be used.</summary>
    /// <exception cref="InvalidOperationException">
    /// The connection string is neither an SDK connection string with an account key nor a service URI, or it
    /// carries only a shared access signature: such a client can neither sign account-key SAS URLs nor fetch a
    /// user-delegation key, so <see cref="AzureBlobStore.GetReadUrlAsync"/> could never work.
    /// </exception>
    public static AzureBlobEndpoint Resolve(BlobStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var connectionString = options.ConnectionString?.Trim();

        if (!string.IsNullOrEmpty(connectionString))
        {
            if (TryParseHttpUri(connectionString, out var bareUri))
            {
                return new AzureBlobEndpoint(null, bareUri);
            }

            var settings = ParseSettings(connectionString);
            if (SigningKeySettings.Any(settings.ContainsKey))
            {
                return new AzureBlobEndpoint(connectionString, null);
            }

            if (settings.ContainsKey(SharedAccessSignatureSetting))
            {
                throw new InvalidOperationException(
                    $"ConnectionStrings:{BlobStoreOptions.ConnectionStringName} carries only a {SharedAccessSignatureSetting}. A SAS credential cannot sign read URLs (no account key, no user-delegation key), so it is not supported: use an account key, or the service URI with a managed identity.");
            }

            if (settings.TryGetValue("Endpoint", out var endpoint) && TryParseHttpUri(endpoint, out var endpointUri))
            {
                return new AzureBlobEndpoint(null, endpointUri);
            }

            throw new InvalidOperationException($"ConnectionStrings:{BlobStoreOptions.ConnectionStringName} is neither an SDK connection string nor a blob service URI.");
        }

        if (options.ServiceUri is { IsAbsoluteUri: true } configured)
        {
            return new AzureBlobEndpoint(null, configured);
        }

        throw new InvalidOperationException($"Provider Azure needs ConnectionStrings:{BlobStoreOptions.ConnectionStringName} or {BlobStoreOptions.SectionName}:{nameof(BlobStoreOptions.ServiceUri)}.");
    }

    private static bool TryParseHttpUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    private static Dictionary<string, string> ParseSettings(string connectionString)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                settings[pair[..separator].Trim()] = pair[(separator + 1)..].Trim();
            }
        }

        return settings;
    }
}
