using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace DocProc.Pipeline.Tests;

/// <summary>Zip archives built at test time, including the malformed ones stage 1 has to survive.</summary>
internal static class TestZips
{
    public const string MimeType = "application/zip";

    private static readonly byte[] LocalHeaderSignature = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] CentralHeaderSignature = [0x50, 0x4B, 0x01, 0x02];

    /// <summary>A well-formed archive with the given entries, deflated.</summary>
    public static byte[] With(params (string Name, byte[] Content)[] entries) => Build(CompressionLevel.Optimal, entries);

    /// <summary>A local file header signature followed by garbage: looks like a zip to a sniffer, has no central directory.</summary>
    public static byte[] Corrupt() => [.. LocalHeaderSignature, .. Encoding.ASCII.GetBytes("garbage")];

    /// <summary>
    /// A valid archive with one stored (uncompressed) entry whose headers declare <paramref name="declaredLength"/>
    /// uncompressed bytes while the entry really holds <paramref name="content"/>. <c>ZipArchiveEntry.Length</c>
    /// reports the lie; <c>Open()</c> yields the real bytes, because a stored entry is read by its compressed size.
    /// (A deflated entry cannot lie this way: .NET bounds the inflater by the declared size.) A reader that sizes its
    /// work by <c>Length</c> alone is fooled.
    /// </summary>
    public static byte[] LyingAboutSize(string name, byte[] content, uint declaredLength)
    {
        var zip = Build(CompressionLevel.NoCompression, (name, content));

        // Local file header: signature(4) version(2) flags(2) method(2) time(2) date(2) crc(4) compressed(4) uncompressed(4).
        var local = IndexOf(zip, LocalHeaderSignature, fromEnd: false);
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(local + 22, 4), declaredLength);

        // Central directory header: signature(4) made-by(2) needed(2) flags(2) method(2) time(2) date(2) crc(4) compressed(4) uncompressed(4).
        var central = IndexOf(zip, CentralHeaderSignature, fromEnd: true);
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(central + 24, 4), declaredLength);
        return zip;
    }

    private static byte[] Build(CompressionLevel level, params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name, level);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static int IndexOf(byte[] haystack, byte[] needle, bool fromEnd)
    {
        var span = haystack.AsSpan();
        var index = fromEnd ? span.LastIndexOf(needle) : span.IndexOf(needle);
        return index >= 0 ? index : throw new InvalidOperationException("The archive has no such header.");
    }
}
