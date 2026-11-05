using System.Text.RegularExpressions;

namespace DocProc.Mapping.Values;

/// <summary>The order of day, month and year in a numeric date such as <c>03/11/2026</c>.</summary>
public enum DateOrder
{
    DayMonthYear,
    MonthDayYear,
    YearMonthDay,
}

/// <summary>
/// How printed numbers and dates are read (docs/11 §12.3 "parse candidates per kind, using the supplier locale"): the
/// decimal separator and the order of a numeric date. The parsers never use <see cref="System.Globalization.CultureInfo"/>
/// (the hosts run in invariant globalization mode), so the conventions are spelled out here. A printed number whose
/// separators decide it (<c>1.234,56</c>, <c>1,234.56</c>) is read the same in every locale; the locale settles only
/// the ambiguous ones (<c>1,250</c>, <c>03/11/2026</c>).
/// </summary>
public sealed partial record ValueLocale(string Name, char DecimalSeparator, DateOrder DateOrder)
{
    /// <summary>German conventions: <c>1.234,56</c> and <c>28.10.2026</c>.</summary>
    public static readonly ValueLocale German = new("de-DE", ',', DateOrder.DayMonthYear);

    /// <summary>US conventions: <c>1,234.56</c> and <c>10/28/2026</c>.</summary>
    public static readonly ValueLocale UnitedStates = new("en-US", '.', DateOrder.MonthDayYear);

    /// <summary>British conventions: <c>1,234.56</c> and <c>28/10/2026</c>.</summary>
    public static readonly ValueLocale British = new("en-GB", '.', DateOrder.DayMonthYear);

    /// <summary>Countries whose numbers use a decimal point; every other country is read with a decimal comma.</summary>
    private static readonly HashSet<string> DecimalPointCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        "US", "GB", "IE", "CH", "LI", "AU", "NZ", "CA", "IN", "SG", "HK", "JP", "CN", "KR", "MX", "IL", "MT", "PH", "ZA", "TH", "MY",
    };

    /// <summary>Countries that print the month first.</summary>
    private static readonly HashSet<string> MonthFirstCountries = new(StringComparer.OrdinalIgnoreCase) { "US", "PH" };

    /// <summary>Countries that print the year first.</summary>
    private static readonly HashSet<string> YearFirstCountries = new(StringComparer.OrdinalIgnoreCase) { "CN", "JP", "KR", "HU", "LT", "SE" };

    /// <summary>The conventions of a country (ISO 3166-1 alpha-2, the tenant's <c>CountryCode</c>); German conventions for an unknown or empty code.</summary>
    public static ValueLocale ForCountry(string? countryCode, string? language = null)
    {
        var country = countryCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(country) || country.Length != 2)
        {
            return German;
        }

        var separator = DecimalPointCountries.Contains(country) ? '.' : ',';
        var order = MonthFirstCountries.Contains(country) ? DateOrder.MonthDayYear
            : YearFirstCountries.Contains(country) ? DateOrder.YearMonthDay
            : DateOrder.DayMonthYear;
        var name = $"{(string.IsNullOrWhiteSpace(language) ? DefaultLanguage(country) : language.Trim().ToLowerInvariant())}-{country}";
        return new ValueLocale(name, separator, order);
    }

    /// <summary>
    /// A locale named like <c>de-DE</c>, <c>en-US</c> or <c>hr</c> (a bare language takes its usual country); false for
    /// anything else, so a request with an unreadable locale is refused instead of silently read another way.
    /// </summary>
    public static bool TryParse(string? name, out ValueLocale locale)
    {
        locale = German;
        var match = name is null ? null : LocalePattern().Match(name.Trim());
        if (match is not { Success: true })
        {
            return false;
        }

        var language = match.Groups["language"].Value.ToLowerInvariant();
        var country = match.Groups["country"].Success ? match.Groups["country"].Value.ToUpperInvariant() : DefaultCountry(language);
        locale = ForCountry(country, language);
        return true;
    }

    private static string DefaultCountry(string language) => language switch
    {
        "en" => "GB",
        "de" => "DE",
        "hr" => "HR",
        "fr" => "FR",
        "it" => "IT",
        "es" => "ES",
        "nl" => "NL",
        "pl" => "PL",
        "cs" => "CZ",
        "sl" => "SI",
        "sv" => "SE",
        "da" => "DK",
        "pt" => "PT",
        _ => language.ToUpperInvariant(),
    };

    private static string DefaultLanguage(string country) => country switch
    {
        "DE" or "AT" or "CH" or "LI" => "de",
        "US" or "GB" or "IE" or "AU" or "NZ" or "CA" => "en",
        "HR" => "hr",
        "FR" or "BE" or "LU" => "fr",
        "IT" => "it",
        "ES" => "es",
        "NL" => "nl",
        _ => country.ToLowerInvariant(),
    };

    [GeneratedRegex(@"^(?<language>[A-Za-z]{2})(?:[-_](?<country>[A-Za-z]{2}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalePattern();
}
