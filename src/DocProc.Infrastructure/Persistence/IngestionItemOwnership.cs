using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Persistence;

/// <summary>
/// The database rule behind exact-duplicate detection (docs/03 "Implementation"): at most one
/// <see cref="ItemDisposition.Accepted"/> item per tenant and SHA-256, so two processes that decide the same bytes at
/// the same time cannot both create a document. The rule is a filtered unique index on
/// <c>IngestionItems(TenantId, Sha256) WHERE Disposition = 'Accepted'</c> (migration <c>AddItemOwnershipIndex</c>);
/// the loser's <c>SaveChanges</c> fails with a <see cref="DbUpdateException"/> that <see cref="IsViolation"/> recognises
/// on both providers, and it re-decides its items against the committed owner.
/// </summary>
public static class IngestionItemOwnership
{
    /// <summary>The index as the migration names it; SQL Server reports the name in the violation message.</summary>
    public const string IndexName = "UX_IngestionItems_TenantId_Sha256_Accepted";

    /// <summary>The filter as SQL Server stores it (<c>sys.indexes.filter_definition</c> normalises it to this shape).</summary>
    public const string SqlServerFilter = "[Disposition] = N'Accepted'";

    /// <summary>SQLite reports the columns instead ("UNIQUE constraint failed: IngestionItems.TenantId, IngestionItems.Sha256").</summary>
    private const string SqliteConstraintText = "IngestionItems.TenantId, IngestionItems.Sha256";

    private const string SqliteFilter = "\"Disposition\" = 'Accepted'";

    /// <summary>
    /// Adds the index to the model with the filter in the provider's SQL (enums are stored as strings, see
    /// <see cref="DocProcDbContext.OnModelCreating"/>). It is named explicitly so it exists next to the plain
    /// <c>(TenantId, Sha256)</c> index that serves the duplicate lookup over every disposition.
    /// </summary>
    internal static void Configure(ModelBuilder modelBuilder, bool sqlite)
    {
        modelBuilder.Entity<IngestionItem>()
            .HasIndex(i => new { i.TenantId, i.Sha256 }, IndexName)
            .IsUnique()
            .HasFilter(sqlite ? SqliteFilter : SqlServerFilter);
    }

    /// <summary>
    /// True only when the save failed on the ownership index: another writer committed an accepted item with the same
    /// tenant and bytes first. Any other save failure (a missing ingestion, a truncated column, a lost connection) is
    /// not a race and must surface as the error it is.
    /// </summary>
    public static bool IsViolation(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner.Message.Contains(IndexName, StringComparison.OrdinalIgnoreCase)
                || inner.Message.Contains(SqliteConstraintText, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
