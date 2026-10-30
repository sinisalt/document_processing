using System.Security.Cryptography;
using System.Text;
using DocProc.Application.Abstractions;

namespace DocProc.Infrastructure.Security;

/// <summary>
/// <see cref="ISecretStore"/> on a local directory for development and tests (docs/09): one file per reference,
/// named by the SHA-256 of the reference so a reference such as <c>tenant/{id}/{configId}</c> can never escape the
/// root, created owner-only (0600, directory 0700) on Unix. Writes go to a temp file first and are moved into
/// place, so a crash never leaves a half-written secret. Key Vault replaces this in Azure behind the same interface.
/// </summary>
public sealed class FileSecretStore : ISecretStore
{
    private const string Extension = ".secret";
    private static readonly UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly UnixFileMode OwnerOnlyDirectory = OwnerOnlyFile | UnixFileMode.UserExecute;

    public FileSecretStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(RootPath);
        }
        else
        {
            Directory.CreateDirectory(RootPath, OwnerOnlyDirectory);
            File.SetUnixFileMode(RootPath, OwnerOnlyDirectory);
        }
    }

    /// <summary>Absolute directory the secret files live in.</summary>
    public string RootPath { get; }

    /// <summary>The file a reference maps to; public so tests can assert on permissions.</summary>
    public string PathFor(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(reference)));
        return Path.Combine(RootPath, name + Extension);
    }

    public async Task SetAsync(string reference, string secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var path = PathFor(reference);
        var temp = Path.Combine(RootPath, $".{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerOnlyFile;
        }

        try
        {
            var stream = new FileStream(temp, options);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(secret), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public async Task<string?> GetAsync(string reference, CancellationToken cancellationToken)
    {
        var path = PathFor(reference);
        try
        {
            return await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Deletes the secret's file; a reference that was never stored (or was already deleted) is a no-op.</summary>
    public Task DeleteAsync(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(PathFor(reference));
        return Task.CompletedTask;
    }
}
