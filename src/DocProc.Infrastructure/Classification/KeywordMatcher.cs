using System.Text.RegularExpressions;

namespace DocProc.Infrastructure.Classification;

/// <summary>
/// One keyword found on a page. <see cref="Kind"/> is the effective kind after context demotion: a title preceded
/// by an article or possessive is a <see cref="KeywordKind.Reference"/>, a title glued to a number marker
/// ("Lieferschein-Nr") is a <see cref="KeywordKind.Label"/>. <see cref="NumberValue"/> is the document number that
/// follows a label or a title ("Rechnungsnummer RE-2026-0042", "Račun br. 12/1/1"), when there is one.
/// </summary>
internal sealed record KeywordHit(KeywordRule Rule, KeywordKind Kind, int WordIndex, double RelativeY, string Text, string? NumberValue);

/// <summary>Runs the <see cref="KeywordRuleSet"/> over a page; shared by the classifier (stage 4) and the splitter (stage 3).</summary>
internal static class KeywordMatcher
{
    public static IReadOnlyList<KeywordHit> FindHits(IndexedPageText page) => FindHits(page, KeywordRuleTable.Default);

    /// <summary>The hits of <paramref name="table"/>'s rules (the built-in table, or a tenant's, docs/11 §9.4).</summary>
    public static IReadOnlyList<KeywordHit> FindHits(IndexedPageText page, KeywordRuleTable table)
    {
        if (page.WordCount == 0)
        {
            return [];
        }

        var hits = new List<KeywordHit>();
        foreach (var rule in table.Rules)
        {
            MatchCollection matches;
            try
            {
                matches = rule.Pattern.Matches(page.Text);
                _ = matches.Count;
            }
            catch (RegexMatchTimeoutException)
            {
                // A tenant rule that ran out of time (escaped literals make this unlikely) is skipped, not fatal.
                continue;
            }

            foreach (Match match in matches)
            {
                // "rechnung@lieferant.de" or "www.lieferschein-online.de" name a mailbox or a site, not the document.
                if (!IsInAddress(page, match.Index))
                {
                    hits.Add(ToHit(page, rule, match));
                }
            }
        }

        // "Invoice number INV-1", "Invoice date" or "Lieferschein-Nr. 4711" match both the title rule and an explicit
        // label rule at the same word. The label rule knows better: drop the title-rule hit so the line is not scored
        // as a title as well.
        var labelled = hits
            .Where(h => h.Rule.Kind is KeywordKind.Label or KeywordKind.DateLabel)
            .Select(h => (h.Rule.Type, h.WordIndex))
            .ToHashSet();
        hits.RemoveAll(h => h.Rule.Kind == KeywordKind.Title && labelled.Contains((h.Rule.Type, h.WordIndex)));

        return hits;
    }

    /// <summary>Whether the word holding the character at <paramref name="charIndex"/> is an e-mail address or a web address.</summary>
    internal static bool IsInAddress(IndexedPageText page, int charIndex)
    {
        var word = page.Page.Words[page.WordIndexAt(charIndex)].Text;
        return word.Contains('@', StringComparison.Ordinal)
               || word.Contains("://", StringComparison.Ordinal)
               || word.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
    }

    private static KeywordHit ToHit(IndexedPageText page, KeywordRule rule, Match match)
    {
        var wordIndex = page.WordIndexAt(match.Index);
        var end = match.Index + match.Length;
        var kind = rule.Kind;
        string? value = null;

        if (kind == KeywordKind.Title)
        {
            if (KeywordRuleSet.ReferencePrefix.IsMatch(page.Text.AsSpan(0, match.Index)))
            {
                // "zur Rechnung Nr. 42", "Ihre Bestellung", "the invoice": a mention of another document.
                kind = KeywordKind.Reference;
            }
            else
            {
                // "Račun br. 12/1/1", "Fattura n. 118", "Rechnung Nr. 42" are the usual titles; the marker only moves the
                // number. Glued with a hyphen ("Gutschrift-Nr") it is a compound label instead.
                var marker = KeywordRuleSet.NumberMarkerAfterTitle.Match(page.Text, end);
                if (marker.Success)
                {
                    if (page.Text[end] == '-')
                    {
                        kind = KeywordKind.Label;
                    }

                    end = marker.Index + marker.Length;
                }
            }
        }

        // A number label carries its number; so does a title used as one ("Rechnung RE-2026-0042" in a
        // continuation-page header). The splitter tracks these values; the classifier still scores a title as a
        // title. A date label's value is a date, which identifies nothing, so it carries no value.
        if (kind is KeywordKind.Label or KeywordKind.Title)
        {
            var number = KeywordRuleSet.NumberValueAfterLabel.Match(page.Text, end);
            if (number.Success)
            {
                value = number.Groups["value"].Value.TrimEnd('.', ',', ':', ';');
            }
        }

        return new KeywordHit(rule, kind, wordIndex, page.RelativeY(wordIndex), match.Value, value);
    }
}
