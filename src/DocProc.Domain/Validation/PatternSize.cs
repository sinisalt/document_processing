namespace DocProc.Domain.Validation;

/// <summary>
/// A bound on how much work a regular expression can make when the platform runs it (NonBacktracking, docs/11 §11). The
/// engine's match timeout is no such bound: it is checked only at some points of a match, not while the engine builds
/// the states of its automaton, so a pattern that makes many large states runs for seconds whatever the timeout. Two
/// measures are bounded, and a pattern above either is refused wherever a tenant or a reviewer supplies one (field
/// patterns at publish, the table tool's patterns, identification and layout patterns):
/// <list type="bullet">
/// <item><see cref="Estimate"/>, the atoms the pattern unrolls to, at most <see cref="MaxUnrolled"/>. The engine
/// unrolls a counted repetition (<c>x{0,99}</c> is 99 copies of <c>x</c>) and the copies multiply when counted
/// repetitions nest, so a pattern of twenty characters such as <c>(?:[^§]{0,99}\d){0,99}§</c> becomes an automaton of
/// about ten thousand nodes. A literal, an escape or a character class counts 1, a group the sum of its contents,
/// <c>{n,m}</c> multiplies by <c>m</c>, <c>{n,}</c> by <c>n + 1</c>; <c>*</c>, <c>+</c> and <c>?</c> loop without
/// copies.</item>
/// <item><see cref="Variation"/>, how much the length of the pattern's varying groups can vary, at most
/// <see cref="MaxVariation"/>. A repeated group whose length varies makes the engine track every combination of
/// lengths of its copies: <c>(.{0,15}a){30}x</c> unrolls to only 481 atoms but runs about 11 s per match (half a second
/// on 100 characters of text already), and its timeout does not stop it. <c>{n,m}</c> of a body adds <c>m</c> times the
/// body's own range (its longest minus its shortest text, a <c>*</c> or <c>+</c> loop counted as one copy) and the
/// variation inside it, and for a body longer than one character also <c>m - n</c> times its longest text (that many
/// more or fewer copies); <c>*</c>, <c>+</c> and <c>{n,}</c> add <see cref="LoopCopies"/> times the body's range and
/// the variation inside it; alternatives of different length ranges, one of which varies itself, add the group's range
/// (<c>(?:a|.{0,14})</c> adds 14). A repetition of one character or class (<c>.{0,450}</c>, <c>\s*</c>), a
/// fixed-length body repeated a fixed number of times (<c>(?:\d{3}){4}</c>) and words of different lengths
/// (<c>(?:ab|from|von)</c>) add nothing; an IBAN's <c>(?: ?[0-9A-Z]{4}){3,7}</c> adds 27, a phone number's
/// <c>(?:[ -]?\d{2,4}){2,4}</c> 22.</item>
/// </list>
/// The measures read the syntax only; whether the pattern compiles is checked separately.
/// </summary>
public static class PatternSize
{
    /// <summary>The most unrolled atoms a pattern may have: far above any field or line pattern, far below the engine's own limit.</summary>
    public const int MaxUnrolled = 500;

    /// <summary>
    /// The most <see cref="Variation"/> a pattern may have: above the field and line patterns (an IBAN 27 to 38, a phone
    /// number 22), below the patterns that run for seconds (the ones at it take a few hundred milliseconds on a first
    /// match over 20,000 characters).
    /// </summary>
    public const int MaxVariation = 64;

    /// <summary>The copies <see cref="Variation"/> counts for an unbounded loop (<c>*</c>, <c>+</c>, <c>{n,}</c>).</summary>
    public const int LoopCopies = 8;

    private const long Cap = 1_000_000;

    /// <summary>Whether the pattern stays within <see cref="MaxUnrolled"/> and <see cref="MaxVariation"/>.</summary>
    public static bool IsSmallEnough(string pattern) => Problem(pattern) is null;

    /// <summary>Why the pattern is refused, in words for the person who wrote it; null when it stays within both bounds.</summary>
    public static string? Problem(string pattern)
    {
        var measure = Measure(pattern);
        if (measure.Unrolled > MaxUnrolled)
        {
            return $"counted repetitions such as {{0,99}} are unrolled, and nested ones multiply; at most {MaxUnrolled} characters unrolled.";
        }

        if (measure.Variation > MaxVariation)
        {
            return $"a repeated group whose length varies, such as (.{{0,15}}a){{30}}, makes the engine track every combination of lengths; at most {MaxVariation} (the copies times the lengths such a group can vary by).";
        }

        return null;
    }

    /// <summary>The number of atoms <paramref name="pattern"/> unrolls to (capped at a million).</summary>
    public static long Estimate(string pattern) => Measure(pattern).Unrolled;

    /// <summary>How much the varying groups of <paramref name="pattern"/> can vary (capped at a million).</summary>
    public static long Variation(string pattern) => Measure(pattern).Variation;

    private static (long Unrolled, long Variation) Measure(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var frames = new Stack<Frame>();
        var frame = new Frame();
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            switch (c)
            {
                case '\\':
                    var end = EscapeEnd(pattern, i);
                    frame.Add(Part.Atom(IsZeroWidthEscape(pattern, i, end) ? 0 : 1));
                    i = end;
                    break;
                case '[':
                    i = ClassEnd(pattern, i);
                    frame.Add(Part.Atom(1));
                    break;
                case '(':
                    frames.Push(frame);
                    frame = new Frame();
                    i = GroupContentStart(pattern, i + 1);
                    break;
                case ')':
                    var group = frame.Close();
                    frame = frames.Count > 0 ? frames.Pop() : new Frame();
                    frame.Add(group);
                    i++;
                    break;
                case '|':
                    frame.Alternate();
                    i++;
                    break;
                case '*':
                    frame.Repeat(last => last with { Min = 0, Variation = Repeated(LoopCopies, last) });
                    i = SkipLazy(pattern, i + 1);
                    break;
                case '+':
                    frame.Repeat(last => last with { Variation = Repeated(LoopCopies, last) });
                    i = SkipLazy(pattern, i + 1);
                    break;
                case '?':
                    frame.Repeat(last => last with { Min = 0 });
                    i = SkipLazy(pattern, i + 1);
                    break;
                case '{' when Quantifier(pattern, i) is { } quantifier:
                    frame.Repeat(last => new Part(
                        Mul(last.Atoms, quantifier.Count),
                        Mul(last.Min, quantifier.Lower),
                        Mul(last.Max, quantifier.Count),
                        quantifier.Open ? Repeated(Math.Max(quantifier.Count, LoopCopies), last)
                        : quantifier.Count > 1 ? Sum(Repeated(quantifier.Count, last), Spread(quantifier.Count - quantifier.Lower, last))
                        : last.Variation));
                    i = SkipLazy(pattern, quantifier.End);
                    break;
                case '^' or '$':
                    frame.Add(Part.Atom(0));
                    i++;
                    break;
                default:
                    frame.Add(Part.Atom(1));
                    i++;
                    break;
            }
        }

        while (frames.Count > 0)
        {
            var group = frame.Close();
            frame = frames.Pop();
            frame.Add(group);
        }

        var whole = frame.Close();
        return (whole.Atoms, whole.Variation);
    }

    /// <summary>The variation of <paramref name="copies"/> copies of <paramref name="body"/>: each copy varies by the body's length range and by what varies inside it.</summary>
    private static long Repeated(long copies, Part body) => Mul(copies, Math.Min(Cap, Math.Max(0, body.Max - body.Min) + body.Variation));

    /// <summary>
    /// What a varying number of copies adds: <paramref name="extra"/> copies more or fewer of a body longer than one
    /// character are as many lengths, each the body's length apart (<c>(?:.{30}){0,15}</c> adds 450); a single
    /// character or class (<c>.{0,450}</c>) adds nothing, the engine merges its copies.
    /// </summary>
    private static long Spread(long extra, Part body) => body.Max > 1 ? Mul(extra, body.Max) : 0;

    private static long Mul(long a, long b) => a == 0 || b == 0 ? 0 : a > Cap / b ? Cap : Math.Min(Cap, a * b);

    private static long Sum(long a, long b) => Math.Min(Cap, a + b);

    /// <summary>One element of a pattern: the atoms it unrolls to, the shortest and longest text it matches (a loop counted as one copy) and the variation of its repeated parts.</summary>
    private readonly record struct Part(long Atoms, long Min, long Max, long Variation)
    {
        public static Part Atom(long width) => new(1, width, width, 0);
    }

    /// <summary>A group being read: its alternatives so far, the one being read and its last element (what a quantifier applies to).</summary>
    private sealed class Frame
    {
        private long _atoms;
        private long _variation;
        private long _min;
        private long _max;
        private long _alternativesMin = long.MaxValue;
        private long _alternativesMax;
        private int _alternatives;
        private bool _anAlternativeVaries;
        private bool _rangesDiffer;
        private Part? _last;

        public void Add(Part part)
        {
            _atoms = Sum(_atoms, part.Atoms);
            _variation = Sum(_variation, part.Variation);
            _min = Sum(_min, part.Min);
            _max = Sum(_max, part.Max);
            _last = part;
        }

        /// <summary>Replaces the last element by its repetition; a quantifier with nothing before it (a syntax error) does nothing.</summary>
        public void Repeat(Func<Part, Part> repeat)
        {
            if (_last is not { } last)
            {
                return;
            }

            var repeated = repeat(last);
            _atoms = Sum(Math.Max(0, _atoms - last.Atoms), repeated.Atoms);
            _variation = Sum(Math.Max(0, _variation - last.Variation), repeated.Variation);
            _min = Sum(Math.Max(0, _min - last.Min), repeated.Min);
            _max = Sum(Math.Max(0, _max - last.Max), repeated.Max);
            _last = repeated;
        }

        public void Alternate()
        {
            _rangesDiffer |= _alternatives > 0 && (_min != _alternativesMin || _max != _alternativesMax);
            _alternatives++;
            _anAlternativeVaries |= _min < _max;
            _alternativesMin = Math.Min(_alternativesMin, _min);
            _alternativesMax = Math.Max(_alternativesMax, _max);
            _min = 0;
            _max = 0;
            _last = null;
        }

        /// <summary>
        /// The group as one element. Alternatives of different length ranges add the group's range to the variation
        /// when one of them varies itself (<c>(?:a|.{0,14})</c> adds 14); words of different lengths
        /// (<c>(?:ab|from|von)</c>) and alternatives of one range (<c>(?:.{0,15}a|.{0,15}b)</c>) add nothing.
        /// </summary>
        public Part Close()
        {
            Alternate();
            var variation = _rangesDiffer && _anAlternativeVaries ? Sum(_variation, _alternativesMax - _alternativesMin) : _variation;
            return new Part(_atoms, _alternativesMin, _alternativesMax, variation);
        }
    }

    private static int SkipLazy(string pattern, int i) => i < pattern.Length && pattern[i] == '?' ? i + 1 : i;

    /// <summary>Whether the escape from <paramref name="i"/> to <paramref name="end"/> matches no text: <c>\b</c>, <c>\B</c>, <c>\A</c>, <c>\z</c>, <c>\Z</c>, <c>\G</c>.</summary>
    private static bool IsZeroWidthEscape(string pattern, int i, int end) =>
        end == i + 2 && pattern[i + 1] is 'b' or 'B' or 'A' or 'z' or 'Z' or 'G';

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

    /// <summary>
    /// <c>{n}</c>, <c>{n,}</c> or <c>{n,m}</c> at <paramref name="i"/>: how many copies it makes, the fewest it needs,
    /// whether it is open (<c>{n,}</c>) and where it ends; null for a literal brace.
    /// </summary>
    private static (long Count, long Lower, bool Open, int End)? Quantifier(string pattern, int i)
    {
        var j = i + 1;
        var n = Digits(pattern, ref j);
        if (n is null)
        {
            return null;
        }

        if (j < pattern.Length && pattern[j] == '}')
        {
            return (Math.Max(1, n.Value), n.Value, false, j + 1);
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

        return (Math.Max(1, m ?? (n.Value + 1)), n.Value, m is null, j + 1);
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
