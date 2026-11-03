using DocProc.Application.Configuration;
using DocProc.Domain.Documents;
using DocProc.Domain.Validation;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Pipeline.Support;

/// <summary>
/// The duplicate check of a type's <c>roles.duplicateKey</c> (docs/11 §11 "Duplicates"), which the generic
/// <c>duplicate</c> rule reports. <c>$supplierId</c> is the matched supplier; the component bound to
/// <c>documentNumber</c> is read from <c>DocumentNumberNormalized</c> (the existing index); every other component from
/// <c>DocumentFieldIndex</c>, which is why publishing requires those fields to be indexed (§5.7).
/// <list type="bullet">
/// <item><description>A null component matches only a null component of the other document.</description></item>
/// <item><description>A component bound to a required field that has no value is not compared: the check is skipped and
/// says so (<c>FIELD_REQUIRED</c> blocks already). It is skipped too when every component other than the supplier is
/// empty, and when the key names the supplier but none is matched ("no supplier" is not a supplier).</description></item>
/// <item><description>Rejected and failed documents are never matches. (Sandbox runs and superseded documents arrive
/// with later phases and are excluded then.)</description></item>
/// </list>
/// </summary>
public static class DuplicateKeyCheck
{
    public const string SupplierComponent = "$supplierId";

    /// <summary>The most matches reported; the flag names the first.</summary>
    private const int MaxMatches = 5;

    public static async Task<DuplicateCheck?> RunAsync(DocProcDbContext db, Document document, EffectiveType type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(type);
        var roles = type.Definition.Roles;
        if (roles.DuplicateKey is not { Count: > 0 } key)
        {
            return null;
        }

        var bySupplier = key.Contains(SupplierComponent, StringComparer.Ordinal);
        if (bySupplier && document.SupplierId is null)
        {
            return DuplicateCheck.Skipped("no supplier is matched");
        }

        var fields = key.Where(k => k != SupplierComponent).ToList();
        var numberField = fields.FirstOrDefault(f => f == roles.DocumentNumber);
        var indexedFields = fields.Where(f => f != numberField).ToList();
        var own = await db.DocumentFieldIndex.AsNoTracking()
            .Where(e => e.DocumentId == document.Id && indexedFields.Contains(e.FieldKey) && e.Ordinal == 0)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var ownValues = indexedFields.ToDictionary(f => f, f => own.FirstOrDefault(e => e.FieldKey == f) is { } e ? Value(e) : null, StringComparer.Ordinal);
        var ownNumber = numberField is null ? null : document.DocumentNumberNormalized;

        var descriptors = type.Compiled?.Descriptors;
        foreach (var field in fields)
        {
            var empty = field == numberField ? string.IsNullOrEmpty(ownNumber) : ownValues[field] is null;
            if (empty && descriptors?.Find(field) is { Required: true })
            {
                return DuplicateCheck.Skipped($"the required key component '{field}' has no value");
            }
        }

        if (fields.Count > 0 && string.IsNullOrEmpty(ownNumber) && ownValues.Values.All(v => v is null))
        {
            return DuplicateCheck.Skipped("every key component is empty");
        }

        var typeKey = document.Type;
        var candidates = db.Documents.AsNoTracking()
            .Where(d => d.Id != document.Id && d.Type == typeKey && d.Status != DocumentStatus.Failed && d.Status != DocumentStatus.Rejected);
        if (bySupplier)
        {
            var supplierId = document.SupplierId;
            candidates = candidates.Where(d => d.SupplierId == supplierId);
        }

        if (numberField is not null)
        {
            candidates = string.IsNullOrEmpty(ownNumber)
                ? candidates.Where(d => d.DocumentNumberNormalized == null || d.DocumentNumberNormalized == string.Empty)
                : candidates.Where(d => d.DocumentNumberNormalized == ownNumber);
        }

        foreach (var field in indexedFields)
        {
            var fieldKey = field;
            if (ownValues[field] is not { } value)
            {
                candidates = candidates.Where(d => !d.FieldIndex.Any(e => e.FieldKey == fieldKey && e.Ordinal == 0));
            }
            else if (value.Text is { } text)
            {
                candidates = candidates.Where(d => d.FieldIndex.Any(e => e.FieldKey == fieldKey && e.Ordinal == 0 && e.StringValue == text));
            }
            else if (value.Number is { } number)
            {
                candidates = candidates.Where(d => d.FieldIndex.Any(e => e.FieldKey == fieldKey && e.Ordinal == 0 && e.NumberValue == number));
            }
            else if (value.Date is { } date)
            {
                candidates = candidates.Where(d => d.FieldIndex.Any(e => e.FieldKey == fieldKey && e.Ordinal == 0 && e.DateValue == date));
            }
        }

        var matches = await candidates
            .OrderBy(d => d.Id)
            .Take(MaxMatches)
            .Select(d => new { d.Id, d.Status, d.DocumentNumber })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new DuplicateCheck([.. matches.Select(m => new DuplicateMatch(m.Id, m.Status.ToString(), m.DocumentNumber))]);
    }

    private static IndexValue? Value(DocumentFieldIndexEntry entry) =>
        entry.StringValue is null && entry.NumberValue is null && entry.DateValue is null ? null : new IndexValue(entry.StringValue, entry.NumberValue, entry.DateValue);

    private sealed record IndexValue(string? Text, decimal? Number, DateOnly? Date);
}
