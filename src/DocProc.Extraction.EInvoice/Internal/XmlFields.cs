using System.Globalization;
using System.Xml.Linq;
using DocProc.Domain.Common;
using DocProc.Domain.Fields;

namespace DocProc.Extraction.EInvoice.Internal;

/// <summary>
/// Turns XML text into provenance-carrying fields. Every present value is Extracted with
/// <see cref="SourceMethod.StructuredXml"/> and confidence 1.0 (docs/02-data-model.md §3); an absent element is
/// Missing; text that is present but does not parse is Unreadable with the raw text kept on the source.
/// </summary>
internal static class XmlFields
{
    /// <summary>Structured XML is read, not recognised, so its confidence is always 1.0.</summary>
    public const double Confidence = 1.0;

    private const string IsoDateFormat = "yyyy-MM-dd";
    private const string CiiDateFormat102 = "yyyyMMdd";
    private const string CiiFormat102 = "102";
    private const string CiiDateFormatAttribute = "format";
    private const string CiiDateTimeStringElement = "DateTimeString";
    private const string CurrencyAttribute = "currencyID";

    private const NumberStyles DecimalStyles =
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;

    public static FieldSource Source(string? rawText = null) =>
        rawText is null ? FieldSource.Xml : new FieldSource(SourceMethod.StructuredXml, RawText: rawText);

    public static FieldValue<T> Extracted<T>(T value, string? rawText = null) =>
        FieldValue.Extracted(value, Source(rawText), Confidence);

    public static FieldValue<T> Unreadable<T>(string rawText) => FieldValue<T>.Unreadable(Source(rawText));

    public static FieldValue<string> Text(XElement? element) => Text(element.Text());

    public static FieldValue<string> Text(string? text) =>
        text is null ? FieldValue<string>.Missing() : Extracted(text, text);

    public static FieldValue<DocumentIdentifier> Identifier(XElement? element)
    {
        var text = element.Text();
        return text is null ? FieldValue<DocumentIdentifier>.Missing() : Extracted(DocumentIdentifier.Create(text), text);
    }

    public static FieldValue<decimal> Number(XElement? element)
    {
        var text = element.Text();
        if (text is null)
        {
            return FieldValue<decimal>.Missing();
        }

        return TryNumber(text, out var value) ? Extracted(value, text) : Unreadable<decimal>(text);
    }

    public static bool TryNumber(string? text, out decimal value)
    {
        if (text is not null && decimal.TryParse(text, DecimalStyles, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0m;
        return false;
    }

    /// <summary>UBL dates: xsd:date, optionally with a time-zone suffix that is ignored.</summary>
    public static FieldValue<DateOnly> IsoDate(XElement? element)
    {
        var text = element.Text();
        if (text is null)
        {
            return FieldValue<DateOnly>.Missing();
        }

        return TryIsoDate(text, out var date) ? Extracted(date, text) : Unreadable<DateOnly>(text);
    }

    public static bool TryIsoDate(string? text, out DateOnly date)
    {
        if (text is null)
        {
            date = default;
            return false;
        }

        var core = text.Length > IsoDateFormat.Length && text[IsoDateFormat.Length] is '+' or '-' or 'Z'
            ? text[..IsoDateFormat.Length]
            : text;
        return DateOnly.TryParseExact(core, IsoDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>
    /// CII dates: a wrapper such as <c>IssueDateTime</c> holding <c>udt:DateTimeString</c> with <c>format="102"</c>
    /// (yyyyMMdd), the only format EN 16931 allows. Other formats are Unreadable, never reinterpreted.
    /// </summary>
    public static FieldValue<DateOnly> CiiDate(XElement? wrapper)
    {
        var dateTimeString = wrapper.Child(CiiDateTimeStringElement) ?? wrapper;
        var text = dateTimeString.Text();
        if (text is null)
        {
            return FieldValue<DateOnly>.Missing();
        }

        var format = dateTimeString.Attr(CiiDateFormatAttribute);
        return TryCiiDate(text, format, out var date) ? Extracted(date, text) : Unreadable<DateOnly>(text);
    }

    public static bool TryCiiDate(string? text, string? format, out DateOnly date)
    {
        date = default;
        if (text is null || (format is not null && format != CiiFormat102))
        {
            return false;
        }

        return DateOnly.TryParseExact(text, CiiDateFormat102, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>Either syntax's date as written in a free-text item attribute (best-before dates).</summary>
    public static bool TryAnyDate(string? text, out DateOnly date) =>
        TryIsoDate(text, out date) || TryCiiDate(text, format: null, out date);

    /// <summary>
    /// An amount element with its <c>currencyID</c> attribute; the document currency is the fallback. Without any
    /// currency the amount cannot become <see cref="Money"/> and is Unreadable rather than guessed.
    /// </summary>
    public static FieldValue<Money> Amount(XElement? element, string? documentCurrency)
    {
        var text = element.Text();
        if (text is null)
        {
            return FieldValue<Money>.Missing();
        }

        if (TryAmount(element, documentCurrency, out var money))
        {
            return Extracted(money, text);
        }

        return Unreadable<Money>(ResolveCurrency(element, documentCurrency) is null ? $"{text} (no currency)" : text);
    }

    public static bool TryAmount(XElement? element, string? documentCurrency, out Money money)
    {
        var currency = ResolveCurrency(element, documentCurrency);
        if (currency is not null && TryNumber(element.Text(), out var amount))
        {
            money = new Money(amount, currency);
            return true;
        }

        money = default;
        return false;
    }

    /// <summary>xsd:boolean indicators (ChargeIndicator): true/false/1/0, anything else is unknown.</summary>
    public static bool? Indicator(string? text) => text switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => null,
    };

    private static string? ResolveCurrency(XElement? element, string? documentCurrency)
    {
        var code = element.Attr(CurrencyAttribute) ?? documentCurrency;
        return code is null ? null : CurrencyCode.Normalize(code);
    }
}
