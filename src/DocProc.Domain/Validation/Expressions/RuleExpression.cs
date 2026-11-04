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

/// <summary>An evaluation that ran out of its steps (<see cref="ExpressionLimits.MaxSteps"/>, or the rule's budget).</summary>
public sealed class RuleExpressionBudgetException(string message) : Exception(message);

/// <summary>
/// The work an expression rule may do on one document (<see cref="ExpressionLimits.MaxStepsPerRule"/>): shared by every
/// evaluation of the rule, per row or once.
/// </summary>
public sealed class ExpressionBudget(int steps = ExpressionLimits.MaxStepsPerRule)
{
    public int Remaining { get; private set; } = steps;

    internal void Take(int steps)
    {
        Remaining -= steps;
        if (Remaining < 0)
        {
            throw new RuleExpressionBudgetException("The rule's expressions ran out of their steps on this document.");
        }
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
/// depth) and per evaluation (steps, sequence and string length); there is no function that reads anything outside the
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
    public JsonNode? Evaluate(JsonNode? context, JsonNode? root = null, IReadOnlyDictionary<string, JsonNode?>? variables = null, ExpressionBudget? budget = null)
    {
        var evaluation = new Evaluation(FromJson(root ?? context), variables, budget);
        var result = evaluation.Eval(_root, FromJson(context));
        return ToJson(result);
    }

    /// <summary>The expression's result read as a predicate (JSONata's boolean cast: an absent value, false, 0, "" and an empty list are false).</summary>
    public bool Test(JsonNode? context, JsonNode? root = null, IReadOnlyDictionary<string, JsonNode?>? variables = null, ExpressionBudget? budget = null)
    {
        var evaluation = new Evaluation(FromJson(root ?? context), variables, budget);
        return Truthy(evaluation.Eval(_root, FromJson(context)));
    }

    /// <summary>The expression's result as a number (a money value's amount), or null when it is absent or not a number.</summary>
    public decimal? EvaluateNumber(JsonNode? context, JsonNode? root = null, IReadOnlyDictionary<string, JsonNode?>? variables = null, ExpressionBudget? budget = null)
    {
        var evaluation = new Evaluation(FromJson(root ?? context), variables, budget);
        var value = Single(evaluation.Eval(_root, FromJson(context)));
        return value switch
        {
            decimal number => number,
            JsonObject money when money["amount"] is JsonValue amount && amount.TryGetValue<decimal>(out var a) => a,
            _ => null,
        };
    }

    // ------------------------------------------------------------------ values
    // null is "absent" (JSONata's undefined); JsonNull is JSON null; bool, decimal, string, JsonObject and Seq are values.

    private sealed class JsonNull
    {
        public static readonly JsonNull Instance = new();
    }

    private sealed class Seq(List<object> items)
    {
        public List<object> Items { get; } = items;
    }

    private static object? FromJson(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => obj,
        JsonArray array => new Seq([.. array.Select(i => FromJson(i) ?? JsonNull.Instance)]),
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

    private static JsonNode? ToJson(object? value) => value switch
    {
        null or JsonNull => null,
        bool b => JsonValue.Create(b),
        decimal d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        JsonObject o => o.DeepClone(),
        Seq seq => new JsonArray([.. seq.Items.Select(ToJson)]),
        _ => null,
    };

    private static object? Single(object? value) => value is Seq { Items.Count: 1 } seq ? seq.Items[0] : value;

    private static bool Truthy(object? value) => value switch
    {
        null or JsonNull => false,
        bool b => b,
        decimal d => d != 0,
        string s => s.Length > 0,
        Seq seq => seq.Items.Any(Truthy),
        JsonObject => true,
        _ => false,
    };

    private static decimal? AsNumber(object? value) => Single(value) switch
    {
        decimal d => d,
        JsonObject money when money["amount"] is { } amount && FromJson(amount) is decimal a => a,
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

    private sealed class Evaluation(object? root, IReadOnlyDictionary<string, JsonNode?>? variables, ExpressionBudget? budget)
    {
        private int _steps;

        private void Tick(int count = 1)
        {
            _steps += count;
            if (_steps > ExpressionLimits.MaxSteps)
            {
                throw new RuleExpressionBudgetException($"The expression took more than {ExpressionLimits.MaxSteps} steps.");
            }

            budget?.Take(count);
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
                    return root;
                case Variable variable:
                    return variables is not null && variables.TryGetValue(variable.Key, out var bound) ? FromJson(bound) : null;
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
                    return FromJson(obj[key]);
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
                var i = (int)Math.Floor(at);
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

        private static void Add(List<object> items, object? value)
        {
            switch (value)
            {
                case null:
                    return;
                case Seq inner:
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
                    var text = Text(left) + Text(right);
                    return text.Length > ExpressionLimits.MaxString
                        ? throw new RuleExpressionBudgetException($"The expression built a string of more than {ExpressionLimits.MaxString} characters.")
                        : text;
                case "in":
                    if (left is null || right is null)
                    {
                        return false;
                    }

                    var candidates = right is Seq set ? set.Items : [right];
                    Tick(candidates.Count);
                    return candidates.Any(c => Equal(Single(left), c));
                default:
                    return Compare(binary.Op, Single(left), Single(right));
            }
        }

        private static bool Compare(string op, object? left, object? right)
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

        private static bool Equal(object? left, object? right) => (left, right) switch
        {
            (JsonNull, JsonNull) => true,
            (bool a, bool b) => a == b,
            (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
            _ when AsNumber(left) is { } a && AsNumber(right) is { } b && left is not string && right is not string => a == b,
            (JsonObject a, JsonObject b) => JsonNode.DeepEquals(a, b),
            _ => false,
        };

        private static string Text(object? value) => Single(value) switch
        {
            null or JsonNull => string.Empty,
            string s => s,
            bool b => b ? "true" : "false",
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            JsonObject o => o.ToJsonString(),
            Seq seq => new JsonArray([.. seq.Items.Select(ToJson)]).ToJsonString(),
            _ => string.Empty,
        };

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
                        string s => decimal.TryParse(s.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new RuleExpressionEvaluationException($"'{s}' is not a number."),
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
                            if (!distinct.Any(d => Equal(d, item)))
                            {
                                Tick(distinct.Count);
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
                    return first is null ? null : StringArg(first, call.Function).Contains(StringArg(args[1], call.Function), StringComparison.Ordinal);
                case "lowercase":
                    return first is null ? null : StringArg(first, call.Function).ToLowerInvariant();
                case "uppercase":
                    return first is null ? null : StringArg(first, call.Function).ToUpperInvariant();
                case "trim":
                    return first is null ? null : string.Join(' ', StringArg(first, call.Function).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                case "substring":
                    {
                        if (first is null)
                        {
                            return null;
                        }

                        var s = StringArg(first, call.Function);
                        var start = (int)(AsNumber(args[1]) ?? 0);
                        if (start < 0)
                        {
                            start = Math.Max(0, s.Length + start);
                        }

                        start = Math.Min(start, s.Length);
                        var length = args.Count > 2 ? (int)Math.Max(0, AsNumber(args[2]) ?? 0) : s.Length - start;
                        return s.Substring(start, Math.Min(length, s.Length - start));
                    }

                default:
                    throw new RuleExpressionEvaluationException($"'${call.Function}' is not a function.");
            }
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
