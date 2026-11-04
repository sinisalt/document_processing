using System.Globalization;
using System.Text;
using Jsonata.Net.Native;
using Jsonata.Net.Native.Eval;
using Jsonata.Net.Native.Json;

namespace DocProc.Outputs.Renderer;

/// <summary>A <c>$required(value, message)</c> that found no value: the render stops with a permanent error (docs/11 §13.1).</summary>
public sealed class RequiredValueException(string message) : Exception(message);

/// <summary>A custom function used wrongly (an unknown lookup table, a date that is not one): a permanent render error.</summary>
public sealed class RenderFunctionException(string message) : Exception(message);

/// <summary>One lookup entry as the host sends it (docs/11 §6.1 <c>LookupEntry</c>).</summary>
internal sealed record LookupEntryData(string From, string To, DateOnly? ValidFrom, DateOnly? ValidTo);

/// <summary>One lookup table as the host sends it.</summary>
internal sealed class LookupTableData
{
    private readonly Dictionary<string, List<LookupEntryData>> _entries;

    /// <summary>The entries whose <c>from</c> is a number, by its canonical text (<c>10.0</c> and <c>10</c> are both <c>10</c>).</summary>
    private readonly Dictionary<string, List<LookupEntryData>> _numbers = new(StringComparer.Ordinal);

    public LookupTableData(bool caseSensitive, IEnumerable<LookupEntryData> entries)
    {
        _entries = new Dictionary<string, List<LookupEntryData>>(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var key = entry.From.Trim();
            Add(_entries, key, entry);
            if (RenderFunctions.CanonicalNumber(key) is { } number)
            {
                Add(_numbers, number, entry);
            }
        }
    }

    /// <summary>
    /// The value an entry maps <paramref name="from"/> to on <paramref name="asOf"/>: of the entries valid that day, the
    /// one valid from the latest date. A number (<paramref name="number"/>) that matches no entry's text exactly matches
    /// an entry of the same value: the values view keeps a number's text (<c>10.0</c>), the table may say <c>10</c>.
    /// </summary>
    public string? Resolve(string from, DateOnly asOf, bool number = false)
    {
        if (_entries.TryGetValue(from.Trim(), out var list) && Valid(list, asOf) is { } exact)
        {
            return exact;
        }

        return number && RenderFunctions.CanonicalNumber(from.Trim()) is { } canonical && _numbers.TryGetValue(canonical, out var same)
            ? Valid(same, asOf)
            : null;
    }

    private static string? Valid(List<LookupEntryData> list, DateOnly asOf) =>
        list.Where(e => (e.ValidFrom is null || e.ValidFrom <= asOf) && (e.ValidTo is null || e.ValidTo >= asOf))
            .OrderByDescending(e => e.ValidFrom ?? DateOnly.MinValue)
            .Select(e => e.To)
            .FirstOrDefault();

    private static void Add(Dictionary<string, List<LookupEntryData>> index, string key, LookupEntryData entry)
    {
        if (!index.TryGetValue(key, out var list))
        {
            index[key] = list = [];
        }

        list.Add(entry);
    }
}

/// <summary>What one render's custom functions read: the date, the lookup tables and the field envelopes.</summary>
internal sealed class RenderContext(DateOnly today, IReadOnlyDictionary<string, LookupTableData> lookups, JObject? fields)
{
    public DateOnly Today { get; } = today;

    public IReadOnlyDictionary<string, LookupTableData> Lookups { get; } = lookups;

    public JObject? Fields { get; } = fields;
}

/// <summary>
/// The custom functions of docs/11 §13.1, bound into every render. The renderer runs one render at a time on one
/// thread, so the context of the current render is a thread-static field the functions read.
/// </summary>
public static class RenderFunctions
{
    /// <summary>The longest date pattern <c>$fmtDate</c> takes.</summary>
    public const int MaxPatternLength = 64;

    /// <summary>The most decimals <c>$fmtNum</c> takes.</summary>
    public const int MaxDecimals = 10;

    [ThreadStatic]
    private static RenderContext? _current;

    internal static RenderContext Current => _current ?? throw new InvalidOperationException("No render is running.");

    internal static IDisposable Use(RenderContext context)
    {
        _current = context;
        return new Scope();
    }

    /// <summary>The environment of one render: the custom functions and the depth caps.</summary>
    public static EvaluationEnvironment CreateEnvironment(int maxDepth, int maxTailRecursion)
    {
        var environment = new EvaluationEnvironment { MaxDepth = maxDepth, MaxTailRecursionIteration = maxTailRecursion };
        foreach (var name in new[] { "table", "fmtDate", "fmtNum", "today", "field", "required" })
        {
            environment.BindFunction(name, typeof(RenderFunctions).GetMethod(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!, null!);
        }

        return environment;
    }

    /// <summary><c>$table(key, value, default?)</c>: the lookup table's entry for the value, valid today; the default (or nothing) when none is.</summary>
#pragma warning disable IDE1006, CA1707 // JSONata binds the function by the method's name.
    public static JToken table(JToken key, JToken value, [OptionalArgument(null)] JToken? fallback)
    {
        var name = TextOf(key) ?? throw new RenderFunctionException("$table: the first argument is the lookup table's key, a string.");
        if (!Current.Lookups.TryGetValue(name, out var lookup))
        {
            throw new RenderFunctionException($"$table: there is no lookup table '{name}' (the key must be written as a string literal in the expression).");
        }

        var from = TextOf(value);
        if (from is not null && lookup.Resolve(from, Current.Today, number: value.Type is JTokenType.Integer or JTokenType.Float) is { } to)
        {
            return new JValue(to);
        }

        return fallback is null || fallback.Type == JTokenType.Undefined ? JValue.CreateUndefined() : fallback;
    }

    /// <summary>
    /// The ISO 8601 date-times <c>$fmtDate</c> reads: <c>T</c> between date and time, seconds and fractions optional,
    /// an offset or <c>Z</c> optional (none is read as UTC).
    /// </summary>
    private static readonly string[] IsoDateTimes =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mmzzz",
        "yyyy-MM-dd'T'HH:mm'Z'",
        "yyyy-MM-dd'T'HH:mm",
    ];

    /// <summary>
    /// <c>$fmtDate(date, pattern)</c>: a <c>yyyy-MM-dd</c> date or an ISO 8601 date-time in a .NET custom pattern
    /// (invariant culture). A one-character pattern is a custom one too (<c>"d"</c> is the day, not .NET's short date).
    /// </summary>
    public static JToken fmtDate(JToken date, JToken pattern)
    {
        var text = TextOf(date);
        if (text is null)
        {
            return JValue.CreateUndefined();
        }

        var format = TextOf(pattern) ?? throw new RenderFunctionException("$fmtDate: the second argument is the pattern, a string such as \"dd.MM.yyyy\".");
        if (format.Length > MaxPatternLength)
        {
            throw new RenderFunctionException($"$fmtDate: the pattern has more than {MaxPatternLength} characters.");
        }

        // .NET reads a single character as a standard format ("d" the short date, "M" month and day); "%d" is the custom one.
        if (format.Length == 1)
        {
            format = "%" + format;
        }

        try
        {
            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                return new JValue(day.ToDateTime(TimeOnly.MinValue).ToString(format, CultureInfo.InvariantCulture));
            }

            if (DateTimeOffset.TryParseExact(text, IsoDateTimes, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment))
            {
                return new JValue(moment.ToString(format, CultureInfo.InvariantCulture));
            }
        }
        catch (FormatException)
        {
            throw new RenderFunctionException($"$fmtDate: '{format}' is not a date pattern.");
        }

        throw new RenderFunctionException($"$fmtDate: '{Shorten(text)}' is not a date (yyyy-MM-dd) or an ISO date-time.");
    }

    /// <summary><c>$fmtNum(n, decimals, decSep?, groupSep?)</c>: the number rounded half away from zero, with the separators given (default <c>.</c> and none).</summary>
    public static JToken fmtNum(JToken number, JToken decimals, [OptionalArgument(null)] JToken? decimalSeparator, [OptionalArgument(null)] JToken? groupSeparator)
    {
        if (number.Type is JTokenType.Undefined or JTokenType.Null)
        {
            return JValue.CreateUndefined();
        }

        if (number.Type is not (JTokenType.Integer or JTokenType.Float))
        {
            throw new RenderFunctionException("$fmtNum: the first argument is a number.");
        }

        if (DecimalOf(decimals) is not { } places || places < 0 || places > MaxDecimals || places != Math.Floor(places))
        {
            throw new RenderFunctionException($"$fmtNum: the decimals are a whole number from 0 to {MaxDecimals}.");
        }

        var value = DecimalOf(number) ?? throw new RenderFunctionException("$fmtNum: the number is too large to format.");
        var count = (int)places;
        var formatted = Math.Round(value, count, MidpointRounding.AwayFromZero).ToString("N" + count.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var dec = TextOf(decimalSeparator ?? JValue.CreateUndefined()) ?? ".";
        var group = TextOf(groupSeparator ?? JValue.CreateUndefined()) ?? string.Empty;

        // The text must read back as the same number: with decimals, a decimal separator that is missing or equal to the
        // group separator gives another number (or an ambiguous one), and a separator with a digit or a sign does at any
        // count. Without decimals the decimal separator is never written, so "1.234" from (n, 0, ".", ".") is fine.
        if (count > 0 && dec.Length == 0)
        {
            throw new RenderFunctionException("$fmtNum: with decimals the decimal separator is not empty.");
        }

        if (count > 0 && dec == group)
        {
            throw new RenderFunctionException("$fmtNum: the decimal and the group separator differ.");
        }

        if (dec.Any(IsNumberCharacter) || group.Any(IsNumberCharacter))
        {
            throw new RenderFunctionException("$fmtNum: a separator has no digit and no sign.");
        }

        var result = new StringBuilder(formatted.Length + 8);
        foreach (var c in formatted)
        {
            result.Append(c switch
            {
                ',' => group,
                '.' => dec,
                _ => c.ToString(),
            });
        }

        return new JValue(result.ToString());
    }

    /// <summary>A character that is part of a number's text, so not a separator of <c>$fmtNum</c>: a digit or a sign.</summary>
    private static bool IsNumberCharacter(char c) => char.IsDigit(c) || c is '-' or '+' or '\u2212';

    /// <summary><c>$today()</c>: today (<c>yyyy-MM-dd</c>) in the tenant's time zone.</summary>
    public static JToken today() => new JValue(Current.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary><c>$field(path)</c>: <c>{status, confidence, method, normalized}</c> of the field at a path such as <c>lines[0].price</c>; nothing for a path the document has no field at.</summary>
    public static JToken field(JToken path)
    {
        var text = TextOf(path) ?? throw new RenderFunctionException("$field: the argument is a field path such as \"lines[0].price\".");
        if (Current.Fields is null)
        {
            return JValue.CreateUndefined();
        }

        return Current.Fields.Properties.TryGetValue(text, out var envelope) ? envelope : JValue.CreateUndefined();
    }

    /// <summary><c>$required(value, message)</c>: the value, or a permanent error with the message when there is none (missing, null, empty text or an empty list).</summary>
    public static JToken required(JToken value, [OptionalArgument(null)] JToken? message)
    {
        var missing = value.Type switch
        {
            JTokenType.Undefined or JTokenType.Null => true,
            JTokenType.String => string.IsNullOrWhiteSpace((string)value),
            JTokenType.Array => ((JArray)value).Count == 0,
            _ => false,
        };
        if (missing)
        {
            throw new RequiredValueException(TextOf(message ?? JValue.CreateUndefined()) is { Length: > 0 } text ? Shorten(text, 500) : "A required value is missing.");
        }

        return value;
    }
#pragma warning restore IDE1006, CA1707

    /// <summary>A value as text for a lookup or a pattern: strings as they are, numbers in invariant form, anything else none.</summary>
    private static string? TextOf(JToken token) => token.Type switch
    {
        JTokenType.String => (string)token,
        JTokenType.Integer or JTokenType.Float => DecimalOf(token)?.ToString(CultureInfo.InvariantCulture) ?? token.ToFlatString(),
        JTokenType.Boolean => (bool)token ? "true" : "false",
        _ => null,
    };

    /// <summary>A number token as a decimal (its JSON text read in invariant form), or null when it is none or out of range.</summary>
    private static decimal? DecimalOf(JToken token) =>
        token.Type is JTokenType.Integer or JTokenType.Float
        && decimal.TryParse(token.ToFlatString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>
    /// A number's canonical text (invariant, no exponent, no trailing zeros: <c>10.0</c> and <c>1E1</c> are <c>10</c>),
    /// or null when <paramref name="text"/> is not a number.
    /// </summary>
    internal static string? CanonicalNumber(string text) =>
        text.Length is > 0 and <= 64
        && (char.IsAsciiDigit(text[0]) || text[0] is '-' or '+' or '.')
        && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? (value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture)
            : null;

    private static string Shorten(string text, int max = 80) => text.Length <= max ? text : text[..max] + "…";

    private sealed class Scope : IDisposable
    {
        public void Dispose() => _current = null;
    }
}
