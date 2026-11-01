using DocProc.Domain.Fields;
using DocProc.Domain.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace DocProc.Infrastructure.Pdf;

/// <summary>
/// Positioned words from a PdfPig page in the coordinate space the rest of the system uses: PDF points with the
/// origin at the top-left corner and y growing downwards (docs/04 normalises these boxes to 1000 × 1000 later).
/// PdfPig reports boxes with a bottom-left origin, so y is flipped against the page height.
/// </summary>
internal static class PdfTextLayerExtractor
{
    /// <summary>
    /// Two words belong to the same line when the vertical centre of one lies inside the other's box. Used to put the
    /// words into reading order (top to bottom, then left to right), which the content stream does not guarantee.
    /// </summary>
    public static TextPage ExtractPage(Page page)
    {
        var height = page.Height;
        var words = page.GetWords(NearestNeighbourWordExtractor.Instance)
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .Select(w => ToPositionedWord(w, height))
            .ToList();

        return new TextPage(page.Number, page.Width, page.Height, ReadingOrder.Sort(words));
    }

    public static int CountWords(Page page) =>
        page.GetWords(NearestNeighbourWordExtractor.Instance).Count(w => !string.IsNullOrWhiteSpace(w.Text));

    private static PositionedWord ToPositionedWord(Word word, double pageHeight)
    {
        var box = word.BoundingBox;
        var x0 = Math.Min(box.Left, box.Right);
        var x1 = Math.Max(box.Left, box.Right);
        var top = Math.Max(box.Top, box.Bottom);
        var bottom = Math.Min(box.Top, box.Bottom);
        return new PositionedWord(word.Text.Trim(), new BoundingBox(x0, pageHeight - top, x1, pageHeight - bottom));
    }
}

/// <summary>Sorts positioned words top-to-bottom, left-to-right by clustering them into lines.</summary>
internal static class ReadingOrder
{
    public static IReadOnlyList<PositionedWord> Sort(IReadOnlyList<PositionedWord> words)
    {
        if (words.Count < 2)
        {
            return words;
        }

        var byTop = words.OrderBy(w => w.Box.Y0).ThenBy(w => w.Box.X0).ToList();
        var lines = new List<List<PositionedWord>>();
        foreach (var word in byTop)
        {
            var line = lines.LastOrDefault(l => SameLine(l, word));
            if (line is null)
            {
                lines.Add([word]);
            }
            else
            {
                line.Add(word);
            }
        }

        return lines
            .OrderBy(l => l.Min(w => w.Box.Y0))
            .SelectMany(l => l.OrderBy(w => w.Box.X0))
            .ToList();
    }

    private static bool SameLine(List<PositionedWord> line, PositionedWord word)
    {
        var lineTop = line.Min(w => w.Box.Y0);
        var lineBottom = line.Max(w => w.Box.Y1);
        var centre = word.Box.CenterY;
        var lineCentre = (lineTop + lineBottom) / 2;
        return (centre >= lineTop && centre <= lineBottom) || (lineCentre >= word.Box.Y0 && lineCentre <= word.Box.Y1);
    }
}
