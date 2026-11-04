using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Pipeline.Layouts;

/// <summary>
/// Cuts a document query to its newest few in SQL, so a supplier with thousands of approved documents does not load
/// every canonical tree to keep twenty. The caller still orders the rows it reads (CreatedAt, then Id, descending) and
/// takes its count in memory, so the result is the one of ordering everything in memory: the cut keeps every document
/// at least as new as the <c>count</c>-th newest, ties at the cut included, and SQL never decides between equal
/// timestamps (it orders a <c>uniqueidentifier</c> unlike <see cref="Guid.CompareTo(Guid)"/>). Both providers order
/// and compare <c>CreatedAt</c> in SQL (SQLite stores UTC ticks, docs/09).
/// </summary>
internal static class NewestDocuments
{
    /// <summary><paramref name="query"/> with only the documents at least as new as its <paramref name="count"/>-th newest; all of them when it has fewer.</summary>
    public static async Task<IQueryable<Document>> CutAsync(IQueryable<Document> query, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var cut = await query
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => (DateTimeOffset?)d.CreatedAt)
            .Skip(count - 1)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return cut is { } at ? query.Where(d => d.CreatedAt >= at) : query;
    }
}
