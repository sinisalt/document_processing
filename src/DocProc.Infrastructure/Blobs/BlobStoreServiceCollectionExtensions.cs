using DocProc.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DocProc.Infrastructure.Blobs;

/// <summary>Host wiring for blob storage, shared by the API and the worker (docs/01 "Blob", docs/09).</summary>
public static class BlobStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IBlobStore"/> and <see cref="IContainerBlobStore"/> as one singleton. The provider is
    /// <c>Blobs:Provider</c> when set; otherwise <c>Azure</c> as soon as <c>ConnectionStrings:blobs</c> (injected by
    /// the AppHost) or <c>Blobs:ServiceUri</c> (managed identity) is present, and <c>FileSystem</c> (root
    /// <c>Blobs:RootPath</c>, default <c>./data/blobs</c> under the content root) only when nothing points at Azure.
    /// Options are validated when the host starts, so a missing root path or connection fails the start-up instead
    /// of the first upload. Safe to call more than once.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="environment">When given, a relative <c>Blobs:RootPath</c> resolves against its content root instead of the working directory.</param>
    public static IServiceCollection AddDocProcBlobs(this IServiceCollection services, IConfiguration configuration, IHostEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var contentRoot = environment?.ContentRootPath;

        services.AddOptions<BlobStoreOptions>()
            .Configure(options =>
            {
                configuration.GetSection(BlobStoreOptions.SectionName).Bind(options);
                options.ConnectionString ??= configuration.GetConnectionString(BlobStoreOptions.ConnectionStringName);
                if (contentRoot is not null && Path.IsPathFullyQualified(contentRoot) && !string.IsNullOrWhiteSpace(options.RootPath) && !Path.IsPathRooted(options.RootPath))
                {
                    options.RootPath = Path.GetFullPath(options.RootPath, contentRoot);
                }
            })
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<BlobStoreOptions>, BlobStoreOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IContainerBlobStore>(provider => Create(
            provider.GetRequiredService<IOptions<BlobStoreOptions>>().Value,
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IBlobStore>(provider => provider.GetRequiredService<IContainerBlobStore>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, BlobContainersHostedService>());
        return services;
    }

    /// <summary>The store for a validated set of options; public so tests and tools can build one without a host.</summary>
    public static IContainerBlobStore Create(BlobStoreOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.EffectiveProvider switch
        {
            BlobStoreProvider.FileSystem => new FileSystemBlobStore(options.RootPath, clock),
            BlobStoreProvider.Azure => new AzureBlobStore(AzureBlobClients.Create(options), clock, options.MaxReadUrlLifetime),
            _ => throw new InvalidOperationException($"Unknown blob store provider '{options.Provider}'."),
        };
    }
}
