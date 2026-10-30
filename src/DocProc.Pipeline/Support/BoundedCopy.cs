using System.Buffers;

namespace DocProc.Pipeline.Support;

/// <summary>
/// Reads a stream into memory with a hard ceiling. Archive entries declare their size in the central directory, and a
/// crafted zip can declare a few bytes while the deflate stream expands to gigabytes (a "zip bomb"), so stage 1 never
/// trusts the declared length alone: the copy stops as soon as the ceiling is crossed.
/// </summary>
internal static class BoundedCopy
{
    private const int ChunkSize = 81920;

    /// <summary>The whole content of <paramref name="source"/>, or null once more than <paramref name="limit"/> bytes were read.</summary>
    public static async Task<byte[]?> ReadAsync(Stream source, long limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        var chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            using var buffer = new MemoryStream();
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > limit)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }
}
