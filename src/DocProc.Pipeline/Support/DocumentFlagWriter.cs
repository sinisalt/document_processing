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

        foreach (var stale in document.Flags.Where(f => f.CreatedBy == FlagOrigin.Pipeline && f.ResolvedAt is null && FlagCodes.IsOwned(codes, f.Code)).ToList())
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

    /// <summary>The parameter that names the step that raised a flag whose code more than one step raises (docs/11 §10.3).</summary>
    public const string StepIdParam = "stepId";

    /// <summary>
    /// Replaces the unresolved pipeline flags of <paramref name="ownedCodes"/> that <paramref name="stepId"/> raised (their
    /// <see cref="StepIdParam"/>) with <paramref name="flags"/>, leaving the instances other steps raised (docs/11 §10.3:
    /// <c>NEW_LAYOUT</c> and <c>LAYOUT_AMBIGUOUS</c> are raised by <c>classify.identification</c> and <c>match.layout</c>,
    /// and each owns its own). The new flags carry the step id.
    /// </summary>
    public void ReplaceForStep(Document document, string stepId, IEnumerable<string> ownedCodes, IEnumerable<DocumentFlag> flags)
    {
        var codes = new HashSet<string>(ownedCodes, StringComparer.Ordinal);
        foreach (var stale in document.Flags.Where(f => f.CreatedBy == FlagOrigin.Pipeline && f.ResolvedAt is null && codes.Contains(f.Code)
                                                        && f.Params is { } p && p.TryGetValue(StepIdParam, out var owner) && owner == stepId).ToList())
        {
            document.Flags.Remove(stale);
            db.DocumentFlags.Remove(stale);
        }

        var now = clock.GetUtcNow();
        foreach (var flag in flags)
        {
            var parameters = new Dictionary<string, string>(flag.Params ?? new Dictionary<string, string>(), StringComparer.Ordinal) { [StepIdParam] = stepId };
            var record = DocumentFlagRecord.FromDomain(document.TenantId, document.Id, flag with { Params = parameters }, now);
            db.DocumentFlags.Add(record);
            document.Flags.Add(record);
        }
    }

    /// <summary>The open flags of the document, pipeline- or human-made.</summary>
    public static IReadOnlyList<DocumentFlagRecord> Unresolved(Document document) =>
        document.Flags.Where(f => f.ResolvedAt is null).ToList();
}
