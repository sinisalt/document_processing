using Microsoft.Extensions.Options;

namespace DocProc.Infrastructure.Blobs;

/// <summary>Fails host start-up on a store that could not work: no root path, no way to reach Azure, or a read-URL lifetime Azure refuses.</summary>
public sealed class BlobStoreOptionsValidator : IValidateOptions<BlobStoreOptions>
{
    public ValidateOptionsResult Validate(string? name, BlobStoreOptions options)
    {
        var failures = new List<string>();

        if (options.MaxReadUrlLifetime <= TimeSpan.Zero)
        {
            failures.Add($"{BlobStoreOptions.SectionName}:{nameof(BlobStoreOptions.MaxReadUrlLifetime)} must be positive.");
        }
        else if (options.MaxReadUrlLifetime > BlobStoreOptions.MaxSupportedReadUrlLifetime)
        {
            failures.Add($"{BlobStoreOptions.SectionName}:{nameof(BlobStoreOptions.MaxReadUrlLifetime)} cannot exceed {BlobStoreOptions.MaxSupportedReadUrlLifetime.TotalDays:0} days: Azure caps user-delegation keys at seven days.");
        }

        switch (options.EffectiveProvider)
        {
            case BlobStoreProvider.FileSystem:
                ValidateFileSystem(options, failures);
                break;
            case BlobStoreProvider.Azure:
                ValidateAzure(options, failures);
                break;
            default:
                failures.Add($"Unknown {BlobStoreOptions.SectionName}:{nameof(BlobStoreOptions.Provider)} '{options.Provider}'.");
                break;
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateAzure(BlobStoreOptions options, List<string> failures)
    {
        if (!options.HasAzureEndpoint)
        {
            failures.Add($"Provider Azure needs ConnectionStrings:{BlobStoreOptions.ConnectionStringName} or {BlobStoreOptions.SectionName}:{nameof(BlobStoreOptions.ServiceUri)}.");
            return;
        }

        if (options.ServiceUri is { IsAbsoluteUri: false })
        {
            failures.Add($"{BlobStoreOptions.SectionName}:{nameof(BlobStoreOptions.ServiceUri)} must be an absolute URI.");
            return;
        }

        // Resolve applies the connection-string rules (SAS-only strings are refused) without touching the network.
        try
        {
            AzureBlobClients.Resolve(options);
        }
        catch (InvalidOperationException ex)
        {
            failures.Add(ex.Message);
        }
    }

    private static void ValidateFileSystem(BlobStoreOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            failures.Add($"Provider FileSystem needs {BlobStoreOptions.SectionName}:{nameof(BlobStoreOptions.RootPath)}.");
            return;
        }

        // Creating the directory here is what makes a read-only or missing volume a start-up failure instead of a
        // failure on the first upload.
        try
        {
            Directory.CreateDirectory(Path.GetFullPath(options.RootPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            failures.Add($"{BlobStoreOptions.SectionName}:{nameof(BlobStoreOptions.RootPath)} '{options.RootPath}' is not usable: {ex.Message}");
        }
    }
}
