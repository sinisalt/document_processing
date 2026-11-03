using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using DocProc.Domain.Flags;

namespace DocProc.Domain.Validation;

/// <summary>
/// The bound on the flags one check raises on one document (docs/11 §11). A row field's check runs on every row, and a
/// list may have up to 20,000 rows, so one check could otherwise raise a flag per row: the first
/// <see cref="MaxFlagsPerCheck"/> are kept and the rest become one summary flag of the same code and severity, at the
/// first value left out, with <c>reason</c> <see cref="CappedReason"/> (the further values failed too) or
/// <see cref="NotCheckedReason"/> (a pattern check stopped there, so the further values were not checked), and
/// <c>count</c>, the number of values it stands for.
/// </summary>
public static class FlagCap
{
    /// <summary>The most flags one check (one field pattern, one rule, one field's schema keyword) raises on one document.</summary>
    public const int MaxFlagsPerCheck = 50;

    /// <summary>The <c>reason</c> of a summary flag whose values failed the check like the ones flagged before it.</summary>
    public const string CappedReason = "capped";

    /// <summary>The <c>reason</c> of a summary flag whose values were not checked: the check stopped at the cap.</summary>
    public const string NotCheckedReason = "notChecked";

    /// <summary>Whether <paramref name="flag"/> is a summary flag of this cap.</summary>
    public static bool IsSummary(DocumentFlag flag)
    {
        ArgumentNullException.ThrowIfNull(flag);
        return flag.Params is { } p && p.TryGetValue(FlagParams.Reason, out var reason) && reason is CappedReason or NotCheckedReason;
    }

    /// <summary>
    /// The flags of one check, capped: the first <see cref="MaxFlagsPerCheck"/>, then one <see cref="CappedReason"/>
    /// summary for the rest. The parameters named in <paramref name="keep"/> are copied from the first flag left out.
    /// The whole sequence is read (the further values are counted).
    /// </summary>
    public static List<DocumentFlag> Apply(IEnumerable<DocumentFlag> flags, params string[] keep)
    {
        ArgumentNullException.ThrowIfNull(flags);
        var kept = new List<DocumentFlag>();
        DocumentFlag? first = null;
        var more = 0;
        foreach (var flag in flags)
        {
            if (kept.Count < MaxFlagsPerCheck)
            {
                kept.Add(flag);
                continue;
            }

            first ??= flag;
            more++;
        }

        if (first is not null)
        {
            kept.Add(Summary(first, CappedReason, more, keep));
        }

        return kept;
    }

    /// <summary>
    /// <see cref="Apply"/> over flags of several checks told apart by <paramref name="checkOf"/> (the code, the field's
    /// definition path and the keyword, say): the kept flags in their order, then the summaries, in the order their
    /// checks passed the cap. A summary flag already in <paramref name="flags"/> passes through.
    /// </summary>
    public static List<DocumentFlag> ApplyPerCheck<TKey>(IEnumerable<DocumentFlag> flags, Func<DocumentFlag, TKey> checkOf, params string[] keep)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentNullException.ThrowIfNull(checkOf);
        var kept = new List<DocumentFlag>();
        var counts = new Dictionary<TKey, int>();
        var overflow = new Dictionary<TKey, (DocumentFlag First, int Count)>();
        var order = new List<TKey>();
        foreach (var flag in flags)
        {
            if (IsSummary(flag))
            {
                kept.Add(flag);
                continue;
            }

            var key = checkOf(flag);
            var count = counts.GetValueOrDefault(key) + 1;
            counts[key] = count;
            if (count <= MaxFlagsPerCheck)
            {
                kept.Add(flag);
            }
            else if (overflow.TryGetValue(key, out var seen))
            {
                overflow[key] = (seen.First, seen.Count + 1);
            }
            else
            {
                overflow[key] = (flag, 1);
                order.Add(key);
            }
        }

        kept.AddRange(order.Select(key => Summary(overflow[key].First, CappedReason, overflow[key].Count, keep)));
        return kept;
    }

    /// <summary>
    /// The summary flag for <paramref name="count"/> values from <paramref name="first"/> on: its code, severity and
    /// field, the parameters <paramref name="keep"/> names, <c>reason</c>, <c>field</c> and <c>count</c>.
    /// </summary>
    public static DocumentFlag Summary(DocumentFlag first, string reason, int count, params string[] keep)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(keep);
        var at = first.Field;
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keep)
        {
            if (first.Params is { } p && p.TryGetValue(key, out var value))
            {
                parameters[key] = value;
            }
        }

        parameters[FlagParams.Reason] = reason;
        if (at is not null)
        {
            parameters["field"] = at;
        }

        parameters["count"] = count.ToString(CultureInfo.InvariantCulture);
        var values = count == 1 ? "1 more value" : $"{count.ToString(CultureInfo.InvariantCulture)} more values";
        var where = at is null ? string.Empty : $", starting at '{at}',";
        var message = reason == NotCheckedReason
            ? $"{values}{where} {(count == 1 ? "was" : "were")} not checked: the check stopped after {MaxFlagsPerCheck} values failed it."
            : $"{values}{where} failed this check too; only the first {MaxFlagsPerCheck} failures are flagged one by one.";
        return first with { Message = message, Params = parameters };
    }
}

/// <summary>
/// The pattern evaluations one validation of one document may make (docs/11 §11), across the field patterns of the
/// structural check and the <c>pattern</c> rules: up to 300 fields and 200 rules over up to 20,000 row values, each
/// match bounded only by its 100 ms timeout, would otherwise keep the worker (or a correction's inline re-validation)
/// busy for minutes. Past <see cref="MaxEvaluations"/> evaluations or <see cref="MaxTime"/> spent matching, no further
/// value is matched: each is counted in <see cref="Skipped"/> instead, and <see cref="SkippedFlag"/> raises one
/// blocking <c>CHECKS_SKIPPED</c>, so the document goes to review. One instance per validation; not thread-safe.
/// </summary>
public sealed class PatternBudget
{
    /// <summary>The default number of pattern evaluations per validation of one document.</summary>
    public const int DefaultMaxEvaluations = 50_000;

    /// <summary>The default time per validation of one document all pattern matches together may take.</summary>
    public static readonly TimeSpan DefaultMaxTime = TimeSpan.FromSeconds(2);

    /// <summary>The <c>reason</c> of the <c>CHECKS_SKIPPED</c> flag this budget raises.</summary>
    public const string SkippedReason = "patternBudget";

    public PatternBudget()
        : this(DefaultMaxEvaluations, DefaultMaxTime)
    {
    }

    public PatternBudget(int maxEvaluations, TimeSpan maxTime)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxEvaluations);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTime, TimeSpan.Zero);
        MaxEvaluations = maxEvaluations;
        MaxTime = maxTime;
    }

    public int MaxEvaluations { get; }

    public TimeSpan MaxTime { get; }

    /// <summary>The values matched so far.</summary>
    public int Evaluations { get; private set; }

    /// <summary>The time the matches so far took.</summary>
    public TimeSpan Elapsed { get; private set; }

    /// <summary>The values that were not matched because the budget was spent.</summary>
    public int Skipped { get; private set; }

    public bool IsSpent => Evaluations >= MaxEvaluations || Elapsed >= MaxTime;

    /// <summary>
    /// Whether <paramref name="regex"/> matches <paramref name="text"/> (a match its timeout cuts short is no match), or
    /// null when the budget is spent: the value is not matched and counts as skipped.
    /// </summary>
    public bool? IsMatch(Regex regex, string text)
    {
        ArgumentNullException.ThrowIfNull(regex);
        ArgumentNullException.ThrowIfNull(text);
        if (IsSpent)
        {
            Skipped++;
            return null;
        }

        Evaluations++;
        var start = Stopwatch.GetTimestamp();
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        finally
        {
            Elapsed += Stopwatch.GetElapsedTime(start);
        }
    }

    /// <summary>One blocking <c>CHECKS_SKIPPED {reason: patternBudget, count}</c> when values were skipped, else null.</summary>
    public DocumentFlag? SkippedFlag()
    {
        if (Skipped == 0)
        {
            return null;
        }

        var count = Skipped.ToString(CultureInfo.InvariantCulture);
        return FlagCodes.Create(
            FlagCodes.ChecksSkipped,
            $"{count} pattern {(Skipped == 1 ? "check was" : "checks were")} skipped: the document reached the limit of {MaxEvaluations.ToString(CultureInfo.InvariantCulture)} pattern evaluations or {MaxTime.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s of matching per validation. Check those values by hand.",
            null,
            FlagParams.Of((FlagParams.Reason, SkippedReason), ("count", count)));
    }
}
