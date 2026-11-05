using System.Globalization;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Infrastructure.Persistence;

/// <summary>
/// One <c>?field.&lt;key&gt;=value</c> filter of the document list (docs/11 §18) in each form the field index stores a
/// value (<see cref="DocumentFieldIndexWriter"/>): the normalised text, the number and the date, each only when the raw
/// value can be one. A document matches when an index row of that key holds the value in any of the forms.
/// </summary>
/// <param name="Key">The field's definition path (<c>supplierCustomerRef</c>, <c>lines[*].supplierSku</c>).</param>
/// <param name="Text">The value normalised as the index stores strings and identifiers.</param>
/// <param name="Number">The value as a number, when it is one the index can hold.</param>
/// <param name="Date">The value as a date (<c>yyyy-MM-dd</c>), when it is one.</param>
public sealed record DocumentFieldFilter(string Key, string Text, decimal? Number, DateOnly? Date)
{
    /// <summary>
    /// The filter for <paramref name="raw"/>: a number only when it parses and fits what the index holds
    /// (<see cref="DocumentFieldIndexWriter.IsIndexableNumber"/>). A larger one matches no indexed number, and as a
    /// parameter of the decimal(28,8) column it would overflow on SQL Server (an error, not an empty page), so it is
    /// compared as text only.
    /// </summary>
    public static DocumentFieldFilter Of(string key, string raw)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(raw);
        decimal? number = decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) && DocumentFieldIndexWriter.IsIndexableNumber(n) ? n : null;
        DateOnly? date = DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        return new DocumentFieldFilter(key, DocumentFieldIndexWriter.Normalize(raw), number, date);
    }

    /// <summary>The documents of <paramref name="query"/> that hold this value in the field.</summary>
    public IQueryable<Document> Apply(IQueryable<Document> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var fieldKey = Key;
        var text = Text;
        var number = Number;
        var date = Date;
        return query.Where(d => d.FieldIndex.Any(e => e.FieldKey == fieldKey
                                                      && (e.StringValue == text
                                                          || (number != null && e.NumberValue == number)
                                                          || (date != null && e.DateValue == date))));
    }
}
