using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocProc.Mapping.Learning;

/// <summary>
/// How a value a layout read is compared with the confirmed value of a reviewed or approved document (the learner's
/// self-check and the field replay of docs/11 §8.3): text without whitespace and case, numbers by value (<c>19</c> and
/// <c>19.00</c> agree), an amount by its amount, an identifier as printed (else by its normalised form), a date as
/// stored. Two values agree when their comparable forms are equal.
/// </summary>
public static class ValueComparison
{
    public static string? Comparable(JsonNode? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonObject o when o["amount"] is { } amount:
                return Comparable(amount);
            case JsonObject o when o["normalized"] is JsonValue || o["verbatim"] is JsonValue:
                return Comparable(o["verbatim"] is JsonValue verbatim && verbatim.GetValueKind() == JsonValueKind.String ? verbatim : o["normalized"]);
            case JsonObject:
            case JsonArray:
                return value.ToJsonString();
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.Number:
                        return v.TryGetValue<decimal>(out var d) ? Number(d) : v.ToJsonString();
                    case JsonValueKind.String:
                        var s = v.GetValue<string>();
                        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && s.Any(char.IsDigit) && !s.Any(char.IsLetter) && s.Count(c => c == '.') <= 1 && !s.Contains(',', StringComparison.Ordinal)
                            ? Number(parsed)
                            : Text(s);
                    case JsonValueKind.True:
                        return "true";
                    case JsonValueKind.False:
                        return "false";
                    default:
                        return null;
                }

            default:
                return value.ToJsonString();
        }
    }

    public static bool Agree(JsonNode? expected, JsonNode? actual) =>
        string.Equals(Comparable(expected), Comparable(actual), StringComparison.Ordinal);

    private static string Number(decimal d) => (d / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    private static string Text(string s)
    {
        var builder = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }
}
