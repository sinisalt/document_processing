using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocProc.Domain.Validation.Expressions;

/// <summary>
/// The bounds of a rule expression (docs/11 §11, the <c>expression</c> kind and the <c>when</c> of <c>requiredIf</c>):
/// its text, its parsed size and nesting, and the work one evaluation may do. Nothing in the language reads anything but
/// the value it is given (no network, no clock, no files), and every evaluation stops at <see cref="MaxSteps"/>.
/// </summary>
public static class ExpressionLimits
{
    /// <summary>The longest expression text, in characters.</summary>
    public const int MaxLength = 2_000;

    /// <summary>The most nodes of a parsed expression.</summary>
    public const int MaxNodes = 400;

    /// <summary>The deepest nesting of a parsed expression.</summary>
    public const int MaxDepth = 32;

    /// <summary>The most steps (a node evaluated, an item visited) of one evaluation.</summary>
    public const int MaxSteps = 50_000;

    /// <summary>The most steps every evaluation of one rule on one document may take together (a rule run per row).</summary>
    public const int MaxStepsPerRule = 2_000_000;

    /// <summary>The longest time one evaluation may run.</summary>
    public static readonly TimeSpan MaxTimePerEvaluation = TimeSpan.FromSeconds(1);

    /// <summary>The longest time every evaluation of one rule on one document may run together.</summary>
    public static readonly TimeSpan MaxTimePerRule = TimeSpan.FromSeconds(2);

    /// <summary>The longest time the expressions of every rule of one validation of one document may run together.</summary>
    public static readonly TimeSpan MaxTimePerDocument = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The characters of a string one step pays for: a step that reads or builds a longer string (<c>$lowercase</c>,
    /// <c>&amp;</c>, <c>$string</c>, <c>=</c> on two strings) costs a step more per this many characters.
    /// </summary>
    public const int CharactersPerStep = 128;

    /// <summary>The longest sequence an evaluation builds.</summary>
    public const int MaxSequence = 20_000;

    /// <summary>The longest string an evaluation builds.</summary>
    public const int MaxString = 10_000;
}

/// <summary>An expression that does not parse, or is above <see cref="ExpressionLimits"/>.</summary>
public sealed class RuleExpressionSyntaxException(string message, int position) : Exception(message)
{
    public int Position { get; } = position;
}

/// <summary>An expression that cannot be evaluated over the value it was given (a string multiplied, a division by zero).</summary>
public sealed class RuleExpressionEvaluationException(string message) : Exception(message);

/// <summary>
/// An evaluation that ran out of its steps or its time (<see cref="ExpressionLimits"/>, or the rule's or the
/// document's <see cref="ExpressionBudget"/>), or built a list or a string above the limits.
/// </summary>
public sealed class RuleExpressionBudgetException(string message) : Exception(message);

/// <summary>
/// The work expressions may do: per rule on one document (<see cref="ExpressionLimits.MaxStepsPerRule"/> and
/// <see cref="ExpressionLimits.MaxTimePerRule"/>), shared by every evaluation of the rule, per row or once; and per
/// validation of one document (<see cref="ForDocument"/>, <see cref="ExpressionLimits.MaxTimePerDocument"/>), the
/// parent of the rules' budgets. The time counted is the time evaluations ran, so a budget made early is not spent by
/// the work around it. Not thread-safe: one validation evaluates its rules one after the other.
/// </summary>
public sealed class ExpressionBudget
{
    private readonly ExpressionBudget? _parent;

    public ExpressionBudget(int steps = ExpressionLimits.MaxStepsPerRule, TimeSpan? maxTime = null, ExpressionBudget? parent = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(steps);
        Remaining = steps;
        MaxTime = maxTime ?? ExpressionLimits.MaxTimePerRule;
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTime, TimeSpan.Zero);
        _parent = parent;
    }

    /// <summary>The budget of one validation of one document: no step bound of its own (each rule has one), and <see cref="ExpressionLimits.MaxTimePerDocument"/>.</summary>
    public static ExpressionBudget ForDocument(TimeSpan? maxTime = null) => new(int.MaxValue, maxTime ?? ExpressionLimits.MaxTimePerDocument);

    /// <summary>The budget of one rule on a document whose validation has the budget <paramref name="document"/>.</summary>
    public static ExpressionBudget ForRule(ExpressionBudget? document) => new(parent: document);

    public int Remaining { get; private set; }

    public TimeSpan MaxTime { get; }

    /// <summary>The time the finished evaluations on this budget ran together.</summary>
    public TimeSpan Elapsed { get; private set; }

    internal void Take(int steps)
    {
        Remaining -= steps;
        if (Remaining < 0)
        {
            throw new RuleExpressionBudgetException("The rule's expressions ran out of their steps on this document.");
        }

        _parent?.Take(steps);
    }

    /// <summary>Throws when the finished evaluations and the one running for <paramref name="running"/> are over the time.</summary>
    internal void CheckTime(TimeSpan running)
    {
        if (Elapsed + running >= MaxTime)
        {
            var seconds = MaxTime.TotalSeconds.ToString(CultureInfo.InvariantCulture);
            throw new RuleExpressionBudgetException(_parent is null
                ? $"The rules' expressions ran longer than {seconds} s on this document."
                : $"The rule's expressions ran longer than {seconds} s on this document.");
        }

        _parent?.CheckTime(running);
    }

    internal void Spend(TimeSpan time)
    {
        Elapsed += time;
        _parent?.Spend(time);
    }
}

/// <summary>
/// A bounded subset of JSONata (docs/11 §11, Correction (P5)) for rule predicates and row arithmetic, evaluated in
/// process over the values view: paths (<c>lines.lineNet</c>, mapped over arrays and flattened), filters and indexes
/// (<c>lines[quantity &gt; 0]</c>, <c>lines[0]</c>), the operators <c>+ - * / % &amp; = != &lt; &lt;= &gt; &gt;= in and
/// or</c>, the literals <c>true false null</c>, numbers and strings, the context <c>$</c> and the root <c>$$</c>,
/// variables the caller binds (<c>$today</c>, <c>$supplier</c>) and the functions of <see cref="Functions"/>.
/// <para>
/// It is not the out-of-process JSONata of the outputs: rules run inside validation, also inline in the API when a
/// reviewer corrects a value, and are pure. What bounds it: <see cref="ExpressionLimits"/> at parse (text, nodes,
/// depth), per evaluation (steps paid in proportion to the data touched, time, sequence and string length), and per rule and
/// document (an <see cref="ExpressionBudget"/> of steps and time); there is no function that reads anything outside the
/// value given, no recursion, no user-defined functions, no regular expressions. A money value (an object with
/// <c>amount</c>) is its amount wherever a number is expected. A comparison with an absent value is false, as in JSONata.
/// </para>
/// </summary>
public sealed class RuleExpression
{
    /// <summary>The functions the language has, by name (without the <c>$</c>).</summary>
    public static readonly IReadOnlySet<string> Functions = new HashSet<string>(StringComparer.Ordinal)
    {
        "exists", "not", "boolean", "number", "string", "sum", "count", "max", "min", "average", "abs", "round", "floor",
        "ceil", "length", "contains", "lowercase", "uppercase", "trim", "substring", "distinct",
    };

    private readonly Node _root;

    private RuleExpression(string text, Node root, IReadOnlySet<string> variables)
    {
        Text = text;
        _root = root;
        Variables = variables;
    }

    public string Text { get; }

    /// <summary>The variables (without <c>$</c>) the expression names.</summary>
    public IReadOnlySet<string> Variables { get; }

    /// <summary>
    /// Parses <paramref name="text"/>; <paramref name="knownVariables"/> are the variables the caller binds (an unknown
    /// one is a syntax error, so a typo is refused at publish). Throws <see cref="RuleExpressionSyntaxException"/>.
    /// </summary>
    public static RuleExpression Parse(string text, IEnumerable<string> knownVariables)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(knownVariables);
        if (text.Length > ExpressionLimits.MaxLength)
        {
            throw new RuleExpressionSyntaxException($"The expression is longer than {ExpressionLimits.MaxLength} characters.", ExpressionLimits.MaxLength);
        }

        var known = knownVariables.ToHashSet(StringComparer.Ordinal);
        var parser = new Parser(Tokenizer.Tokenize(text), known);
        var root = parser.ParseAll();
        return new RuleExpression(text, root, parser.UsedVariables);
    }

    /// <summary>Parses <paramref name="text"/>, or returns the reason it does not parse.</summary>
    public static bool TryParse(string text, IEnumerable<string> knownVariables, out RuleExpression? expression, out string? error)
    {
        try
        {
            expression = Parse(text, knownVariables);
            error = null;
            return true;
        }
        catch (RuleExpressionSyntaxException e)
        {
            expression = null;
            error = $"{e.Message} (at character {e.Position + 1})";
            return false;
        }
    }

    /// <summary>
    /// Evaluates the expression with <paramref name="context"/> as <c>$</c> (and as the place plain names are read
    /// from), <paramref name="root"/> as <c>$$</c>, and the bound <paramref name="variables"/>. The result is a JSON value
    /// (null for an absent value). Throws <see cref="RuleExpressionEvaluationException"/> or <see cref="RuleExpressionBudgetException"/>.
    /// </summary>
    public JsonNode? Evaluate(JsonNode? context, JsonNode? root = null, IReadOnlyDictionary<string, JsonNode?>? variables = null, ExpressionBudget? budget = null) =>
        Run(context, root, variables, budget, (evaluation, result) => evaluation.Export(result));

    /// <summary>The expression's result read as a predicate (JSONata's boolean cast: an absent value, false, 0, "" and an empty list are false).</summary>
    public bool Test(JsonNode? context, JsonNode? root = null, IReadOnlyDictionary<string, JsonNode?>? variables = null, ExpressionBudget? budget = null) =>
        Run(context, root, variables, budget, (evaluation, result) => evaluation.Truthy(result));

    /// <summary>The expression's result as a number (a money value's amount), or null when it is absent or not a number.</summary>
    public decimal? EvaluateNumber(JsonNode? context, JsonNode? root = null, IReadOnlyDictionary<string, JsonNode?>? variables = null, ExpressionBudget? budget = null) =>
        Run<decimal?>(context, root, variables, budget, (_, result) => Single(result) switch
        {
            decimal number => number,
            JsonObject money when money["amount"] is JsonValue amount && amount.TryGetValue<decimal>(out var a) => a,
            _ => null,
        });

    /// <summary>
    /// One evaluation: the time it ran is spent from <paramref name="budget"/> however it ends, and a budget whose time
    /// is already spent stops it before it starts.
    /// </summary>
    private T Run<T>(JsonNode? context, JsonNode? root, IReadOnlyDictionary<string, JsonNode?>? variables, ExpressionBudget? budget, Func<Evaluation, object?, T> read)
    {
        budget?.CheckTime(TimeSpan.Zero);
        var start = Stopwatch.GetTimestamp();
        try
        {
            var evaluation = new Evaluation(variables, budget, start);
            evaluation.SetRoot(root ?? context);
            var result = evaluation.Eval(_root, ReferenceEquals(root ?? context, context) ? evaluation.Root : evaluation.Load(context));
            return read(evaluation, result);
        }
        finally
        {
            budget?.Spend(Stopwatch.GetElapsedTime(start));
        }
    }

    // ------------------------------------------------------------------ values
    // null is "absent" (JSONata's undefined); JsonNull is JSON null; bool, decimal, string, JsonObject and Seq are values.
    // An object stays the JsonObject it was read from; an array becomes a Seq when it is read (Evaluation.Load), which
    // costs a step per item.

    private sealed class JsonNull
    {
        public static readonly JsonNull Instance = new();
    }

    private sealed class Seq(List<object> items)
    {
        public List<object> Items { get; } = items;
    }

    /// <summary>A JSON value that is not an array (an array is read by <see cref="Evaluation.Load"/>, which counts its items).</summary>
    private static object? Scalar(JsonNode? node) => node switch
    {
        JsonObject obj => obj,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => ValuePaths.Number(value),
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Null => JsonNull.Instance,
            _ => null,
        },
        _ => null,
    };

    private static object? Single(object? value) => value is Seq { Items.Count: 1 } seq ? seq.Items[0] : value;

    /// <summary>
    /// A number used as an index, a start or a length, clamped to the range of <see cref="int"/> (and truncated): an
    /// index or a start beyond any string or sequence is simply past its end (absent, or an empty string, as in
    /// JSONata), never an <see cref="OverflowException"/> out of the evaluator.
    /// </summary>
    internal static int ClampToInt(decimal value) =>
        value >= int.MaxValue ? int.MaxValue : value <= int.MinValue ? int.MinValue : (int)value;

    private static decimal? AsNumber(object? value) => Single(value) switch
    {
        decimal d => d,
        JsonObject money when money["amount"] is JsonValue amount && Scalar(amount) is decimal a => a,
        _ => null,
    };

    private static string Describe(object? value) => value switch
    {
        null => "an absent value",
        JsonNull => "null",
        bool => "a boolean",
        decimal => "a number",
        string => "a string",
        Seq => "a list",
        JsonObject => "an object",
        _ => "a value",
    };

    // ------------------------------------------------------------------ syntax tree

    private abstract record Node;

    private sealed record Literal(object? Value) : Node;

    private sealed record Name(string Key) : Node;

    private sealed record Variable(string Key) : Node;

    private sealed record Context : Node;

    private sealed record RootRef : Node;

    private sealed record Step(Node Left, Node Right) : Node;

    private sealed record Filter(Node Target, Node Predicate) : Node;

    private sealed record Unary(string Op, Node Operand) : Node;

    private sealed record Binary(string Op, Node Left, Node Right) : Node;

    private sealed record Call(string Function, IReadOnlyList<Node> Args) : Node;

    // ------------------------------------------------------------------ tokens

    private enum TokenKind
    {
        Number,
        String,
        Name,
        Variable,
        Op,
        End,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Position, decimal Number = 0);

    private static class Tokenizer
    {
        private static readonly string[] Operators = ["!=", "<=", ">=", "$$", ".", "[", "]", "(", ")", ",", "+", "-", "*", "/", "%", "&", "=", "<", ">"];

        public static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (char.IsAsciiDigit(c))
                {
                    var start = i;
                    while (i < text.Length && char.IsAsciiDigit(text[i]))
                    {
                        i++;
                    }

                    if (i + 1 < text.Length && text[i] == '.' && char.IsAsciiDigit(text[i + 1]))
                    {
                        i++;
                        while (i < text.Length && char.IsAsciiDigit(text[i]))
                        {
                            i++;
                        }
                    }

                    var literal = text[start..i];
                    if (!decimal.TryParse(literal, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                    {
                        throw new RuleExpressionSyntaxException($"'{literal}' is not a number this language can hold.", start);
                    }

                    tokens.Add(new Token(TokenKind.Number, literal, start, number));
                    continue;
                }

                if (c is '"' or '\'')
                {
                    var start = i++;
                    var sb = new StringBuilder();
                    while (true)
                    {
                        if (i >= text.Length)
                        {
                            throw new RuleExpressionSyntaxException("A string is not closed.", start);
                        }

                        var ch = text[i++];
                        if (ch == c)
                        {
                            break;
                        }

                        if (ch == '\\')
                        {
                            if (i >= text.Length)
                            {
                                throw new RuleExpressionSyntaxException("A string ends in a backslash.", i - 1);
                            }

                            var escaped = text[i++];
                            sb.Append(escaped switch
                            {
                                'n' => '\n',
                                't' => '\t',
                                _ => escaped,
                            });
                            continue;
                        }

                        sb.Append(ch);
                    }

                    tokens.Add(new Token(TokenKind.String, sb.ToString(), start));
                    continue;
                }

                if (c == '$' && i + 1 < text.Length && IsNameStart(text[i + 1]))
                {
                    var start = i++;
                    while (i < text.Length && IsNamePart(text[i]))
                    {
                        i++;
                    }

                    tokens.Add(new Token(TokenKind.Variable, text[(start + 1)..i], start));
                    continue;
                }

                if (IsNameStart(c))
                {
                    var start = i;
                    while (i < text.Length && IsNamePart(text[i]))
                    {
                        i++;
                    }

                    tokens.Add(new Token(TokenKind.Name, text[start..i], start));
                    continue;
                }

                if (c == '`')
                {
                    var start = i++;
                    var end = text.IndexOf('`', i);
                    if (end < 0)
                    {
                        throw new RuleExpressionSyntaxException("A quoted name is not closed.", start);
                    }

                    tokens.Add(new Token(TokenKind.Name, text[i..end], start));
                    i = end + 1;
                    continue;
                }

                var op = Operators.FirstOrDefault(o => string.CompareOrdinal(text, i, o, 0, o.Length) == 0);
                if (op is null && c == '$')
                {
                    op = "$";
                }

                if (op is null)
                {
                    throw new RuleExpressionSyntaxException($"'{c}' is not part of the expression language.", i);
                }

                tokens.Add(new Token(TokenKind.Op, op, i));
                i += op.Length;
            }

            tokens.Add(new Token(TokenKind.End, string.Empty, text.Length));
            return tokens;
        }

        private static bool IsNameStart(char c) => char.IsAsciiLetter(c) || c == '_';

        private static bool IsNamePart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
    }

    // ------------------------------------------------------------------ parser

    private sealed class Parser(List<Token> tokens, HashSet<string> known)
    {
        private int _at;
        private int _nodes;
        private int _depth;

        public HashSet<string> UsedVariables { get; } = new(StringComparer.Ordinal);

        private Token Peek => tokens[_at];

        public Node ParseAll()
        {
            var node = Or();
            if (Peek.Kind != TokenKind.End)
            {
                throw new RuleExpressionSyntaxException($"Unexpected '{Peek.Text}'.", Peek.Position);
            }

            return node;
        }

        private T Count<T>(T node)
            where T : Node
        {
            if (++_nodes > ExpressionLimits.MaxNodes)
            {
                throw new RuleExpressionSyntaxException($"The expression has more than {ExpressionLimits.MaxNodes} parts.", Peek.Position);
            }

            return node;
        }

        private bool IsOp(string op) => Peek.Kind == TokenKind.Op && Peek.Text == op;

        private bool IsWord(string word) => Peek.Kind == TokenKind.Name && Peek.Text == word;

        private void Expect(string op)
        {
            if (!IsOp(op))
            {
                throw new RuleExpressionSyntaxException($"Expected '{op}'{(Peek.Kind == TokenKind.End ? " at the end" : $", not '{Peek.Text}'")}.", Peek.Position);
            }

            _at++;
        }

        private Node Nested(Func<Node> parse)
        {
            if (++_depth > ExpressionLimits.MaxDepth)
            {
                throw new RuleExpressionSyntaxException($"The expression nests deeper than {ExpressionLimits.MaxDepth} levels.", Peek.Position);
            }

            try
            {
                return parse();
            }
            finally
            {
                _depth--;
            }
        }

        private Node Or()
        {
            var left = And();
            while (IsWord("or"))
            {
                _at++;
                left = Count(new Binary("or", left, And()));
            }

            return left;
        }

        private Node And()
        {
            var left = Comparison();
            while (IsWord("and"))
            {
                _at++;
                left = Count(new Binary("and", left, Comparison()));
            }

            return left;
        }

        private Node Comparison()
        {
            var left = Additive();
            foreach (var op in new[] { "=", "!=", "<=", ">=", "<", ">" })
            {
                if (IsOp(op))
                {
                    _at++;
                    return Count(new Binary(op, left, Additive()));
                }
            }

            if (IsWord("in"))
            {
                _at++;
                return Count(new Binary("in", left, Additive()));
            }

            return left;
        }

        private Node Additive()
        {
            var left = Multiplicative();
            while (IsOp("+") || IsOp("-") || IsOp("&"))
            {
                var op = tokens[_at++].Text;
                left = Count(new Binary(op, left, Multiplicative()));
            }

            return left;
        }

        private Node Multiplicative()
        {
            var left = UnaryExpression();
            while (IsOp("*") || IsOp("/") || IsOp("%"))
            {
                var op = tokens[_at++].Text;
                left = Count(new Binary(op, left, UnaryExpression()));
            }

            return left;
        }

        private Node UnaryExpression()
        {
            if (IsOp("-"))
            {
                _at++;
                return Nested(() => Count(new Unary("-", UnaryExpression())));
            }

            return Postfix();
        }

        private Node Postfix()
        {
            var node = Primary();
            while (true)
            {
                if (IsOp("."))
                {
                    _at++;
                    var right = Primary();
                    if (right is not (Name or Filter))
                    {
                        throw new RuleExpressionSyntaxException("A '.' must be followed by a name.", Peek.Position);
                    }

                    node = Count(new Step(node, right));
                }
                else if (IsOp("["))
                {
                    _at++;
                    var predicate = Nested(Or);
                    Expect("]");
                    node = Count(new Filter(node, predicate));
                }
                else
                {
                    return node;
                }
            }
        }

        private Node Primary()
        {
            var token = Peek;
            switch (token.Kind)
            {
                case TokenKind.Number:
                    _at++;
                    return Count(new Literal(token.Number));
                case TokenKind.String:
                    _at++;
                    return Count(new Literal(token.Text));
                case TokenKind.Name:
                    _at++;
                    return token.Text switch
                    {
                        "true" => Count(new Literal(true)),
                        "false" => Count(new Literal(false)),
                        "null" => Count(new Literal(JsonNull.Instance)),
                        "and" or "or" or "in" => throw new RuleExpressionSyntaxException($"'{token.Text}' needs a value before it.", token.Position),
                        _ => Count(new Name(token.Text)),
                    };
                case TokenKind.Variable:
                    _at++;
                    if (IsOp("("))
                    {
                        if (!Functions.Contains(token.Text))
                        {
                            throw new RuleExpressionSyntaxException($"'${token.Text}' is not a function of the expression language ({string.Join(", ", Functions.Order(StringComparer.Ordinal).Select(f => "$" + f))}).", token.Position);
                        }

                        _at++;
                        var args = new List<Node>();
                        if (!IsOp(")"))
                        {
                            args.Add(Nested(Or));
                            while (IsOp(","))
                            {
                                _at++;
                                args.Add(Nested(Or));
                            }
                        }

                        Expect(")");
                        CheckArity(token, args.Count);
                        return Count(new Call(token.Text, args));
                    }

                    if (!known.Contains(token.Text))
                    {
                        throw new RuleExpressionSyntaxException(
                            known.Count == 0 ? $"'${token.Text}' is not a variable here." : $"'${token.Text}' is not a variable here (known: {string.Join(", ", known.Order(StringComparer.Ordinal).Select(v => "$" + v))}).",
                            token.Position);
                    }

                    UsedVariables.Add(token.Text);
                    return Count(new Variable(token.Text));
                case TokenKind.Op when token.Text == "$":
                    _at++;
                    return Count(new Context());
                case TokenKind.Op when token.Text == "$$":
                    _at++;
                    return Count(new RootRef());
                case TokenKind.Op when token.Text == "(":
                    _at++;
                    var inner = Nested(Or);
                    Expect(")");
                    return inner;
                default:
                    throw new RuleExpressionSyntaxException(token.Kind == TokenKind.End ? "The expression ends too early." : $"Unexpected '{token.Text}'.", token.Position);
            }
        }

        private static void CheckArity(Token function, int count)
        {
            var (min, max) = function.Text switch
            {
                "round" => (1, 2),
                "substring" => (2, 3),
                "contains" => (2, 2),
                _ => (1, 1),
            };
            if (count < min || count > max)
            {
                throw new RuleExpressionSyntaxException(
                    min == max ? $"'${function.Text}' takes {min} argument{(min == 1 ? string.Empty : "s")}." : $"'${function.Text}' takes {min} to {max} arguments.",
                    function.Position);
            }
        }
    }

    // ------------------------------------------------------------------ evaluation

    /// <summary>
    /// One evaluation. Every piece of work is paid for in steps in proportion to the data it touches: a node evaluated, an
    /// item visited or read from an array, a node of two objects compared, a node or <see cref="ExpressionLimits.CharactersPerStep"/>
    /// characters of a string serialised or read. A string built (<c>$string</c> of an object or a list, <c>&amp;</c>)
    /// stops at <see cref="ExpressionLimits.MaxString"/> before more than that is written, and the time is checked
    /// against <see cref="ExpressionLimits.MaxTimePerEvaluation"/> and the budget's as the steps go.
    /// </summary>
    private sealed class Evaluation(IReadOnlyDictionary<string, JsonNode?>? variables, ExpressionBudget? budget, long start)
    {
        /// <summary>The steps between two looks at the clock.</summary>
        private const int StepsPerTimeCheck = 64;

        private long _steps;
        private long _nextTimeCheck = StepsPerTimeCheck;

        public object? Root { get; private set; }

        public void SetRoot(JsonNode? root) => Root = Load(root);

        private void Tick(long count = 1)
        {
            _steps += count;
            if (_steps > ExpressionLimits.MaxSteps)
            {
                throw new RuleExpressionBudgetException($"The expression took more than {ExpressionLimits.MaxSteps} steps.");
            }

            budget?.Take((int)count);
            if (_steps >= _nextTimeCheck)
            {
                _nextTimeCheck = _steps + StepsPerTimeCheck;
                var running = Stopwatch.GetElapsedTime(start);
                if (running > ExpressionLimits.MaxTimePerEvaluation)
                {
                    throw new RuleExpressionBudgetException($"The expression ran longer than {ExpressionLimits.MaxTimePerEvaluation.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s.");
                }

                budget?.CheckTime(running);
            }
        }

        /// <summary>The steps of reading or building <paramref name="length"/> characters.</summary>
        private void TickText(int length)
        {
            if (length >= ExpressionLimits.CharactersPerStep)
            {
                Tick(length / ExpressionLimits.CharactersPerStep);
            }
        }

        /// <summary>A JSON value read into the evaluation: an array becomes a <see cref="Seq"/>, a step per item (nested arrays too).</summary>
        public object? Load(JsonNode? node)
        {
            if (node is not JsonArray array)
            {
                return Scalar(node);
            }

            Tick(array.Count);
            var items = new List<object>(array.Count);
            foreach (var item in array)
            {
                items.Add(Load(item) ?? JsonNull.Instance);
            }

            return new Seq(items);
        }

        /// <summary>The result as JSON, a step per node copied.</summary>
        public JsonNode? Export(object? value)
        {
            switch (value)
            {
                case null or JsonNull:
                    return null;
                case bool b:
                    return JsonValue.Create(b);
                case decimal d:
                    return JsonValue.Create(d);
                case string s:
                    TickText(s.Length);
                    return JsonValue.Create(s);
                case JsonObject o:
                    ChargeNodes(o);
                    return o.DeepClone();
                case Seq seq:
                    Tick(seq.Items.Count);
                    return new JsonArray([.. seq.Items.Select(Export)]);
                default:
                    return null;
            }
        }

        /// <summary>A step per node of <paramref name="node"/> (and per <see cref="ExpressionLimits.CharactersPerStep"/> characters of its strings).</summary>
        private void ChargeNodes(JsonNode? node)
        {
            Tick();
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (_, child) in obj)
                    {
                        ChargeNodes(child);
                    }

                    break;
                case JsonArray array:
                    foreach (var child in array)
                    {
                        ChargeNodes(child);
                    }

                    break;
                case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                    TickText(value.GetValue<string>().Length);
                    break;
            }
        }

        public bool Truthy(object? value)
        {
            switch (value)
            {
                case null or JsonNull:
                    return false;
                case bool b:
                    return b;
                case decimal d:
                    return d != 0;
                case string s:
                    return s.Length > 0;
                case Seq seq:
                    Tick(seq.Items.Count);
                    foreach (var item in seq.Items)
                    {
                        if (Truthy(item))
                        {
                            return true;
                        }
                    }

                    return false;
                case JsonObject:
                    return true;
                default:
                    return false;
            }
        }

        public object? Eval(Node node, object? context)
        {
            Tick();
            switch (node)
            {
                case Literal literal:
                    return literal.Value;
                case Context:
                    return context;
                case RootRef:
                    return Root;
                case Variable variable:
                    return variables is not null && variables.TryGetValue(variable.Key, out var bound) ? Load(bound) : null;
                case Name name:
                    return Member(context, name.Key);
                case Step step:
                    return Navigate(Eval(step.Left, context), step.Right);
                case Filter filter:
                    return ApplyFilter(Eval(filter.Target, context), filter.Predicate, context);
                case Unary { Op: "-" } unary:
                    return Eval(unary.Operand, context) is var operand && operand is null
                        ? null
                        : AsNumber(operand) is { } n ? -n : throw new RuleExpressionEvaluationException($"'-' needs a number, not {Describe(operand)}.");
                case Binary binary:
                    return EvalBinary(binary, context);
                case Call call:
                    return EvalCall(call, context);
                default:
                    throw new RuleExpressionEvaluationException("Unknown expression node.");
            }
        }

        private object? Member(object? value, string key)
        {
            switch (value)
            {
                case JsonObject obj:
                    return Load(obj[key]);
                case Seq seq:
                    var items = new List<object>();
                    foreach (var item in seq.Items)
                    {
                        Tick();
                        Add(items, Member(item, key));
                    }

                    return Collapse(items);
                default:
                    return null;
            }
        }

        private object? Navigate(object? left, Node right)
        {
            if (left is Seq seq)
            {
                var items = new List<object>();
                foreach (var item in seq.Items)
                {
                    Tick();
                    Add(items, Eval(right, item));
                }

                return Collapse(items);
            }

            return left is null ? null : Eval(right, left);
        }

        private object? ApplyFilter(object? target, Node predicate, object? context)
        {
            if (target is null)
            {
                return null;
            }

            var items = target is Seq seq ? seq.Items : [target];
            if (predicate is Literal { Value: decimal } || (predicate is Unary { Op: "-", Operand: Literal { Value: decimal } }))
            {
                var at = AsNumber(Eval(predicate, context))!.Value;
                var i = ClampToInt(Math.Floor(at));
                if (i < 0)
                {
                    i += items.Count;
                }

                return i >= 0 && i < items.Count ? items[i] : null;
            }

            var kept = new List<object>();
            foreach (var item in items)
            {
                Tick();
                if (Truthy(Eval(predicate, item)))
                {
                    kept.Add(item);
                }
            }

            return Collapse(kept);
        }

        private void Add(List<object> items, object? value)
        {
            switch (value)
            {
                case null:
                    return;
                case Seq inner:
                    Tick(inner.Items.Count);
                    items.AddRange(inner.Items);
                    break;
                default:
                    items.Add(value);
                    break;
            }

            if (items.Count > ExpressionLimits.MaxSequence)
            {
                throw new RuleExpressionBudgetException($"The expression built a list of more than {ExpressionLimits.MaxSequence} values.");
            }
        }

        private static object? Collapse(List<object> items) => items.Count switch
        {
            0 => null,
            1 => items[0],
            _ => new Seq(items),
        };

        private object? EvalBinary(Binary binary, object? context)
        {
            switch (binary.Op)
            {
                case "and":
                    return Truthy(Eval(binary.Left, context)) && Truthy(Eval(binary.Right, context));
                case "or":
                    return Truthy(Eval(binary.Left, context)) || Truthy(Eval(binary.Right, context));
            }

            var left = Eval(binary.Left, context);
            var right = Eval(binary.Right, context);
            switch (binary.Op)
            {
                case "+" or "-" or "*" or "/" or "%":
                    if (left is null || right is null)
                    {
                        return null;
                    }

                    var a = AsNumber(left) ?? throw new RuleExpressionEvaluationException($"'{binary.Op}' needs numbers, not {Describe(left)}.");
                    var b = AsNumber(right) ?? throw new RuleExpressionEvaluationException($"'{binary.Op}' needs numbers, not {Describe(right)}.");
                    try
                    {
                        return binary.Op switch
                        {
                            "+" => a + b,
                            "-" => a - b,
                            "*" => a * b,
                            "/" => b == 0 ? throw new RuleExpressionEvaluationException("Division by zero.") : a / b,
                            _ => b == 0 ? throw new RuleExpressionEvaluationException("Division by zero.") : a % b,
                        };
                    }
                    catch (OverflowException)
                    {
                        throw new RuleExpressionEvaluationException("A number grew too large.");
                    }

                case "&":
                    var first = Text(left);
                    var second = Text(right);
                    if (first.Length + second.Length > ExpressionLimits.MaxString)
                    {
                        throw StringTooLong();
                    }

                    TickText(first.Length + second.Length);
                    return first + second;
                case "in":
                    if (left is null || right is null)
                    {
                        return false;
                    }

                    var candidates = right is Seq set ? set.Items : [right];
                    Tick(candidates.Count);
                    var needle = Single(left);
                    foreach (var candidate in candidates)
                    {
                        if (Equal(needle, candidate))
                        {
                            return true;
                        }
                    }

                    return false;
                default:
                    return Compare(binary.Op, Single(left), Single(right));
            }
        }

        private bool Compare(string op, object? left, object? right)
        {
            if (left is null || right is null)
            {
                return false;
            }

            if (op is "=" or "!=")
            {
                var equal = Equal(left, right);
                return op == "=" ? equal : !equal;
            }

            int order;
            if (AsNumber(left) is { } a && AsNumber(right) is { } b)
            {
                order = a.CompareTo(b);
            }
            else if (left is string x && right is string y)
            {
                TickText(Math.Min(x.Length, y.Length));
                order = string.CompareOrdinal(x, y);
            }
            else
            {
                throw new RuleExpressionEvaluationException($"'{op}' compares two numbers or two strings, not {Describe(left)} and {Describe(right)}.");
            }

            return op switch
            {
                "<" => order < 0,
                "<=" => order <= 0,
                ">" => order > 0,
                _ => order >= 0,
            };
        }

        /// <summary>Whether two values are equal; two objects are compared node by node, a step per node.</summary>
        private bool Equal(object? left, object? right)
        {
            Tick();
            switch (left, right)
            {
                case (JsonNull, JsonNull):
                    return true;
                case (bool a, bool b):
                    return a == b;
                case (string a, string b):
                    TickText(Math.Min(a.Length, b.Length));
                    return string.Equals(a, b, StringComparison.Ordinal);
                case var _ when left is not string && right is not string && AsNumber(left) is { } x && AsNumber(right) is { } y:
                    return x == y;
                case (JsonObject a, JsonObject b):
                    return NodesEqual(a, b);
                default:
                    return false;
            }
        }

        private bool NodesEqual(JsonNode? left, JsonNode? right)
        {
            Tick();
            switch (left, right)
            {
                case (null, null):
                    return true;
                case (JsonObject a, JsonObject b):
                    if (a.Count != b.Count)
                    {
                        return false;
                    }

                    foreach (var (key, value) in a)
                    {
                        if (!b.TryGetPropertyValue(key, out var other) || !NodesEqual(value, other))
                        {
                            return false;
                        }
                    }

                    return true;
                case (JsonArray a, JsonArray b):
                    if (a.Count != b.Count)
                    {
                        return false;
                    }

                    for (var i = 0; i < a.Count; i++)
                    {
                        if (!NodesEqual(a[i], b[i]))
                        {
                            return false;
                        }
                    }

                    return true;
                case (JsonValue a, JsonValue b):
                    return Scalar(a) is { } x && Scalar(b) is { } y && x.GetType() == y.GetType() && Equal(x, y);
                default:
                    return false;
            }
        }

        private static RuleExpressionBudgetException StringTooLong() =>
            new($"The expression built a string of more than {ExpressionLimits.MaxString} characters.");

        /// <summary>
        /// A value as text (<c>$string</c>, <c>&amp;</c>): a string as it is; an object or a list as compact JSON, written
        /// a step per node and stopped as soon as it would pass <see cref="ExpressionLimits.MaxString"/>.
        /// </summary>
        private string Text(object? value)
        {
            switch (Single(value))
            {
                case null or JsonNull:
                    return string.Empty;
                case string s:
                    return s;
                case bool b:
                    return b ? "true" : "false";
                case decimal d:
                    return d.ToString(CultureInfo.InvariantCulture);
                case JsonObject o:
                    {
                        var text = new StringBuilder();
                        WriteNode(text, o);
                        return text.ToString();
                    }

                case Seq seq:
                    {
                        var text = new StringBuilder();
                        WriteValue(text, seq);
                        return text.ToString();
                    }

                default:
                    return string.Empty;
            }
        }

        private void Append(StringBuilder text, string part)
        {
            Tick(1 + (part.Length / ExpressionLimits.CharactersPerStep));
            if (text.Length + part.Length > ExpressionLimits.MaxString)
            {
                throw StringTooLong();
            }

            text.Append(part);
        }

        private void AppendString(StringBuilder text, string value)
        {
            // A JSON string is at least as long as its value and the two quotes: refuse a long one before escaping it.
            if (text.Length + value.Length + 2 > ExpressionLimits.MaxString)
            {
                throw StringTooLong();
            }

            Append(text, JsonSerializer.Serialize(value));
        }

        private void WriteValue(StringBuilder text, object? value)
        {
            switch (value)
            {
                case null or JsonNull:
                    Append(text, "null");
                    break;
                case bool b:
                    Append(text, b ? "true" : "false");
                    break;
                case decimal d:
                    Append(text, JsonValue.Create(d).ToJsonString());
                    break;
                case string s:
                    AppendString(text, s);
                    break;
                case JsonObject o:
                    WriteNode(text, o);
                    break;
                case Seq seq:
                    Append(text, "[");
                    for (var i = 0; i < seq.Items.Count; i++)
                    {
                        if (i > 0)
                        {
                            Append(text, ",");
                        }

                        WriteValue(text, seq.Items[i]);
                    }

                    Append(text, "]");
                    break;
            }
        }

        private void WriteNode(StringBuilder text, JsonNode? node)
        {
            switch (node)
            {
                case null:
                    Append(text, "null");
                    break;
                case JsonObject obj:
                    {
                        Append(text, "{");
                        var first = true;
                        foreach (var (key, child) in obj)
                        {
                            if (!first)
                            {
                                Append(text, ",");
                            }

                            first = false;
                            AppendString(text, key);
                            Append(text, ":");
                            WriteNode(text, child);
                        }

                        Append(text, "}");
                        break;
                    }

                case JsonArray array:
                    {
                        Append(text, "[");
                        for (var i = 0; i < array.Count; i++)
                        {
                            if (i > 0)
                            {
                                Append(text, ",");
                            }

                            WriteNode(text, array[i]);
                        }

                        Append(text, "]");
                        break;
                    }

                case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                    AppendString(text, value.GetValue<string>());
                    break;
                default:
                    Append(text, node.ToJsonString());
                    break;
            }
        }

        private object? EvalCall(Call call, object? context)
        {
            var args = call.Args.Select(a => Eval(a, context)).ToList();
            var first = args[0];
            switch (call.Function)
            {
                case "exists":
                    return first is not null;
                case "not":
                    return !Truthy(first);
                case "boolean":
                    return first is null ? null : Truthy(first);
                case "number":
                    return Single(first) switch
                    {
                        null => null,
                        string s => Chars(s) && decimal.TryParse(s.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new RuleExpressionEvaluationException($"'{(s.Length > 40 ? s[..40] + "..." : s)}' is not a number."),
                        bool b => b ? 1m : 0m,
                        var other => AsNumber(other) ?? throw new RuleExpressionEvaluationException($"$number cannot read {Describe(other)}."),
                    };
                case "string":
                    return first is null ? null : Text(first);
                case "sum" or "max" or "min" or "average":
                    {
                        var numbers = Numbers(first, call.Function);
                        if (numbers.Count == 0)
                        {
                            return call.Function == "sum" ? 0m : null;
                        }

                        try
                        {
                            return call.Function switch
                            {
                                "sum" => numbers.Sum(),
                                "max" => numbers.Max(),
                                "min" => numbers.Min(),
                                _ => numbers.Average(),
                            };
                        }
                        catch (OverflowException)
                        {
                            throw new RuleExpressionEvaluationException("A number grew too large.");
                        }
                    }

                case "count":
                    return first switch
                    {
                        null => 0m,
                        Seq seq => seq.Items.Count,
                        _ => 1m,
                    };
                case "distinct":
                    {
                        if (first is not Seq seq)
                        {
                            return first;
                        }

                        Tick(seq.Items.Count);
                        var distinct = new List<object>();
                        foreach (var item in seq.Items)
                        {
                            var seen = false;
                            foreach (var kept in distinct)
                            {
                                if (Equal(kept, item))
                                {
                                    seen = true;
                                    break;
                                }
                            }

                            if (!seen)
                            {
                                distinct.Add(item);
                            }
                        }

                        return Collapse(distinct);
                    }

                case "abs" or "floor" or "ceil" or "round":
                    {
                        if (first is null)
                        {
                            return null;
                        }

                        var number = AsNumber(first) ?? throw new RuleExpressionEvaluationException($"${call.Function} needs a number, not {Describe(first)}.");
                        if (call.Function == "round")
                        {
                            var digits = args.Count > 1 ? AsNumber(args[1]) ?? 0 : 0;
                            if (digits is < 0 or > 10)
                            {
                                throw new RuleExpressionEvaluationException("$round takes 0 to 10 digits.");
                            }

                            return Math.Round(number, (int)digits, MidpointRounding.ToEven);
                        }

                        return call.Function switch
                        {
                            "abs" => Math.Abs(number),
                            "floor" => Math.Floor(number),
                            _ => Math.Ceiling(number),
                        };
                    }

                case "length":
                    return first is null ? null : (decimal)StringArg(first, call.Function).Length;
                case "contains":
                    if (first is null)
                    {
                        return null;
                    }

                    var haystack = StringArg(first, call.Function);
                    TickText(haystack.Length);
                    return haystack.Contains(StringArg(args[1], call.Function), StringComparison.Ordinal);
                case "lowercase":
                    return first is null ? null : StringArg(first, call.Function) is var lower && Chars(lower) ? lower.ToLowerInvariant() : null;
                case "uppercase":
                    return first is null ? null : StringArg(first, call.Function) is var upper && Chars(upper) ? upper.ToUpperInvariant() : null;
                case "trim":
                    return first is null ? null : StringArg(first, call.Function) is var untrimmed && Chars(untrimmed) ? string.Join(' ', untrimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) : null;
                case "substring":
                    {
                        if (first is null)
                        {
                            return null;
                        }

                        var s = StringArg(first, call.Function);
                        var start = ClampToInt(AsNumber(args[1]) ?? 0);
                        if (start < 0)
                        {
                            start = Math.Max(0, s.Length + start);
                        }

                        start = Math.Min(start, s.Length);
                        var length = args.Count > 2 ? ClampToInt(Math.Max(0, AsNumber(args[2]) ?? 0)) : s.Length - start;
                        var part = s.Substring(start, Math.Min(length, s.Length - start));
                        TickText(part.Length);
                        return part;
                    }

                default:
                    throw new RuleExpressionEvaluationException($"'${call.Function}' is not a function.");
            }
        }

        /// <summary>Pays for reading <paramref name="text"/> (always true, to be used in a condition).</summary>
        private bool Chars(string text)
        {
            TickText(text.Length);
            return true;
        }

        private List<decimal> Numbers(object? value, string function)
        {
            var items = value switch
            {
                null => [],
                Seq seq => seq.Items,
                _ => [value],
            };
            Tick(items.Count);
            var numbers = new List<decimal>(items.Count);
            foreach (var item in items)
            {
                if (item is JsonNull)
                {
                    continue;
                }

                numbers.Add(AsNumber(item) ?? throw new RuleExpressionEvaluationException($"${function} needs numbers, not {Describe(item)}."));
            }

            return numbers;
        }

        private static string StringArg(object? value, string function) =>
            Single(value) as string ?? throw new RuleExpressionEvaluationException($"${function} needs a string, not {Describe(Single(value))}.");
    }
}
