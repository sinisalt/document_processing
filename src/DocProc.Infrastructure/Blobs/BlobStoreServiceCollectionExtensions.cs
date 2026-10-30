using DocProc.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DocProc.Infrastructure.Blobs;

/// <summary>Host wiring for blob storage, shared by the API and the worker (docs/01 "Blob", docs/09).</summary>
public static class BlobStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IBlobStore"/> and <see cref="IContainerBlobStore"/> as one singleton chosen by
    /// <c>Blobs:Provider</c>: <c>FileSystem</c> (default, root <c>Blobs:RootPath</c>, default <c>./data/blobs</c>) or
    /// <c>Azure</c> (<c>ConnectionStrings:blobs</c> as injected by the AppHost, or <c>Blobs:ServiceUri</c> with
    /// <c>DefaultAzureCredential</c>). Options are validated when the host starts, so a missing root path or
    /// connection fails the start-up instead of the first upload.
    /// </summary>
    public static IServiceCollection AddDocProcBlobs(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<BlobStoreOptions>()
            .Configure(options =>
            {
                configuration.GetSection(BlobStoreOptions.SectionName).Bind(options);
                options.ConnectionString ??= configuration.GetConnectionString(BlobStoreOptions.ConnectionStringName);
            })
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<BlobStoreOptions>, BlobStoreOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IContainerBlobStore>(provider => Create(
            provider.GetRequiredService<IOptions<BlobStoreOptions>>().Value,
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IBlobStore>(provider => provider.GetRequiredService<IContainerBlobStore>());
        services.AddHostedService<BlobContainersHostedService>();
        return services;
    }

    /// <summary>The store for a validated set of options; public so tests and tools can build one without a host.</summary>
    public static IContainerBlobStore Create(BlobStoreOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Provider switch
        {
            BlobStoreProvider.FileSystem => new FileSystemBlobStore(options.RootPath),
            BlobStoreProvider.Azure => new AzureBlobStore(AzureBlobClients.Create(options), clock, options.MaxReadUrlLifetime),
            _ => throw new InvalidOperationException($"Unknown blob store provider '{options.Provider}'."),
        };
    }
}
