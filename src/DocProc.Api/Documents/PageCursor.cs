using System.Globalization;
using System.Text;

namespace DocProc.Api.Documents;

/// <summary>
/// Opaque keyset cursor for the list endpoints (docs/05 "cursor pagination"): base64 of <c>{timestamp:o}|{id}</c>,
/// the sort key of the last item of a page, with the priority rank (<c>|{rank}</c>) and the due time
/// (<c>|{due:o}</c>, <c>-</c> for none) for the lists sorted by review priority and due time. Opaque to clients, but
/// deliberately readable in a log.
/// </summary>
public readonly record struct PageCursor(DateTimeOffset At, Guid Id, int? Rank = null, DateTimeOffset? Due = null, bool HasDue = false)
{
    /// <summary>A cursor of a list sorted by priority rank, due time (none last), then <paramref name="at"/> and <paramref name="id"/>.</summary>
    public static PageCursor ByDue(DateTimeOffset at, Guid id, int rank, DateTimeOffset? due) => new(at, id, rank, due, HasDue: true);

    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public string Encode() =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            At.ToString("o", CultureInfo.InvariantCulture) + "|" + Id.ToString("D")
            + (Rank is { } rank ? "|" + rank.ToString(CultureInfo.InvariantCulture) : string.Empty)
            + (HasDue && Rank is not null ? "|" + (Due is { } due ? due.ToString("o", CultureInfo.InvariantCulture) : "-") : string.Empty)));

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

        var parts = Encoding.UTF8.GetString(buffer[..written]).Split('|');
        int? rank = null;
        DateTimeOffset? due = null;
        if (parts.Length is < 2 or > 4
            || !DateTimeOffset.TryParseExact(parts[0], "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            || !Guid.TryParseExact(parts[1], "D", out var id))
        {
            return false;
        }

        if (parts.Length >= 3)
        {
            if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed > 9)
            {
                return false;
            }

            rank = parsed;
        }

        if (parts.Length == 4 && parts[3] != "-")
        {
            if (!DateTimeOffset.TryParseExact(parts[3], "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedDue))
            {
                return false;
            }

            due = parsedDue;
        }

        cursor = new PageCursor(at, id, rank, due, HasDue: parts.Length == 4);
        return true;
    }

    /// <summary>Clamps a requested page size into [1, <see cref="MaxLimit"/>], defaulting to <see cref="DefaultLimit"/>.</summary>
    public static int ClampLimit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
}
