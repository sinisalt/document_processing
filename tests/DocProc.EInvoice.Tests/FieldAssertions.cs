using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;

namespace DocProc.EInvoice.Tests;

/// <summary>Assertions over the provenance contract of docs/02-data-model.md §3 for the structured-XML path.</summary>
internal static class FieldAssertions
{
    public static Money Eur(decimal amount) => new(amount, "EUR");

    public static T ShouldBeExtracted<T>(this FieldValue<T> field, T expected)
    {
        field.Status.ShouldBe(FieldStatus.Extracted);
        field.Value.ShouldBe(expected);
        field.ShouldHaveXmlProvenance();
        return field.Value!;
    }

    public static void ShouldHaveXmlProvenance(this IFieldValue field)
    {
        field.Source.ShouldNotBeNull().Method.ShouldBe(SourceMethod.StructuredXml);
        field.Confidence.ShouldBe(1.0);
    }

    public static void ShouldBeMissing(this IFieldValue field)
    {
        field.Status.ShouldBe(FieldStatus.Missing);
        field.HasValue.ShouldBeFalse();
    }

    /// <summary>Unreadable with the raw text preserved on the source; returns that raw text.</summary>
    public static string ShouldBeUnreadable(this IFieldValue field)
    {
        field.Status.ShouldBe(FieldStatus.Unreadable);
        field.HasValue.ShouldBeFalse();
        field.Confidence.ShouldBe(0);
        var source = field.Source.ShouldNotBeNull();
        source.Method.ShouldBe(SourceMethod.StructuredXml);
        return source.RawText.ShouldNotBeNull();
    }

    /// <summary>Every present field, header and lines, carries StructuredXml provenance at confidence 1.0.</summary>
    public static void ShouldHaveXmlProvenanceEverywhere(this Invoice invoice)
    {
        var present = 0;
        foreach (var (path, field) in invoice.EnumerateAllFields())
        {
            if (!field.HasValue)
            {
                continue;
            }

            present++;
            field.Source.ShouldNotBeNull(path).Method.ShouldBe(SourceMethod.StructuredXml, path);
            field.Confidence.ShouldBe(1.0, path);
        }

        present.ShouldBeGreaterThan(0);
    }
}
