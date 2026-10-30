using DocProc.Application.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DocProc.Infrastructure.Blobs;

/// <summary>
/// Creates the Azure containers at start-up when <c>Blobs:CreateContainersOnStartup</c> is on, for environments
/// where nothing else owns them (a bare Azurite without the AppHost). The file-system store creates its directories
/// on demand and needs nothing here.
/// </summary>
public sealed class BlobContainersHostedService(IBlobStore store, IOptions<BlobStoreOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        options.Value.CreateContainersOnStartup && store is AzureBlobStore azure
            ? azure.EnsureContainersAsync(cancellationToken)
            : Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
