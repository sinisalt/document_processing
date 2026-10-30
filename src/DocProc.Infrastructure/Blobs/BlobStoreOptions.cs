namespace DocProc.Infrastructure.Blobs;

/// <summary>Which <see cref="DocProc.Application.Abstractions.IBlobStore"/> implementation a host runs with.</summary>
public enum BlobStoreProvider
{
    /// <summary>Content-addressed files under <see cref="BlobStoreOptions.RootPath"/>; local development and tests.</summary>
    FileSystem,

    /// <summary>Azure Blob Storage, or Azurite through the same SDK (docs/09 "Blob storage").</summary>
    Azure,
}

/// <summary>
/// Blob storage settings, bound from the <c>Blobs</c> configuration section plus the <c>ConnectionStrings:blobs</c>
/// entry the Aspire AppHost injects for Azurite or Azure Blob Storage. Validated at host start-up by
/// <see cref="BlobStoreOptionsValidator"/> so a misconfigured host fails before it accepts work.
/// </summary>
public sealed record BlobStoreOptions
{
    public const string SectionName = "Blobs";

    /// <summary>The connection-string name the AppHost uses for the blob service (<c>WithReference(blobs)</c>).</summary>
    public const string ConnectionStringName = "blobs";

    public BlobStoreProvider Provider { get; set; } = BlobStoreProvider.FileSystem;

    /// <summary>Root directory of the <see cref="BlobStoreProvider.FileSystem"/> store; relative paths resolve against the working directory.</summary>
    public string RootPath { get; set; } = "./data/blobs";

    /// <summary>
    /// Blob service endpoint (<c>https://{account}.blob.core.windows.net</c>) used with <c>DefaultAzureCredential</c>
    /// when no connection string carries a key; this is the managed-identity path of docs/01 "Identity".
    /// </summary>
    public Uri? ServiceUri { get; set; }

    /// <summary>
    /// Taken from <c>ConnectionStrings:blobs</c>. Either an SDK connection string (Azurite, or an account key) or, as
    /// Aspire emits for a real account, a bare service URI, in which case <c>DefaultAzureCredential</c> is used.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>Upper bound for <see cref="DocProc.Application.Abstractions.IBlobStore.GetReadUrlAsync"/>; longer requests are clamped.</summary>
    public TimeSpan MaxReadUrlLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Create the containers of <see cref="BlobContainers"/> when the host starts. Off by default: the AppHost and
    /// Bicep own the containers (and the WORM policy on <see cref="BlobContainers.Raw"/>).
    /// </summary>
    public bool CreateContainersOnStartup { get; set; }
}
