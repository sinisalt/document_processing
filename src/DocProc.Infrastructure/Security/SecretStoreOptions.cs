using DocProc.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DocProc.Infrastructure.Security;

/// <summary>Settings of the local <see cref="FileSecretStore"/>, bound from the <c>Secrets</c> configuration section.</summary>
public sealed class SecretStoreOptions
{
    public const string SectionName = "Secrets";

    /// <summary>Default <see cref="Path"/>, relative to the host's content root; next to the blob and SQLite data.</summary>
    public const string DefaultPath = "./data/secrets";

    /// <summary>Directory of the file-backed store. A relative path resolves against the host's content root.</summary>
    public string Path { get; set; } = DefaultPath;
}

/// <summary>Host wiring for <see cref="ISecretStore"/>, shared by the API and the worker.</summary>
public static class SecretStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the file-backed <see cref="ISecretStore"/> under <c>Secrets:Path</c> (default <c>./data/secrets</c>
    /// under the content root). Azure Key Vault will be selected here once the Azure environment is wired; the
    /// endpoints and the gateway only ever see the interface. Safe to call more than once.
    /// </summary>
    public static IServiceCollection AddDocProcSecrets(this IServiceCollection services, IConfiguration configuration, IHostEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var contentRoot = environment?.ContentRootPath;

        services.AddOptions<SecretStoreOptions>()
            .Configure(options =>
            {
                configuration.GetSection(SecretStoreOptions.SectionName).Bind(options);
                if (string.IsNullOrWhiteSpace(options.Path))
                {
                    options.Path = SecretStoreOptions.DefaultPath;
                }

                if (contentRoot is not null && System.IO.Path.IsPathFullyQualified(contentRoot) && !System.IO.Path.IsPathRooted(options.Path))
                {
                    options.Path = System.IO.Path.GetFullPath(options.Path, contentRoot);
                }
            });

        services.TryAddSingleton<ISecretStore>(provider => new FileSecretStore(provider.GetRequiredService<IOptions<SecretStoreOptions>>().Value.Path));
        return services;
    }
}
