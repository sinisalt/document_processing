using DocProc.Application.Documents;
using DocProc.Domain.Common;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Infrastructure.Persistence;

/// <summary>
/// Writes a <see cref="RoleProjection"/> into the denormalised list columns of a <see cref="Document"/> (docs/02 §2:
/// they are always rewritten from the canonical JSON). The one writer of those columns, used by the Extract stage and
/// by reviewer corrections alike; it fits each value to its column so nothing the document printed can fail a save.
/// </summary>
public static class DocumentRoleColumns
{
    public const int MaxDocumentNumberLength = 100;

    /// <summary>
    /// Re-projects <see cref="Document.CanonicalJson"/> under <paramref name="bindings"/>, the role bindings of the
    /// document's resolved type definition (<c>EffectiveType.Roles</c>, docs/11 §6.2); a type without a definition
    /// (null) leaves the columns as they are.
    /// </summary>
    public static void Refresh(Document document, RoleBindings? bindings)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (bindings is null)
        {
            return;
        }

        Apply(document, RoleProjector.Project(bindings, document.CanonicalJson));
    }

    /// <summary>Sets the column of every bound role; columns of unbound roles keep their value.</summary>
    public static void Apply(Document document, RoleProjection projection)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(projection);
        var bindings = projection.Bindings;
        if (bindings.DocumentNumber is not null)
        {
            document.DocumentNumber = Truncate(projection.DocumentNumber?.Verbatim, MaxDocumentNumberLength);
            document.DocumentNumberNormalized = Truncate(projection.DocumentNumber?.Normalized, MaxDocumentNumberLength);
        }

        if (bindings.DocumentDate is not null)
        {
            document.IssueDate = projection.DocumentDate;
        }

        if (bindings.Currency is not null)
        {
            // Document.Currency is char(3): only a well-formed code goes into the list column; anything else stays in
            // the canonical JSON alone.
            var normalized = projection.Currency is null ? null : CurrencyCode.Normalize(projection.Currency);
            document.Currency = CurrencyCode.IsWellFormed(normalized) ? normalized : null;
        }

        if (bindings.Total is not null)
        {
            document.TotalGross = projection.Total;
        }

        if (bindings.ValidFrom is not null)
        {
            document.ValidFrom = projection.ValidFrom;
        }

        if (bindings.ValidUntil is not null)
        {
            document.ValidUntil = projection.ValidUntil;
        }

        if (bindings.LineItems is not null)
        {
            document.LineCount = projection.LineCount;
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
