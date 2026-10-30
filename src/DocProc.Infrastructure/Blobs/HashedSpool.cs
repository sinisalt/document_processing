using System.Buffers;
using System.Security.Cryptography;

namespace DocProc.Infrastructure.Blobs;

/// <summary>
/// Copies an incoming stream to a temp file while computing its SHA-256, so a store knows the content-addressed key
/// before it has to place the bytes and never holds a whole document in memory. The file is deleted on dispose
/// unless it was moved away first.
/// </summary>
internal sealed class HashedSpool : IAsyncDisposable
{
    private const int BufferSize = 80 * 1024;

    private HashedSpool(string path, string sha256, long sizeBytes)
    {
        Path = path;
        Sha256 = sha256;
        SizeBytes = sizeBytes;
    }

    public string Path { get; }

    public string Sha256 { get; }

    public long SizeBytes { get; }

    public static async Task<HashedSpool> CreateAsync(Stream content, string spoolDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        Directory.CreateDirectory(spoolDirectory);
        var path = System.IO.Path.Combine(spoolDirectory, $"{Guid.NewGuid():N}.part");

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.Asynchronous);
            await using (file.ConfigureAwait(false))
            {
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    size += read;
                }

                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            return new HashedSpool(path, Convert.ToHexStringLower(hash.GetHashAndReset()), size);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }

        return ValueTask.CompletedTask;
    }
}
