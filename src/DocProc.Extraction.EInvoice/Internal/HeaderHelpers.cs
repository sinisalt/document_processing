using System.Xml.Linq;
using DocProc.Domain.Common;
using DocProc.Domain.Fields;

namespace DocProc.Extraction.EInvoice.Internal;

/// <summary>Header mapping shared by the UBL and CII mappers.</summary>
internal static class HeaderHelpers
{
    /// <summary>The document currency code, normalised to upper case; the raw text is kept on the source.</summary>
    public static FieldValue<string> Currency(XElement? element)
    {
        var text = element.Text();
        return text is null ? FieldValue<string>.Missing() : XmlFields.Extracted(CurrencyCode.Normalize(text), text);
    }

    /// <summary>
    /// A service period needs both ends. Both absent is Missing; one absent or unparsable is Unreadable, because
    /// completing the period from one date would be a guess.
    /// </summary>
    public static FieldValue<DatePeriod> Period(FieldValue<DateOnly> from, FieldValue<DateOnly> to)
    {
        if (from.HasValue && to.HasValue)
        {
            return XmlFields.Extracted(new DatePeriod(from.Value, to.Value), $"{from.Source?.RawText} - {to.Source?.RawText}");
        }

        if (from.Status == FieldStatus.Missing && to.Status == FieldStatus.Missing)
        {
            return FieldValue<DatePeriod>.Missing();
        }

        return XmlFields.Unreadable<DatePeriod>($"{from.Source?.RawText ?? "?"} - {to.Source?.RawText ?? "?"}");
    }

    /// <summary>Document references that are present; absent elements contribute nothing rather than Missing entries.</summary>
    public static FieldValue<DocumentIdentifier>[] References(IEnumerable<XElement?> elements) =>
        elements.Select(XmlFields.Identifier).Where(f => f.HasValue).ToArray();
}
