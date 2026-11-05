using System.Globalization;

namespace DocProc.Infrastructure.MasterData;

/// <summary>
/// A five-field cron expression (minute, hour, day of month, month, day of week; <c>*</c>, numbers, ranges <c>a-b</c>,
/// lists <c>a,b</c> and steps <c>*/n</c>, <c>a-b/n</c>; day of week 0-7 with 0 and 7 Sunday), in UTC or, after a
/// <c>CRON_TZ=&lt;IANA zone&gt; </c> prefix, in that zone, as docs/11 §6.8 writes master-data schedules. When both day
/// fields are restricted a day matches either, as in classic cron. <see cref="Next"/> finds the next minute within a
/// year, or null.
/// </summary>
public sealed class CronSchedule
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _days = new bool[32];
    private readonly bool[] _months = new bool[13];
    private readonly bool[] _weekdays = new bool[7];
    private readonly bool _anyDay;
    private readonly bool _anyWeekday;

    private CronSchedule(TimeZoneInfo zone, string[] fields)
    {
        Zone = zone;
        Fill(_minutes, fields[0], 0, 59, "minute");
        Fill(_hours, fields[1], 0, 23, "hour");
        Fill(_days, fields[2], 1, 31, "day of month");
        Fill(_months, fields[3], 1, 12, "month");
        var weekdays = new bool[8];
        Fill(weekdays, fields[4], 0, 7, "day of week");
        for (var d = 0; d < 7; d++)
        {
            _weekdays[d] = weekdays[d] || (d == 0 && weekdays[7]);
        }

        _anyDay = fields[2] == "*";
        _anyWeekday = fields[4] == "*";
    }

    public TimeZoneInfo Zone { get; }

    /// <summary>Parses <paramref name="expression"/>; throws <see cref="FormatException"/> with the reason when it does not read.</summary>
    public static CronSchedule Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var text = expression.Trim();
        var zone = TimeZoneInfo.Utc;
        if (text.StartsWith("CRON_TZ=", StringComparison.Ordinal))
        {
            var space = text.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0)
            {
                throw new FormatException("CRON_TZ=<zone> is followed by the five fields.");
            }

            var name = text[8..space];
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(name);
            }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw new FormatException($"'{name}' is not a time zone.", e);
            }

            text = text[(space + 1)..].Trim();
        }

        var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            throw new FormatException("A schedule has five fields: minute hour day-of-month month day-of-week.");
        }

        return new CronSchedule(zone, fields);
    }

    /// <summary>Whether <paramref name="expression"/> reads; the reason when not.</summary>
    public static bool TryParse(string expression, out CronSchedule? schedule, out string? error)
    {
        try
        {
            schedule = Parse(expression);
            error = null;
            return true;
        }
        catch (FormatException e)
        {
            schedule = null;
            error = e.Message;
            return false;
        }
    }

    /// <summary>
    /// The shortest time between two runs over the day after <paramref name="from"/> (at most 200 runs looked at), or
    /// <see cref="TimeSpan.MaxValue"/> when it runs once or never in that day: a save-time check of how often it runs.
    /// </summary>
    public TimeSpan ShortestGap(DateTimeOffset from)
    {
        var shortest = TimeSpan.MaxValue;
        var previous = Next(from);
        var end = from.AddDays(1);
        for (var i = 0; i < 200 && previous is { } at && at < end; i++)
        {
            if (Next(at) is not { } next)
            {
                break;
            }

            shortest = next - at < shortest ? next - at : shortest;
            previous = next;
        }

        return shortest;
    }

    /// <summary>The first matching minute after <paramref name="after"/>, as a UTC time; null when none within a year.</summary>
    public DateTimeOffset? Next(DateTimeOffset after)
    {
        var local = TimeZoneInfo.ConvertTime(after, Zone).DateTime;
        var candidate = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);
        var end = candidate.AddYears(1);
        while (candidate < end)
        {
            if (!_months[candidate.Month] || !DayMatches(candidate))
            {
                candidate = candidate.Date.AddDays(1);
                continue;
            }

            if (!_hours[candidate.Hour])
            {
                candidate = new DateTime(candidate.Year, candidate.Month, candidate.Day, candidate.Hour, 0, 0, DateTimeKind.Unspecified).AddHours(1);
                continue;
            }

            if (!_minutes[candidate.Minute])
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            if (Zone.IsInvalidTime(candidate))
            {
                candidate = candidate.AddMinutes(1);
                continue; // skipped by a clock change
            }

            return new DateTimeOffset(candidate, Zone.GetUtcOffset(candidate)).ToUniversalTime();
        }

        return null;
    }

    private bool DayMatches(DateTime day)
    {
        var dom = _days[day.Day];
        var dow = _weekdays[(int)day.DayOfWeek];
        return (_anyDay, _anyWeekday) switch
        {
            (true, true) => true,
            (true, false) => dow,
            (false, true) => dom,
            _ => dom || dow,
        };
    }

    private static void Fill(bool[] slots, string field, int min, int max, string name)
    {
        foreach (var part in field.Split(','))
        {
            var step = 1;
            var range = part;
            var slash = part.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                if (!int.TryParse(part[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1)
                {
                    throw new FormatException($"The {name} step in '{part}' is not a positive number.");
                }

                range = part[..slash];
            }

            int from, to;
            if (range == "*")
            {
                (from, to) = (min, max);
            }
            else if (range.Split('-') is [var a, var b])
            {
                from = Number(a, min, max, name);
                to = Number(b, min, max, name);
                if (to < from)
                {
                    throw new FormatException($"The {name} range '{range}' runs backwards.");
                }
            }
            else
            {
                from = Number(range, min, max, name);
                to = slash >= 0 ? max : from;
            }

            for (var v = from; v <= to; v += step)
            {
                slots[v] = true;
            }
        }
    }

    private static int Number(string text, int min, int max, string name) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : throw new FormatException($"'{text}' is not a {name} ({min}-{max}).");
}
