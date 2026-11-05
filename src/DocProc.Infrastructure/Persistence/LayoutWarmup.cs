using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Persistence;

/// <summary>
/// The places of a layout version's review warm-up (docs/11 §8.3, §11; decision 2 of §24): the first
/// <c>ReviewWarmup</c> documents under an Active version go to review, each labelled with its own place ("document 1 of
/// the first 5").
/// </summary>
public static class LayoutWarmup
{
    /// <summary>
    /// Takes the next place of the version's warm-up and returns it (1-based), or null when every place is taken. The
    /// place is taken by a compare-and-set (<c>UPDATE ... SET WarmupSeen = n + 1 WHERE WarmupSeen = n AND n &lt;
    /// ReviewWarmup</c>), so the place returned is the one this caller's update wrote: two documents validated at once
    /// never get the same place, none is skipped, and none is over the warm-up. A caller that loses the race reads the
    /// count again; it loses only to a caller that took a place, so the loop ends within <c>ReviewWarmup</c> rounds.
    /// Called inside a transaction (the Validate stage's), the place is held until that transaction ends and given back
    /// if it rolls back: a caller waiting on the row then takes the same place.
    /// </summary>
    public static async Task<int?> TakePlaceAsync(DocProcDbContext db, Guid versionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        while (true)
        {
            var state = await db.SupplierLayoutVersions.AsNoTracking()
                .Where(v => v.Id == versionId)
                .Select(v => new { v.WarmupSeen, v.ReviewWarmup })
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (state is null || state.WarmupSeen >= state.ReviewWarmup)
            {
                return null;
            }

            var seen = state.WarmupSeen;
            var taken = await db.SupplierLayoutVersions
                .Where(v => v.Id == versionId && v.WarmupSeen == seen && v.WarmupSeen < v.ReviewWarmup)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.WarmupSeen, seen + 1), cancellationToken)
                .ConfigureAwait(false);
            if (taken == 1)
            {
                return seen + 1;
            }
        }
    }
}
