namespace DocProc.Domain.Validation;

/// <summary>
/// A bound on how large a regular expression becomes when the platform runs it (NonBacktracking, docs/11 §11): that
/// engine unrolls a counted repetition (<c>x{0,99}</c> is 99 copies of <c>x</c>) and the copies multiply when counted
/// repetitions nest, so a pattern of twenty characters such as <c>(?:[^§]{0,99}\d){0,99}§</c> becomes an automaton of
/// about ten thousand nodes, whose state cache grows by hundreds of megabytes on one line of text and whose matches run
/// into their time limit. <see cref="Estimate"/> counts the unrolled atoms (a literal, an escape or a character class
/// counts 1, a group the sum of its contents, <c>{n,m}</c> multiplies by <c>m</c>, <c>{n,}</c> by <c>n + 1</c>;
/// <c>*</c>, <c>+</c> and <c>?</c> loop without copies), and a pattern above <see cref="MaxUnrolled"/> is refused
/// wherever a tenant or a reviewer supplies one (field patterns at publish, the table tool's <c>skipRows</c> and
/// <c>stopAt</c>). The estimate reads the syntax only; whether the pattern compiles is checked separately.
/// </summary>
public static class PatternSize
{
    /// <summary>The most unrolled atoms a pattern may have: far above any field or line pattern, far below the engine's own limit.</summary>
    public const int MaxUnrolled = 500;

    private const long Cap = 1_000_000;

    /// <summary>Whether the pattern stays within <see cref="MaxUnrolled"/>.</summary>
    public static bool IsSmallEnough(string pattern) => Estimate(pattern) <= MaxUnrolled;

    /// <summary>The number of atoms <paramref name="pattern"/> unrolls to (capped at a million).</summary>
    public static long Estimate(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var frames = new Stack<(long Total, long Last)>();
        long total = 0;
        long last = 0;
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            switch (c)
            {
                case '\\':
                    i = EscapeEnd(pattern, i);
                    (total, last) = Atom(total);
                    break;
                case '[':
                    i = ClassEnd(pattern, i);
                    (total, last) = Atom(total);
                    break;
                case '(':
                    frames.Push((total, last));
                    total = 0;
                    last = 0;
                    i = GroupContentStart(pattern, i + 1);
                    break;
                case ')':
                    var inner = total;
                    (total, _) = frames.Count > 0 ? frames.Pop() : (0, 0);
                    total = Math.Min(Cap, total + inner);
                    last = inner;
                    i++;
                    break;
                case '|':
                    last = 0;
                    i++;
                    break;
                case '*' or '+' or '?':
                    i = SkipLazy(pattern, i + 1);
                    break;
                case '{' when Quantifier(pattern, i) is { } quantifier:
                    // The atom is counted once already; its copies come on top.
                    total = Math.Min(Cap, total + (last * (quantifier.Count - 1)));
                    last = Math.Min(Cap, last * quantifier.Count);
                    i = SkipLazy(pattern, quantifier.End);
                    break;
                default:
                    (total, last) = Atom(total);
                    i++;
                    break;
            }
        }

        while (frames.Count > 0)
        {
            var inner = total;
            (total, _) = frames.Pop();
            total = Math.Min(Cap, total + inner);
        }

        return total;
    }

    private static (long Total, long Last) Atom(long total) => (Math.Min(Cap, total + 1), 1);

    private static int SkipLazy(string pattern, int i) => i < pattern.Length && pattern[i] == '?' ? i + 1 : i;

    /// <summary>The index after an escape: <c>\d</c>, <c>\p{L}</c>, <c>\x41</c>, <c>ä</c>, <c>\cA</c>.</summary>
    private static int EscapeEnd(string pattern, int i)
    {
        if (i + 1 >= pattern.Length)
        {
            return pattern.Length;
        }

        var next = pattern[i + 1];
        if (next is 'p' or 'P' && i + 2 < pattern.Length && pattern[i + 2] == '{')
        {
            var close = pattern.IndexOf('}', i + 3);
            return close < 0 ? pattern.Length : close + 1;
        }

        return next switch
        {
            'x' => Math.Min(pattern.Length, i + 4),
            'u' => Math.Min(pattern.Length, i + 6),
            'c' => Math.Min(pattern.Length, i + 3),
            _ => i + 2,
        };
    }

    /// <summary>The index after a character class, nested subtractions (<c>[a-z-[aeiou]]</c>) and escapes included.</summary>
    private static int ClassEnd(string pattern, int i)
    {
        var j = i + 1;
        if (j < pattern.Length && pattern[j] == '^')
        {
            j++;
        }

        // A ']' right at the start is a literal.
        if (j < pattern.Length && pattern[j] == ']')
        {
            j++;
        }

        var depth = 1;
        while (j < pattern.Length)
        {
            switch (pattern[j])
            {
                case '\\':
                    j = EscapeEnd(pattern, j);
                    continue;
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    if (depth == 0)
                    {
                        return j + 1;
                    }

                    break;
            }

            j++;
        }

        return pattern.Length;
    }

    /// <summary>Past a group's prefix: <c>(?:</c>, <c>(?&lt;name&gt;</c>, <c>(?'name'</c>, <c>(?i:</c>; an option group <c>(?i)</c> stops at its <c>)</c>.</summary>
    private static int GroupContentStart(string pattern, int i)
    {
        if (i >= pattern.Length || pattern[i] != '?')
        {
            return i;
        }

        var j = i + 1;
        if (j < pattern.Length && pattern[j] is '<' or '\'' && j + 1 < pattern.Length && pattern[j + 1] is not ('=' or '!'))
        {
            var close = pattern.IndexOf(pattern[j] == '<' ? '>' : '\'', j + 1);
            return close < 0 ? pattern.Length : close + 1;
        }

        while (j < pattern.Length && (char.IsLetter(pattern[j]) || pattern[j] == '-'))
        {
            j++;
        }

        if (j < pattern.Length && pattern[j] == ')')
        {
            return j;
        }

        return Math.Min(pattern.Length, j + 1);
    }

    /// <summary><c>{n}</c>, <c>{n,}</c> or <c>{n,m}</c> at <paramref name="i"/>: how many copies it makes and where it ends; null for a literal brace.</summary>
    private static (long Count, int End)? Quantifier(string pattern, int i)
    {
        var j = i + 1;
        var n = Digits(pattern, ref j);
        if (n is null)
        {
            return null;
        }

        if (j < pattern.Length && pattern[j] == '}')
        {
            return (Math.Max(1, n.Value), j + 1);
        }

        if (j >= pattern.Length || pattern[j] != ',')
        {
            return null;
        }

        j++;
        var m = Digits(pattern, ref j);
        if (j >= pattern.Length || pattern[j] != '}')
        {
            return null;
        }

        return (Math.Max(1, m ?? (n.Value + 1)), j + 1);
    }

    private static long? Digits(string pattern, ref int j)
    {
        long? value = null;
        while (j < pattern.Length && char.IsAsciiDigit(pattern[j]))
        {
            value = Math.Min(Cap, ((value ?? 0) * 10) + (pattern[j] - '0'));
            j++;
        }

        return value;
    }
}
