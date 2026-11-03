using DocProc.Domain.Fields;
using DocProc.Domain.Text;

namespace DocProc.Mapping.Text;

/// <summary>
/// A word of a page in the normalised 1000 × 1000 page space (docs/04), with its index in the stored text layer's word
/// list of that page, which is how a correction source names picked words (<c>words: [[page, index]]</c>).
/// </summary>
public sealed record IndexedWord(int Index, string Text, BoundingBox Box, double? Confidence = null);

/// <summary>One page of normalised, indexed words.</summary>
public sealed record WordPage(int Number, IReadOnlyList<IndexedWord> Words)
{
    /// <summary>The page in 1000 × 1000 units; a page without a usable size (an empty OCR result) has no words.</summary>
    public static WordPage From(TextPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Width <= 0 || page.Height <= 0)
        {
            return new WordPage(page.Number, []);
        }

        var normalized = page.Normalize();
        var words = new List<IndexedWord>(normalized.Words.Count);
        for (var i = 0; i < normalized.Words.Count; i++)
        {
            var word = normalized.Words[i];
            if (!string.IsNullOrWhiteSpace(word.Text))
            {
                words.Add(new IndexedWord(i, word.Text.Trim(), word.Box, word.Confidence));
            }
        }

        return new WordPage(page.Number, words);
    }
}

/// <summary>A line of words: top to bottom, then left to right within it.</summary>
public sealed record TextLine(IReadOnlyList<IndexedWord> Words, BoundingBox Box)
{
    public string Text => string.Join(' ', Words.Select(w => w.Text));
}

/// <summary>Geometry helpers over normalised words (docs/11 §12.3: hit-test, then join in reading order by y-bucket, then x).</summary>
public static class PageGeometry
{
    /// <summary>The words whose centre lies inside <paramref name="box"/>.</summary>
    public static IReadOnlyList<IndexedWord> Hit(IEnumerable<IndexedWord> words, BoundingBox box) =>
        [.. words.Where(w => box.Contains(w.Box.CenterX, w.Box.CenterY))];

    /// <summary>The union of the boxes, or null for none.</summary>
    public static BoundingBox? Union(IEnumerable<IndexedWord> words) => Union(words.Select(w => w.Box));

    public static BoundingBox? Union(IEnumerable<BoundingBox> boxes)
    {
        BoundingBox? union = null;
        foreach (var b in boxes)
        {
            union = union is { } u ? new BoundingBox(Math.Min(u.X0, b.X0), Math.Min(u.Y0, b.Y0), Math.Max(u.X1, b.X1), Math.Max(u.Y1, b.Y1)) : b;
        }

        return union;
    }

    /// <summary>
    /// Groups words into lines: a word joins the line whose vertical band holds its centre (or whose centre its own band
    /// holds), so a slightly raised superscript or a taller word stays on its line; lines top to bottom, words in a line
    /// left to right.
    /// </summary>
    public static IReadOnlyList<TextLine> Lines(IEnumerable<IndexedWord> words)
    {
        var lines = new List<(List<IndexedWord> Words, double Top, double Bottom)>();
        foreach (var word in words.OrderBy(w => w.Box.CenterY).ThenBy(w => w.Box.X0))
        {
            var placed = false;
            for (var i = lines.Count - 1; i >= 0 && i >= lines.Count - 3; i--)
            {
                var (members, top, bottom) = lines[i];
                var centre = word.Box.CenterY;
                var lineCentre = (top + bottom) / 2;
                if ((centre >= top && centre <= bottom) || (lineCentre >= word.Box.Y0 && lineCentre <= word.Box.Y1))
                {
                    members.Add(word);
                    lines[i] = (members, Math.Min(top, word.Box.Y0), Math.Max(bottom, word.Box.Y1));
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                lines.Add(([word], word.Box.Y0, word.Box.Y1));
            }
        }

        return [.. lines
            .Select(l => new TextLine([.. l.Words.OrderBy(w => w.Box.X0)], Union(l.Words)!.Value))
            .OrderBy(l => l.Box.Y0)];
    }

    /// <summary>The words joined in reading order (lines, then left to right).</summary>
    public static string ReadingText(IEnumerable<IndexedWord> words) => string.Join(' ', Lines(words).Select(l => l.Text));
}
