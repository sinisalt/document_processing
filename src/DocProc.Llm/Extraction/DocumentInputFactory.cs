using DocProc.Application.Abstractions;
using DocProc.Domain.Text;

namespace DocProc.Llm.Extraction;

/// <summary>
/// Builds the provider-neutral <see cref="DocumentInput"/> for each LLM stage from what the pipeline hands over:
/// the PDF when there is one (native PDF input, docs/06), otherwise page PNGs plus the text layer per page.
/// </summary>
public static class DocumentInputFactory
{
    public static DocumentInput ForExtraction(ExtractionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var fileName = $"{input.DocumentId:N}.pdf";
        var texts = PageTexts(input.TextLayer);
        if (input.Pdf is { Length: > 0 } pdf)
        {
            return new DocumentInput { FileName = fileName, Pdf = pdf, PageTexts = texts };
        }

        return new DocumentInput
        {
            FileName = fileName,
            PageImages = input.PageImages.Select((png, i) => ToPageImage(i + 1, png)).ToList(),
            PageTexts = texts,
        };
    }

    public static DocumentInput ForPages(IReadOnlyList<ReadOnlyMemory<byte>> pagePngs, string fileName = "document.pdf")
    {
        ArgumentNullException.ThrowIfNull(pagePngs);
        return new DocumentInput
        {
            FileName = fileName,
            PageImages = pagePngs.Select((png, i) => ToPageImage(i + 1, png)).ToList(),
        };
    }

    public static PageImage ToPageImage(int page, ReadOnlyMemory<byte> png)
    {
        PngDimensions.TryRead(png.Span, out var width, out var height);
        return new PageImage(page, png, width, height);
    }

    /// <summary>Plain text per page in page order; pages the layer does not have are empty strings so indexes stay page numbers.</summary>
    public static IReadOnlyList<string> PageTexts(TextLayer? layer)
    {
        if (layer is null || layer.Pages.Count == 0)
        {
            return [];
        }

        var count = Math.Max(layer.Pages.Count, layer.Pages.Max(p => p.Number));
        var texts = new string[count];
        Array.Fill(texts, string.Empty);
        foreach (var page in layer.Pages)
        {
            if (page.Number >= 1 && page.Number <= count)
            {
                texts[page.Number - 1] = page.PlainText;
            }
        }

        return texts;
    }
}
