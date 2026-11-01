using DocProc.Domain.Text;

namespace DocProc.Infrastructure.Classification;

/// <summary>
/// The plain text of a page (words joined by single spaces, as <see cref="TextPage.PlainText"/>) together with the
/// character offset of every word, so a regex match on the text maps back to the word and its box. That is what
/// lets the keyword rules weigh a hit by where it sits on the page.
/// </summary>
internal sealed class IndexedPageText
{
    private readonly int[] _wordStarts;

    private IndexedPageText(TextPage page, string text, int[] wordStarts)
    {
        Page = page;
        Text = text;
        _wordStarts = wordStarts;
    }

    public TextPage Page { get; }

    public string Text { get; }

    public int WordCount => Page.Words.Count;

    public static IndexedPageText From(TextPage page)
    {
        var starts = new int[page.Words.Count];
        var offset = 0;
        for (var i = 0; i < page.Words.Count; i++)
        {
            starts[i] = offset;
            offset += page.Words[i].Text.Length + 1;
        }

        return new IndexedPageText(page, page.PlainText, starts);
    }

    /// <summary>The index of the word that contains the character at <paramref name="charIndex"/>.</summary>
    public int WordIndexAt(int charIndex)
    {
        var index = Array.BinarySearch(_wordStarts, charIndex);
        if (index >= 0)
        {
            return index;
        }

        return Math.Max(0, ~index - 1);
    }

    /// <summary>Vertical position of a word as a fraction of the page height (0 = top edge, 1 = bottom edge).</summary>
    public double RelativeY(int wordIndex)
    {
        if (Page.Height <= 0)
        {
            return 0;
        }

        return Math.Clamp(Page.Words[wordIndex].Box.CenterY / Page.Height, 0, 1);
    }

    /// <summary>Lower-cased distinct words whose centre lies within the top <paramref name="fraction"/> of the page.</summary>
    public HashSet<string> WordsInTopBand(double fraction)
    {
        var band = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Page.Words.Count; i++)
        {
            if (RelativeY(i) < fraction)
            {
                band.Add(Page.Words[i].Text.ToUpperInvariant());
            }
        }

        return band;
    }
}
