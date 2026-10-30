using System.Buffers.Binary;

namespace DocProc.Llm.Extraction;

/// <summary>Reads width and height from a PNG's IHDR chunk, so page images carry their size without an imaging library.</summary>
public static class PngDimensions
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private const int HeaderLength = 24;

    public static bool TryRead(ReadOnlySpan<byte> png, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (png.Length < HeaderLength || !png[..Signature.Length].SequenceEqual(Signature) || !png.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        var w = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4));
        var h = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4));
        if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue)
        {
            return false;
        }

        width = (int)w;
        height = (int)h;
        return true;
    }
}
