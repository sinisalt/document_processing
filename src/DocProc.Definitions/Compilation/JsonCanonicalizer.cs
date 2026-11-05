using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocProc.Definitions.Compilation;

/// <summary>
/// The JSON Canonicalization Scheme (RFC 8785) for the content hash of a definition version (docs/11 §6.1,
/// <c>ContentHash</c>): object members sorted by their UTF-16 code units, no whitespace, strings with the minimal
/// escaping of RFC 8785 and numbers in the ECMAScript shortest round-trip form. Two definitions that differ only in
/// formatting, member order or number spelling (<c>1.0</c> and <c>1</c>) hash alike.
/// </summary>
public static class JsonCanonicalizer
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    public static string Canonicalize(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(node, builder);
        return builder.ToString();
    }

    /// <summary>The lower-case hex SHA-256 of the canonical form of <paramref name="node"/>.</summary>
    public static string Sha256Hex(JsonNode? node) => Sha256Hex(Canonicalize(node));

    /// <summary>The lower-case hex SHA-256 of the UTF-8 bytes of <paramref name="text"/>.</summary>
    public static string Sha256Hex(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static void Write(JsonNode? node, StringBuilder builder)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    WriteString(key, builder);
                    builder.Append(':');
                    Write(value, builder);
                }

                builder.Append('}');
                break;
            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    Write(array[i], builder);
                }

                builder.Append(']');
                break;
            case JsonValue value:
                WriteValue(value, builder);
                break;
            default:
                throw new ArgumentException($"Unexpected JSON node {node.GetType().Name}.", nameof(node));
        }
    }

    private static void WriteValue(JsonValue value, StringBuilder builder)
    {
        var element = value.GetValueKind() switch
        {
            JsonValueKind.String => (JsonValueKind?)JsonValueKind.String,
            JsonValueKind.Number => JsonValueKind.Number,
            JsonValueKind.True => JsonValueKind.True,
            JsonValueKind.False => JsonValueKind.False,
            JsonValueKind.Null => JsonValueKind.Null,
            _ => null,
        };
        switch (element)
        {
            case JsonValueKind.String:
                WriteString(value.GetValue<string>(), builder);
                break;
            case JsonValueKind.Number:
                builder.Append(FormatNumber(double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)));
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            default:
                builder.Append("null");
                break;
        }
    }

    private static void WriteString(string value, StringBuilder builder)
    {
        // RFC 8785 §3.2.2.2: escape only the quote, the backslash and control characters (short forms where they
        // exist); everything else, including non-ASCII, is written as it is.
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    /// <summary>ECMAScript <c>Number.prototype.toString</c> for a finite double (RFC 8785 §3.2.2.3).</summary>
    public static string FormatNumber(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException("JSON numbers are finite.", nameof(value));
        }

        if (value == 0)
        {
            return "0";
        }

        // The shortest round-trip digits and the decimal exponent, then ECMAScript's placement rules.
        var shortest = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = value < 0;
        var digitsAndExponent = Decompose(shortest.TrimStart('-'));
        var digits = digitsAndExponent.Digits;
        var n = digitsAndExponent.PointPosition; // value = 0.digits * 10^n
        var k = digits.Length;
        var sb = new StringBuilder();
        if (negative)
        {
            sb.Append('-');
        }

        if (k <= n && n <= 21)
        {
            sb.Append(digits).Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            sb.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            sb.Append("0.").Append('0', -n).Append(digits);
        }
        else
        {
            var exponent = n - 1;
            sb.Append(digits[0]);
            if (k > 1)
            {
                sb.Append('.').Append(digits, 1, k - 1);
            }

            sb.Append('e').Append(exponent >= 0 ? "+" : "-").Append(Math.Abs(exponent).ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>The significant digits (no leading or trailing zeros) and n, so that the value is 0.digits × 10^n.</summary>
    private static (string Digits, int PointPosition) Decompose(string shortest)
    {
        var exponent = 0;
        var mantissa = shortest;
        var e = shortest.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(shortest[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            mantissa = shortest[..e];
        }

        var point = mantissa.IndexOf('.', StringComparison.Ordinal);
        var integerPart = point >= 0 ? mantissa[..point] : mantissa;
        var fraction = point >= 0 ? mantissa[(point + 1)..] : string.Empty;
        var all = integerPart + fraction;
        var position = integerPart.Length + exponent;
        var leading = 0;
        while (leading < all.Length - 1 && all[leading] == '0')
        {
            leading++;
        }

        all = all[leading..];
        position -= leading;
        all = all.TrimEnd('0');
        return (all.Length == 0 ? "0" : all, position);
    }

    /// <summary>The compact JSON of <paramref name="node"/> as System.Text.Json writes it (not canonical; for storage).</summary>
    public static string Compact(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            node.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
