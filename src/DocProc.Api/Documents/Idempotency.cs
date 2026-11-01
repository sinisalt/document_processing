using System.Collections.Concurrent;
using System.Data.Common;
using DocProc.Api.Endpoints;
using DocProc.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Api.Documents;

/// <summary>Recognises the one failure of <c>POST /ingestions</c> that means "another request with the same key got there first".</summary>
public static class IdempotencyConflicts
{
    /// <summary>The unique index of <c>IdempotencyKeys(TenantId, Key)</c> as the migration names it (SQL Server reports the index name).</summary>
    public const string UniqueIndexName = "IX_IdempotencyKeys_TenantId_Key";

    /// <summary>SQLite reports the columns instead ("UNIQUE constraint failed: IdempotencyKeys.TenantId, IdempotencyKeys.Key").</summary>
    private const string SqliteConstraintText = "IdempotencyKeys.Key";

    /// <summary>
    /// True only when the save failed on the unique (TenantId, Key) index. Any other save failure (a missing channel,
    /// a truncated column, a lost connection) is not a conflict and must surface as the error it is.
    /// </summary>
    public static bool IsUniqueKeyViolation(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner.Message.Contains(UniqueIndexName, StringComparison.OrdinalIgnoreCase)
                || inner.Message.Contains(SqliteConstraintText, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the save failed because another request with the same key committed between this request's lookup and
    /// its save. That is either the unique index firing on the insert (<see cref="IsUniqueKeyViolation"/>) or, when
    /// this request was replacing an expired row, the delete of that row affecting nothing because the other request
    /// already replaced it: EF reports that as <see cref="DbUpdateConcurrencyException"/>. The latter only counts when
    /// an expired row really was being replaced; a concurrency failure on a plain insert is a bug, not a race.
    /// </summary>
    public static bool IsConcurrentKeyWrite(DbUpdateException exception, bool replacedExpiredRow)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return IsUniqueKeyViolation(exception) || (replacedExpiredRow && exception is DbUpdateConcurrencyException);
    }
}

/// <summary>
/// Removes idempotency keys older than <see cref="IngestionLimits.IdempotencyWindow"/>. It runs opportunistically on
/// upload, at most once an hour per tenant, through the request's own tenant-scoped context, so it never needs a
/// cross-tenant job and never races a request of another tenant. An expired key that is reused before the purge gets
/// to it is replaced by the upload itself (<c>IngestionEndpoints.UploadAsync</c>); this only keeps the table small.
/// </summary>
public sealed class IdempotencyKeyPurge(ILogger<IdempotencyKeyPurge> logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _nextDue = new();

    /// <summary>Deletes the tenant's expired keys when the tenant is due; a failure is logged, never surfaced, because the upload it follows has already been saved.</summary>
    public async Task RunIfDueAsync(DocProcDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var due = _nextDue.GetOrAdd(tenantId, DateTimeOffset.MinValue);
        if (now < due || !_nextDue.TryUpdate(tenantId, now + Interval, due))
        {
            return;
        }

        var before = now - IngestionLimits.IdempotencyWindow;
        try
        {
            var removed = await db.IdempotencyKeys.Where(k => k.CreatedAt < before).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            if (removed > 0 && logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Purged {Count} expired idempotency keys of tenant {TenantId}.", removed, tenantId);
            }
        }
        catch (DbException ex)
        {
            logger.LogWarning(ex, "Purging expired idempotency keys of tenant {TenantId} failed; the next upload retries.", tenantId);
            _nextDue.TryUpdate(tenantId, now, now + Interval);
        }
    }
}
