using DocProc.Domain.Fields;
using DocProc.Domain.Text;

namespace DocProc.Integration.Tests.Classification;

/// <summary>
/// Builds synthetic text layers for the classifier and splitter tests: each line is placed at a given y (points from
/// the top of an A4 page) and its words are laid out left to right, so position-weighted rules see realistic boxes.
/// </summary>
internal static class TextLayers
{
    public const double PageWidth = 595;
    public const double PageHeight = 842;
    private const double FontSize = 10;
    private const double CharWidth = 5.5;

    public static TextLayer Layer(params TextPage[] pages) => new(TextLayerSource.PdfText, pages);

    public static TextPage Page(int number, params (double Y, string Text)[] lines)
    {
        var words = new List<PositionedWord>();
        foreach (var (y, text) in lines)
        {
            var x = 50d;
            foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var width = token.Length * CharWidth;
                words.Add(new PositionedWord(token, new BoundingBox(x, y - FontSize, x + width, y)));
                x += width + CharWidth;
            }
        }

        return new TextPage(number, PageWidth, PageHeight, words);
    }

    public static TextPage BlankPage(int number) => new(number, PageWidth, PageHeight, []);

    /// <summary>A page of body text only (no titles, labels or page numbers), as the middle page of a long invoice.</summary>
    public static TextPage BodyPage(int number) => Page(
        number,
        (120, "4 Kabelbinder 200 mm 500 Stk 0,05 EUR 25,00 EUR"),
        (136, "5 Dübel 8 mm 200 Stk 0,10 EUR 20,00 EUR"),
        (152, "6 Montagematerial pauschal 1 Stk 45,00 EUR 45,00 EUR"));
}
