using DocProc.Application.Abstractions;

namespace DocProc.Llm.Extraction;

/// <summary>
/// Turns the model's page ranges into what stage 3 requires: 1-based, contiguous, non-overlapping and covering
/// every page. Gaps are given to the preceding document (a blank or separator page belongs to the document before
/// it), overlaps are cut at the start of the later range, and an empty or wholly invalid answer is one document.
/// </summary>
public static class SplitRangeNormalizer
{
    public static IReadOnlyList<PageRange> Normalize(IEnumerable<PageRange> ranges, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        if (pageCount < 1)
        {
            return [];
        }

        var result = new List<PageRange>();
        var cursor = 1;
        foreach (var range in ranges.OrderBy(r => r.StartPage).ThenBy(r => r.EndPage))
        {
            var start = Math.Max(range.StartPage, cursor);
            var end = Math.Min(range.EndPage, pageCount);
            if (end < start)
            {
                continue;
            }

            if (start > cursor)
            {
                if (result.Count == 0)
                {
                    start = cursor;
                }
                else
                {
                    result[^1] = result[^1] with { EndPage = start - 1 };
                }
            }

            result.Add(new PageRange(start, end));
            cursor = end + 1;
        }

        if (result.Count == 0)
        {
            return [new PageRange(1, pageCount)];
        }

        if (cursor <= pageCount)
        {
            result[^1] = result[^1] with { EndPage = pageCount };
        }

        return result;
    }
}
