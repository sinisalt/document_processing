using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocProc.Definitions;

namespace DocProc.Mapping.Values;

/// <summary>A printed text read as a value of a field kind: the value in the shape a correction takes, or why it is not one.</summary>
public sealed record ParsedValue(JsonNode? Value, string? Error)
{
    public bool Ok => Error is null;

    public static ParsedValue Of(JsonNode? value) => new(value, null);

    public static ParsedValue Fail(string error) => new(null, error);
}

/// <summary>A reading of a selection as one kind (docs/11 §12.3: "28.10.2026 → 2026-10-28").</summary>
public sealed record ValueCandidate(string Kind, JsonNode? Value);

/// <summary>
/// Reads printed text as field values (docs/11 §12.3 map mode and §12.4 table tool). The value is in the shape
/// <c>PATCH …/fields</c> takes for the kind: an identifier or a string as its text, a number for a decimal, an integer
/// or a percentage, <c>yyyy-MM-dd</c> for a date, an amount as a number (or <c>{amount, currency}</c> when a currency is
/// printed with it), a code for a currency or an enum, true or false for a yes/no. Deterministic and culture-free.
/// </summary>
public static partial class PrintedValues
{
    /// <summary>The kinds <see cref="Candidates"/> tries, in the order a preview offers them.</summary>
    public static readonly IReadOnlyList<string> CandidateKinds =
        [FieldKinds.Date, FieldKinds.Decimal, FieldKinds.Integer, FieldKinds.Money, FieldKinds.Percent, FieldKinds.Currency, FieldKinds.Identifier, FieldKinds.Text];

    /// <summary>Currency symbols and the codes they stand for.</summary>
    private static readonly (string Symbol, string Code)[] CurrencySymbols =
    [
        ("€", "EUR"),
        ("$", "USD"),
        ("£", "GBP"),
        ("¥", "JPY"),
        ("kn", "HRK"),
        ("Fr.", "CHF"),
        ("zł", "PLN"),
        ("Kč", "CZK"),
    ];

    private static readonly FrozenDictionary<string, int> MonthNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["januar"] = 1, ["jänner"] = 1, ["january"] = 1, ["jan"] = 1,
        ["februar"] = 2, ["february"] = 2, ["feb"] = 2,
        ["märz"] = 3, ["maerz"] = 3, ["march"] = 3, ["mär"] = 3, ["mar"] = 3,
        ["april"] = 4, ["apr"] = 4,
        ["mai"] = 5, ["may"] = 5,
        ["juni"] = 6, ["june"] = 6, ["jun"] = 6,
        ["juli"] = 7, ["july"] = 7, ["jul"] = 7,
        ["august"] = 8, ["aug"] = 8,
        ["september"] = 9, ["sept"] = 9, ["sep"] = 9,
        ["oktober"] = 10, ["october"] = 10, ["okt"] = 10, ["oct"] = 10,
        ["november"] = 11, ["nov"] = 11,
        ["dezember"] = 12, ["december"] = 12, ["dez"] = 12, ["dec"] = 12,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every reading of <paramref name="text"/> among <see cref="CandidateKinds"/>, in that order; identifier and text
    /// always. An amount is offered as money only when a currency is printed with it, and a percentage only with its
    /// sign (a bare number is the decimal reading, which amount and percentage fields take as well).
    /// </summary>
    public static IReadOnlyList<ValueCandidate> Candidates(string? text, ValueLocale locale)
    {
        ArgumentNullException.ThrowIfNull(locale);
        var candidates = new List<ValueCandidate>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return candidates;
        }

        foreach (var kind in CandidateKinds)
        {
            var parsed = Parse(kind, text, locale);
            var offered = kind switch
            {
                FieldKinds.Money => parsed.Value is JsonObject,
                FieldKinds.Percent => text.TrimEnd().EndsWith('%'),
                _ => true,
            };
            if (parsed.Ok && parsed.Value is not null && offered)
            {
                candidates.Add(new ValueCandidate(kind, parsed.Value));
            }
        }

        return candidates;
    }

    /// <summary>
    /// <paramref name="text"/> read as a value of <paramref name="kind"/>. Blank text is no value (<see cref="ParsedValue.Value"/>
    /// null, no error). <paramref name="options"/> are an enum's codes; <paramref name="pattern"/> a field's pattern the
    /// text (an identifier's or string's) must match.
    /// </summary>
    public static ParsedValue Parse(string kind, string? text, ValueLocale locale, IReadOnlyList<string>? options = null, Regex? pattern = null)
    {
        ArgumentNullException.ThrowIfNull(locale);
        var trimmed = Collapse(text);
        if (trimmed.Length == 0)
        {
            return ParsedValue.Of(null);
        }

        switch (kind)
        {
            case FieldKinds.Identifier or FieldKinds.String or FieldKinds.Text or FieldKinds.Unit:
                if (pattern is not null && !Matches(pattern, trimmed))
                {
                    return ParsedValue.Fail($"'{trimmed}' does not match the field's pattern.");
                }

                return ParsedValue.Of(JsonValue.Create(trimmed));

            case FieldKinds.Decimal:
                return TryDecimal(trimmed, locale, out var number) ? ParsedValue.Of(JsonValue.Create(number)) : ParsedValue.Fail($"'{trimmed}' is not a number.");

            case FieldKinds.Integer:
                return TryDecimal(trimmed, locale, out var whole) && whole == decimal.Truncate(whole)
                    ? ParsedValue.Of(JsonValue.Create(decimal.ToInt64(whole)))
                    : ParsedValue.Fail($"'{trimmed}' is not a whole number.");

            case FieldKinds.Percent:
                var bare = trimmed.EndsWith('%') ? trimmed[..^1] : trimmed;
                return TryDecimal(bare, locale, out var percent) ? ParsedValue.Of(JsonValue.Create(percent)) : ParsedValue.Fail($"'{trimmed}' is not a percentage.");

            case FieldKinds.Money:
                return TryMoney(trimmed, locale, out var amount, out var currency)
                    ? ParsedValue.Of(currency is null ? JsonValue.Create(amount) : new JsonObject { ["amount"] = amount, ["currency"] = currency })
                    : ParsedValue.Fail($"'{trimmed}' is not an amount.");

            case FieldKinds.Currency:
                return TryCurrency(trimmed, out var code) ? ParsedValue.Of(JsonValue.Create(code)) : ParsedValue.Fail($"'{trimmed}' is not a currency.");

            case FieldKinds.Date:
                return TryDate(trimmed, locale, out var date)
                    ? ParsedValue.Of(JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                    : ParsedValue.Fail($"'{trimmed}' is not a date.");

            case FieldKinds.Boolean:
                return trimmed.ToLowerInvariant() switch
                {
                    "ja" or "yes" or "true" or "x" or "✓" or "✔" or "da" or "oui" or "si" or "sì" => ParsedValue.Of(JsonValue.Create(true)),
                    "nein" or "no" or "false" or "-" or "ne" or "non" => ParsedValue.Of(JsonValue.Create(false)),
                    _ => ParsedValue.Fail($"'{trimmed}' is not yes or no."),
                };

            case FieldKinds.Enum:
                var option = options?.FirstOrDefault(o => string.Equals(o, trimmed, StringComparison.OrdinalIgnoreCase));
                return option is not null || options is not { Count: > 0 }
                    ? ParsedValue.Of(JsonValue.Create(option ?? trimmed))
                    : ParsedValue.Fail($"'{trimmed}' is not one of {string.Join(", ", options)}.");

            default:
                return ParsedValue.Fail($"A {kind} value is not read from printed text.");
        }
    }

    private static bool Matches(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// A printed number: grouping separators (<c>.</c>, <c>,</c>, apostrophes, spaces) dropped, the decimal separator read
    /// from the text where it decides (the last of two different separators; a separator that occurs more than once
    /// groups) and from <paramref name="locale"/> where it does not (<c>1,250</c>: 1.25 with a decimal comma, 1250 with
    /// a decimal point, unless the digits after it make it a decimal: <c>12,99</c> is 12.99 everywhere). A leading or
    /// trailing minus, or a trailing <c>-</c> as on German credit lists, makes it negative.
    /// </summary>
    public static bool TryDecimal(string? text, ValueLocale locale, out decimal value)
    {
        ArgumentNullException.ThrowIfNull(locale);
        value = 0;
        var s = (text ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("'", string.Empty, StringComparison.Ordinal)
            .Replace("’", string.Empty, StringComparison.Ordinal)
            .Replace('−', '-');
        if (s.Length == 0)
        {
            return false;
        }

        var negative = false;
        if (s.StartsWith('-') || s.StartsWith('+'))
        {
            negative = s[0] == '-';
            s = s[1..];
        }
        else if (s.EndsWith('-'))
        {
            negative = true;
            s = s[..^1];
        }

        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            negative = true;
            s = s[1..^1];
        }

        if (s.Length == 0 || !NumberShape().IsMatch(s))
        {
            return false;
        }

        var lastDot = s.LastIndexOf('.');
        var lastComma = s.LastIndexOf(',');
        char? decimalSeparator;
        if (lastDot >= 0 && lastComma >= 0)
        {
            decimalSeparator = lastDot > lastComma ? '.' : ',';
        }
        else if (lastDot < 0 && lastComma < 0)
        {
            decimalSeparator = null;
        }
        else
        {
            var separator = lastDot >= 0 ? '.' : ',';
            var count = s.Count(c => c == separator);
            var digitsAfter = s.Length - s.LastIndexOf(separator) - 1;
            decimalSeparator = count > 1 ? null
                : separator == locale.DecimalSeparator ? separator
                : digitsAfter == 3 ? null
                : separator;
        }

        // Grouping separators group three digits each after the first group of one to three: "28.10.2026" is no number.
        var integerPart = decimalSeparator is { } ds && s.LastIndexOf(ds) is var at and >= 0 ? s[..at] : s;
        var groups = integerPart.Split('.', ',');
        if (groups.Length > 1 && (groups[0].Length is 0 or > 3 || groups.Skip(1).Any(g => g.Length != 3)))
        {
            return false;
        }

        var builder = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsAsciiDigit(c))
            {
                builder.Append(c);
            }
            else if (c == decimalSeparator)
            {
                builder.Append('.');
            }
        }

        if (!decimal.TryParse(builder.ToString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (negative)
        {
            value = -value;
        }

        return true;
    }

    /// <summary>An amount with an optional currency symbol or code before or after it (<c>12,99 €</c>, <c>EUR 1.234,50</c>).</summary>
    public static bool TryMoney(string? text, ValueLocale locale, out decimal amount, out string? currency)
    {
        amount = 0;
        currency = null;
        var s = Collapse(text);
        foreach (var (symbol, code) in CurrencySymbols)
        {
            if (s.Contains(symbol, StringComparison.Ordinal))
            {
                currency = code;
                s = s.Replace(symbol, " ", StringComparison.Ordinal);
                break;
            }
        }

        if (currency is null && CurrencyCodeInText().Match(s) is { Success: true } match)
        {
            currency = match.Value.ToUpperInvariant();
            s = s.Remove(match.Index, match.Length);
        }

        return TryDecimal(s.Trim(), locale, out amount);
    }

    /// <summary>A three-letter code (<c>eur</c> reads as EUR) or a symbol (€).</summary>
    public static bool TryCurrency(string? text, out string code)
    {
        var s = Collapse(text);
        foreach (var (symbol, symbolCode) in CurrencySymbols)
        {
            if (string.Equals(s, symbol, StringComparison.Ordinal))
            {
                code = symbolCode;
                return true;
            }
        }

        code = s.ToUpperInvariant();
        return code.Length == 3 && code.All(c => c is >= 'A' and <= 'Z');
    }

    /// <summary>
    /// A printed date: ISO (<c>2026-10-28</c>), numeric with dots, slashes or dashes in the locale's order (two-digit
    /// years are 20yy), or with a German or English month name (<c>28. Oktober 2026</c>, <c>October 28, 2026</c>).
    /// </summary>
    public static bool TryDate(string? text, ValueLocale locale, out DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(locale);
        date = default;
        var s = Collapse(text).TrimEnd('.', ',');
        if (IsoDate().Match(s) is { Success: true } iso)
        {
            return Make(Int(iso, "y"), Int(iso, "m"), Int(iso, "d"), out date);
        }

        if (NumericDate().Match(s) is { Success: true } numeric)
        {
            var a = Int(numeric, "a");
            var b = Int(numeric, "b");
            var year = Year(Int(numeric, "c"), numeric.Groups["c"].Value.Length);
            var dots = numeric.Groups["sep"].Value == ".";
            // A dotted date is day first in every convention that uses dots.
            return dots || locale.DateOrder != DateOrder.MonthDayYear ? Make(year, b, a, out date) : Make(year, a, b, out date);
        }

        if (DayMonthName().Match(s) is { Success: true } dayFirst && MonthNames.TryGetValue(dayFirst.Groups["month"].Value.TrimEnd('.'), out var month))
        {
            return Make(Year(Int(dayFirst, "y"), dayFirst.Groups["y"].Value.Length), month, Int(dayFirst, "d"), out date);
        }

        if (MonthNameDay().Match(s) is { Success: true } monthFirst && MonthNames.TryGetValue(monthFirst.Groups["month"].Value.TrimEnd('.'), out var month2))
        {
            return Make(Year(Int(monthFirst, "y"), monthFirst.Groups["y"].Value.Length), month2, Int(monthFirst, "d"), out date);
        }

        return false;
    }

    private static int Int(Match match, string group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    private static int Year(int year, int digits) => digits == 2 ? 2000 + year : year;

    private static bool Make(int year, int month, int day, out DateOnly date)
    {
        date = default;
        if (year is < 1900 or > 2200 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = new DateOnly(year, month, day);
        return true;
    }

    /// <summary>Whitespace runs as one space, trimmed.</summary>
    public static string Collapse(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : Whitespace().Replace(text.Trim(), " ");

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^[0-9.,]*[0-9][0-9.,]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberShape();

    [GeneratedRegex(@"(?<![A-Za-z])(EUR|USD|GBP|CHF|HRK|PLN|CZK|SEK|NOK|DKK|HUF|RON|BGN|JPY)(?![A-Za-z])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CurrencyCodeInText();

    [GeneratedRegex(@"^(?<y>\d{4})-(?<m>\d{1,2})-(?<d>\d{1,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"^(?<a>\d{1,2})(?<sep>[./-])(?<b>\d{1,2})\k<sep>(?<c>\d{4}|\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericDate();

    [GeneratedRegex(@"^(?<d>\d{1,2})\.?\s+(?<month>[A-Za-zÄÖÜäöü]{3,9}\.?)\s+(?<y>\d{4}|\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex DayMonthName();

    [GeneratedRegex(@"^(?<month>[A-Za-z]{3,9}\.?)\s+(?<d>\d{1,2}),?\s+(?<y>\d{4})$", RegexOptions.CultureInvariant)]
    private static partial Regex MonthNameDay();
}
