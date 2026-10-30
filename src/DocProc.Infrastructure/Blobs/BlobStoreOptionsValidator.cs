using Microsoft.Extensions.Options;

namespace DocProc.Infrastructure.Blobs;

/// <summary>Fails host start-up on a store that could not work: no root path, or no way to reach Azure.</summary>
public sealed class BlobStoreOptionsValidator : IValidateOptions<BlobStoreOptions>
{
    public ValidateOptionsResult Validate(string? name, BlobStoreOptions options)
    {
        var failures = new List<string>();

        if (options.MaxReadUrlLifetime <= TimeSpan.Zero)
        {
            failures.Add($"{BlobStoreOptions.SectionName}:{nameof(BlobStoreOptions.MaxReadUrlLifetime)} must be positive.");
        }

        switch (options.Provider)
        {
            case BlobStoreProvider.FileSystem:
                ValidateFileSystem(options, failures);
                break;
            case BlobStoreProvider.Azure:
                if (string.IsNullOrWhiteSpace(options.ConnectionString) && options.ServiceUri is null)
                {
                    failures.Add($"Provider Azure needs ConnectionStrings:{BlobStoreOptions.ConnectionStringName} or {BlobStoreOptions.SectionName}:{nameof(BlobStoreOptions.ServiceUri)}.");
                }
                else if (options.ServiceUri is { IsAbsoluteUri: false })
                {
                    failures.Add($"{BlobStoreOptions.SectionName}:{nameof(BlobStoreOptions.ServiceUri)} must be an absolute URI.");
                }

                break;
            default:
                failures.Add($"Unknown {BlobStoreOptions.SectionName}:{nameof(BlobStoreOptions.Provider)} '{options.Provider}'.");
                break;
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
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
