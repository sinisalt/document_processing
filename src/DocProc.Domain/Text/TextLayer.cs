using DocProc.Domain.Fields;

namespace DocProc.Domain.Text;

public enum TextLayerSource
{
    /// <summary>Words came from the PDF's own text layer (digital-native document).</summary>
    PdfText,

    /// <summary>Words came from OCR over page images.</summary>
    Ocr,
}

/// <summary>A word with its box, in the page's own coordinate space.</summary>
public sealed record PositionedWord(string Text, BoundingBox Box, double? Confidence = null);

/// <summary>One page of positioned words.</summary>
public sealed record TextPage(int Number, double Width, double Height, IReadOnlyList<PositionedWord> Words)
{
    public const double NormalizedSize = 1000d;

    /// <summary>Plain text in reading order (as supplied), used for keyword rules and LLM prompts.</summary>
    public string PlainText => string.Join(' ', Words.Select(w => w.Text));

    /// <summary>Rescales all word boxes to the 1000 × 1000 space used by extractor profiles.</summary>
    public TextPage Normalize()
    {
        if (Width <= 0 || Height <= 0)
        {
            throw new InvalidOperationException($"Page {Number} has no usable size ({Width} × {Height}).");
        }

        var sx = NormalizedSize / Width;
        var sy = NormalizedSize / Height;
        var words = Words
            .Select(w => w with { Box = new BoundingBox(w.Box.X0 * sx, w.Box.Y0 * sy, w.Box.X1 * sx, w.Box.Y1 * sy) })
            .ToArray();
        return new TextPage(Number, NormalizedSize, NormalizedSize, words);
    }
}

/// <summary>Positioned words for every page of a document, from PDF text or OCR.</summary>
public sealed record TextLayer(TextLayerSource Source, IReadOnlyList<TextPage> Pages)
{
    public int WordCount => Pages.Sum(p => p.Words.Count);

    /// <summary>Words per page; a crude but effective readability signal for the UNREADABLE_PAGES flag.</summary>
    public double AverageWordsPerPage => Pages.Count == 0 ? 0 : (double)WordCount / Pages.Count;

    public string PlainText => string.Join('\n', Pages.Select(p => p.PlainText));
}
