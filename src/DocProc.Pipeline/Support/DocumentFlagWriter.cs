using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Pipeline.Support;

/// <summary>
/// Persists pipeline flags per stage. Each stage owns a set of codes; running it again (a retry, a re-process)
/// replaces the unresolved pipeline flags with those codes instead of stacking duplicates. Human-created or resolved
/// flags are never touched. The document's <c>Flags</c> collection must be loaded.
/// </summary>
internal sealed class DocumentFlagWriter(DocProcDbContext db, TimeProvider clock)
{
    public void Replace(Document document, IEnumerable<string> ownedCodes, IEnumerable<DocumentFlag> flags)
    {
        var incoming = flags.ToList();
        var codes = new HashSet<string>(ownedCodes, StringComparer.Ordinal);
        codes.UnionWith(incoming.Select(f => f.Code));

        foreach (var stale in document.Flags.Where(f => f.CreatedBy == FlagOrigin.Pipeline && f.ResolvedAt is null && codes.Contains(f.Code)).ToList())
        {
            document.Flags.Remove(stale);
            db.DocumentFlags.Remove(stale);
        }

        var now = clock.GetUtcNow();
        foreach (var flag in incoming)
        {
            // Added through the set, not only the navigation: a record with a pre-set id discovered by DetectChanges
            // would be tracked as an existing row and the insert would never happen.
            var record = DocumentFlagRecord.FromDomain(document.TenantId, document.Id, flag, now);
            db.DocumentFlags.Add(record);
            document.Flags.Add(record);
        }
    }

    /// <summary>The open flags of the document, pipeline- or human-made.</summary>
    public static IReadOnlyList<DocumentFlagRecord> Unresolved(Document document) =>
        document.Flags.Where(f => f.ResolvedAt is null).ToList();
}
