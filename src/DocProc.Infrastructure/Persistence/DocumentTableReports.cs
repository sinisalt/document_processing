using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Persistence;

/// <summary>
/// Writes and drops the table reports of a document (docs/11 §6.2 <c>DocumentTableReport</c>) as tracked changes, so they
/// commit in the same save as the rows they describe.
/// </summary>
public static class DocumentTableReports
{
    /// <summary>
    /// Marks every table report of the document deleted: its groups are about to be written by another source (the
    /// Extract stage, a new type, a split or join), so no report may describe rows that are no longer there.
    /// </summary>
    public static async Task RemoveAllAsync(DocProcDbContext db, Guid documentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        foreach (var report in await db.DocumentTableReports.Where(r => r.DocumentId == documentId).ToListAsync(ct).ConfigureAwait(false))
        {
            db.DocumentTableReports.Remove(report);
        }
    }

    /// <summary>Marks the report of <paramref name="path"/> deleted, when there is one (a reviewer replaced all its rows).</summary>
    public static async Task RemoveAsync(DocProcDbContext db, Guid documentId, string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (await db.DocumentTableReports.FirstOrDefaultAsync(r => r.DocumentId == documentId && r.Path == path, ct).ConfigureAwait(false) is { } report)
        {
            db.DocumentTableReports.Remove(report);
        }
    }

    /// <summary>The report of <paramref name="report"/>'s path replaces the one stored for it (docs/11 §12.4).</summary>
    public static async Task ReplaceAsync(DocProcDbContext db, DocumentTableReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(report);
        var existing = await db.DocumentTableReports.FirstOrDefaultAsync(r => r.DocumentId == report.DocumentId && r.Path == report.Path, ct).ConfigureAwait(false);
        if (existing is null)
        {
            db.DocumentTableReports.Add(report);
            return;
        }

        existing.Origin = report.Origin;
        existing.StepId = report.StepId;
        existing.CorrectionId = report.CorrectionId;
        existing.Correction = report.Correction;
        existing.ProducedAtVersion = report.ProducedAtVersion;
        existing.ReportJson = report.ReportJson;
        existing.CreatedAt = report.CreatedAt;
    }
}
