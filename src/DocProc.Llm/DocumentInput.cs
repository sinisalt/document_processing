namespace DocProc.Llm;

/// <summary>A rendered page, PNG-encoded.</summary>
public sealed record PageImage(int Page, ReadOnlyMemory<byte> Png, int Width, int Height);

/// <summary>
/// The document in every form a provider might want. Adapters pick by capability: native PDF when supported,
/// otherwise page images plus the text layer rendered as text (docs/08-llm-providers-and-byok.md).
/// </summary>
public sealed record DocumentInput
{
    public string FileName { get; init; } = "document.pdf";

    public ReadOnlyMemory<byte>? Pdf { get; init; }

    public IReadOnlyList<PageImage> PageImages { get; init; } = [];

    /// <summary>Plain text of the positioned text layer, page by page.</summary>
    public IReadOnlyList<string> PageTexts { get; init; } = [];

    public int PageCount => Math.Max(PageImages.Count, PageTexts.Count);

    public bool HasPdf => Pdf is { Length: > 0 };

    public bool HasImages => PageImages.Count > 0;

    public bool HasText => PageTexts.Any(t => !string.IsNullOrWhiteSpace(t));
}
