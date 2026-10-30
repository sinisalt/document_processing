using System.Globalization;
using System.Text;

namespace DocProc.Api.Documents;

/// <summary>
/// Opaque keyset cursor for the list endpoints (docs/05 "cursor pagination"): base64 of <c>{timestamp:o}|{id}</c>,
/// the sort key of the last item of a page. Opaque to clients, but deliberately readable in a log.
/// </summary>
public readonly record struct PageCursor(DateTimeOffset At, Guid Id)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public string Encode() =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(At.ToString("o", CultureInfo.InvariantCulture) + "|" + Id.ToString("D")));

    public static bool TryDecode(string? value, out PageCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[128];
        if (!Convert.TryFromBase64String(value.Trim(), buffer, out var written))
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(buffer[..written]);
        var separator = text.IndexOf('|', StringComparison.Ordinal);
        if (separator <= 0
            || !DateTimeOffset.TryParseExact(text[..separator], "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            || !Guid.TryParseExact(text[(separator + 1)..], "D", out var id))
        {
            return false;
        }

        cursor = new PageCursor(at, id);
        return true;
    }

    /// <summary>Clamps a requested page size into [1, <see cref="MaxLimit"/>], defaulting to <see cref="DefaultLimit"/>.</summary>
    public static int ClampLimit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
}
