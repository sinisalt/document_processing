using System.Text.RegularExpressions;
using DocProc.Definitions;
using DocProc.Domain.Fields;
using DocProc.Domain.Text;
using DocProc.Mapping.Tables;
using DocProc.Mapping.Text;
using DocProc.Mapping.Values;

namespace DocProc.Mapping.Tests;

/// <summary>
/// Synthetic word layouts in the normalised 1000 × 1000 page space: a word is 6 units wide per character and 10 high,
/// words of one cell 4 units apart. <see cref="PriceList"/> prints the price list of docs/11 §5.8 / §8.6 with the columns
/// Art.-Nr., Bezeichnung, Inhalt, VE, Preis, ab 10 and ab 50.
/// </summary>
internal sealed class PageBuilder(int number)
{
    private readonly List<PositionedWord> _words = [];

    public int Number { get; } = number;

    /// <summary>Prints <paramref name="text"/> starting at <paramref name="x"/> on the line at <paramref name="y"/>.</summary>
    public PageBuilder Text(double x, double y, string text)
    {
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var width = word.Length * 6;
            _words.Add(new PositionedWord(word, new BoundingBox(x, y, x + width, y + 10)));
            x += width + 4;
        }

        return this;
    }

    /// <summary>One table line: (x, text) per cell.</summary>
    public PageBuilder Line(double y, params (double X, string Text)[] cells)
    {
        foreach (var (x, text) in cells)
        {
            Text(x, y, text);
        }

        return this;
    }

    public TextPage Build() => new(Number, 1000, 1000, [.. _words]);

    public WordPage Words() => WordPage.From(Build());
}

internal static class PriceList
{
    // Column left edges of the printed layout (docs/11 §8.6 example), and the dividers a reviewer draws between them.
    public const double Sku = 55, Description = 155, Content = 505, Unit = 625, Price = 705, Tier10 = 805, Tier50 = 905;

    public static readonly IReadOnlyList<(double X0, double X1, string? Field, string Header, decimal? Tier)> Dividers =
    [
        (50, 150, "supplierSku", "Art.-Nr.", null),
        (150, 500, "description", "Bezeichnung", null),
        (500, 620, "packDescription", "Inhalt", null),
        (620, 700, "unit", "VE", null),
        (700, 800, "price", "Preis", null),
        (800, 900, null, "ab 10", 10),
        (900, 990, null, "ab 50", 50),
    ];

    public static readonly IReadOnlyDictionary<string, TableField> Fields = new Dictionary<string, TableField>(StringComparer.Ordinal)
    {
        ["supplierSku"] = new("supplierSku", FieldKinds.Identifier),
        ["description"] = new("description", FieldKinds.Text),
        ["packDescription"] = new("packDescription", FieldKinds.String),
        ["unit"] = new("unit", FieldKinds.Unit),
        ["price"] = new("price", FieldKinds.Money),
        ["category"] = new("category", FieldKinds.String),
    };

    public static readonly Regex TierPattern = new(@"ab\s+(\d+)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>Prints the header row at <paramref name="y"/>, shifted right by <paramref name="shift"/> and scaled by <paramref name="scale"/>.</summary>
    public static PageBuilder Header(this PageBuilder page, double y, double shift = 0, double scale = 1) =>
        page.Line(y, X(Sku, shift, scale, "Art.-Nr."), X(Description, shift, scale, "Bezeichnung"), X(Content, shift, scale, "Inhalt"),
            X(Unit, shift, scale, "VE"), X(Price, shift, scale, "Preis"), X(Tier10, shift, scale, "ab 10"), X(Tier50, shift, scale, "ab 50"));

    /// <summary>Prints one article row.</summary>
    public static PageBuilder Article(this PageBuilder page, double y, string sku, string description, string price, string? tier10 = null, string? tier50 = null, double shift = 0, double scale = 1, string content = "6 x 0,75 l", string unit = "KRT")
    {
        var cells = new List<(double, string)>
        {
            X(Sku, shift, scale, sku), X(Description, shift, scale, description), X(Content, shift, scale, content), X(Unit, shift, scale, unit), X(Price, shift, scale, price),
        };
        if (tier10 is not null)
        {
            cells.Add(X(Tier10, shift, scale, tier10));
        }

        if (tier50 is not null)
        {
            cells.Add(X(Tier50, shift, scale, tier50));
        }

        return page.Line(y, [.. cells]);
    }

    private static (double, string) X(double x, double shift, double scale, string text) => ((x * scale) + shift, text);

    /// <summary>The mapping a reviewer draws on page 1: the seven columns (two of them tier columns, named by their minimum quantity as the proposal names them), the header band and the box.</summary>
    public static ColumnTableSpec Spec(int page = 1, double headerY = 100, double top = 95, double bottom = 940, IReadOnlyList<int>? pages = null) => new()
    {
        Page = page,
        Region = new BoundingBox(40, top, 995, bottom),
        Columns = [.. Dividers.Select(d => new TableColumn(d.X0, d.X1, d.Field, TierMinQuantity: d.Tier))],
        Fields = Fields,
        HeaderRow = (headerY - 1, headerY + 11),
        RowAnchor = "supplierSku",
        Pages = pages,
        GroupHeader = new GroupHeaderSpec("category"),
        TierColumns = new TierColumnsSpec(TierPattern),
        Locale = ValueLocale.German,
    };
}
