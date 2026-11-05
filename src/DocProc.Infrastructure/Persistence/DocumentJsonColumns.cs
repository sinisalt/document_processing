using System.Data;
using System.Data.Common;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DocProc.Infrastructure.Persistence;

/// <summary>
/// Reads a document whose canonical tree is large (a 400-row price list's is about 1 MB) without the cost SqlClient puts
/// on reading a large value asynchronously in a buffered row: measured on SQL Server 2025 for a 1.1 MB tree, an async
/// entity read took 1.5 s, the same read sync 0.15 s, and the value streamed with <see cref="CommandBehavior.SequentialAccess"/>
/// 0.08 s (<c>LargeDocumentReadSqlTests</c>). EF Core reads entities buffered, so <see cref="WithoutTrees"/> loads the
/// row with the two large json columns (<see cref="Document.CanonicalJson"/>, <see cref="Document.AppliedConfigJson"/>) as
/// NULL, and <see cref="LoadTreesAsync"/> streams them into the entity afterwards, as if they had been loaded with it (a
/// tracked entity does not count them as changed). Lists never read them at all.
/// </summary>
public static class DocumentJsonColumns
{
    private static readonly string[] Trees = [nameof(Document.CanonicalJson), nameof(Document.AppliedConfigJson)];

    /// <summary>
    /// The documents of <paramref name="db"/> (with its query filters, tracked or not as the caller composes) with the
    /// canonical tree and the applied configuration left out (NULL): every other column as EF maps it.
    /// </summary>
    public static IQueryable<Document> WithoutTrees(DocProcDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var entity = db.Model.FindEntityType(typeof(Document))!;
        var table = entity.GetTableMappings().Single().Table;
        var columns = table.Columns
            .Select(c => Trees.Contains(c.Name, StringComparer.Ordinal) ? $"NULL AS [{c.Name}]" : $"[{c.Name}]")
            .ToList();
#pragma warning disable EF1002 // the statement is made of the model's own column and table names, nothing from a request
        return db.Documents.FromSqlRaw($"SELECT {string.Join(", ", columns)} FROM [{table.Name}]");
#pragma warning restore EF1002
    }

    /// <summary>
    /// Streams the canonical tree and the applied configuration of <paramref name="document"/> (loaded by
    /// <see cref="WithoutTrees"/>) into it. A tracked entity keeps them as its original values, so a save writes them only
    /// when the caller changes them.
    /// </summary>
    public static async Task LoadTreesAsync(DocProcDbContext db, Document document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(document);
        var entry = db.Entry(document);
        if (entry.State != EntityState.Detached && (document.CanonicalJson is not null || document.AppliedConfigJson is not null))
        {
            return; // the context tracked the document already, with its trees (and perhaps the caller's changes to them)
        }

        var (canonical, applied) = await ReadAsync(db, document.Id, cancellationToken).ConfigureAwait(false);
        if (entry.State == EntityState.Detached)
        {
            document.CanonicalJson = canonical;
            document.AppliedConfigJson = applied;
            return;
        }

        foreach (var (name, value) in new[] { (nameof(Document.CanonicalJson), canonical), (nameof(Document.AppliedConfigJson), applied) })
        {
            var property = entry.Property(name);
            property.CurrentValue = value;
            property.OriginalValue = value;
            property.IsModified = false;
        }
    }

    private static async Task<(string? Canonical, string? Applied)> ReadAsync(DocProcDbContext db, Guid id, CancellationToken cancellationToken)
    {
        // The connection the context opens (and sets the tenant's session context on, for row-level security); the
        // row was found through the context's filters already, so it is the caller's.
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = $"SELECT [{Trees[0]}], [{Trees[1]}] FROM [Documents] WHERE [Id] = @id";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@id";
            parameter.Value = id;
            command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return (null, null);
            }

            var canonical = await TextAsync(reader, 0, cancellationToken).ConfigureAwait(false);
            var applied = await TextAsync(reader, 1, cancellationToken).ConfigureAwait(false);
            return (canonical, applied);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task<string?> TextAsync(DbDataReader reader, int ordinal, CancellationToken cancellationToken)
    {
        if (await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        using var text = reader.GetTextReader(ordinal);
        return await text.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
