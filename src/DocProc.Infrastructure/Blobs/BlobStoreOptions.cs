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

    /// <summary>Default <see cref="RootPath"/>, relative to the host's content root.</summary>
    public const string DefaultRootPath = "./data/blobs";

    /// <summary>Azure caps a user-delegation key, and so a read URL signed with it, at seven days.</summary>
    public static readonly TimeSpan MaxSupportedReadUrlLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// Explicit provider choice. Unset, the provider is inferred by <see cref="EffectiveProvider"/>: Azure when the
    /// AppHost injected <c>ConnectionStrings:blobs</c> or <see cref="ServiceUri"/> is configured, FileSystem otherwise.
    /// </summary>
    public BlobStoreProvider? Provider { get; set; }

    /// <summary>
    /// Root directory of the <see cref="BlobStoreProvider.FileSystem"/> store. <c>AddDocProcBlobs</c> resolves a
    /// relative path against the host's content root; without a host, against the working directory.
    /// </summary>
    public string RootPath { get; set; } = DefaultRootPath;

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

    /// <summary>
    /// Upper bound for <see cref="DocProc.Application.Abstractions.IBlobStore.GetReadUrlAsync"/>; longer requests are
    /// clamped. Cannot exceed <see cref="MaxSupportedReadUrlLifetime"/>.
    /// </summary>
    public TimeSpan MaxReadUrlLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Create the containers of <see cref="DocProc.Application.Abstractions.BlobContainers"/> when the host starts.
    /// Off by default: the AppHost and Bicep own the containers (and the WORM policy on the raw container).
    /// </summary>
    public bool CreateContainersOnStartup { get; set; }

    /// <summary>
    /// The provider the host runs with: <see cref="Provider"/> when set; otherwise Azure as soon as the AppHost
    /// injected <c>ConnectionStrings:blobs</c> or a <see cref="ServiceUri"/> is configured, so both hosts share the
    /// same Azurite under the AppHost without extra settings; FileSystem only as the last fallback.
    /// </summary>
    public BlobStoreProvider EffectiveProvider =>
        Provider ?? (HasAzureEndpoint ? BlobStoreProvider.Azure : BlobStoreProvider.FileSystem);

    /// <summary>True when something that could reach an Azure (or Azurite) blob service is configured.</summary>
    public bool HasAzureEndpoint => !string.IsNullOrWhiteSpace(ConnectionString) || ServiceUri is not null;
}
