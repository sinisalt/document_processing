namespace DocProc.Extraction.EInvoice;

/// <summary>
/// Resource limits for XML that arrives from untrusted mailboxes. EN 16931 documents are a few megabytes at most and
/// fewer than 20 elements deep, so the defaults are generous; anything beyond them is rejected before it can pin a
/// worker (Detect returns null, Parse throws <see cref="EInvoiceParseException"/>).
/// </summary>
public sealed record EInvoiceParserOptions
{
    public const int DefaultMaxDocumentBytes = 20 * 1024 * 1024;
    public const int DefaultMaxElementDepth = 256;

    public static readonly EInvoiceParserOptions Default = new();

    private readonly int _maxDocumentBytes = DefaultMaxDocumentBytes;
    private readonly int _maxElementDepth = DefaultMaxElementDepth;

    /// <summary>Largest XML payload, in bytes, that is parsed at all.</summary>
    public int MaxDocumentBytes
    {
        get => _maxDocumentBytes;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxDocumentBytes = value;
        }
    }

    /// <summary>Deepest element nesting that is read; the root element is at depth 0.</summary>
    public int MaxElementDepth
    {
        get => _maxElementDepth;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxElementDepth = value;
        }
    }
}
