using System.Diagnostics;
using System.Globalization;
using System.Text;
using Jsonata.Net.Native;
using Jsonata.Net.Native.Json;

namespace DocProc.Outputs.Renderer;

/// <summary>
/// One render request to one response, both a single line of JSON (the protocol between the hosts'
/// <c>JsonataRenderer</c> and this process, docs/11 §13.1).
/// <para>
/// Request: <c>{"id", "expression", "input", "fields"?, "today", "lookups"? {key: {"caseSensitive", "entries":
/// [{"from", "to", "validFrom"?, "validTo"?}]}}, "maxOutputBytes", "maxOutputDepth", "maxDepth", "maxTailRecursion",
/// "check"?}</c>; <c>check: true</c> compiles and scans the expression without running it.
/// </para>
/// <para>
/// Response: <c>{"id", "ok": true, "undefined", "output", "outputBytes", "referencedPaths", "tables", "elapsedMs"}</c>
/// or <c>{"id", "ok": false, "error": {"kind", "code", "message", "position"}, "referencedPaths", "tables"}</c>, where
/// <c>kind</c> is <c>syntax</c>, <c>runtime</c>, <c>required</c>, <c>function</c>, <c>depthLimit</c>, <c>sizeLimit</c>,
/// <c>memoryLimit</c> or <c>request</c>. The time limit is the host's: it kills this process. An error message is at
/// most <see cref="MaxMessageLength"/> characters (<c>$error</c> of a long text is cut), and an output nests at most
/// <c>maxOutputDepth</c> levels (default <see cref="DefaultMaxOutputDepth"/>), so every answer is one the host can read.
/// </para>
/// </summary>
public static class RenderEngine
{
    /// <summary>
    /// The line the process writes once it has read a request, before it compiles and runs the tenant's expression:
    /// the host's time limit starts there, so reading a large values view is not counted against the render.
    /// </summary>
    public const string StartedLine = """{"started":true}""";

    /// <summary>The longest error message an answer carries (the host's <c>RenderLimits.MaxErrorMessageLength</c>).</summary>
    public const int MaxMessageLength = 2000;

    /// <summary>The output depth when the request names none (the host's <c>RenderLimits.MaxOutputDepth</c>).</summary>
    public const int DefaultMaxOutputDepth = 48;

    /// <summary>The longest path or message of one target-schema error.</summary>
    public const int MaxSchemaErrorTextLength = 500;

    /// <summary>One request line to its response line; <paramref name="started"/> runs once the request is read.</summary>
    public static string Handle(string line, Action? started = null)
    {
        long id = 0;
        JObject request;
        try
        {
            request = JToken.Parse(line) as JObject ?? throw new FormatException("The request is not a JSON object.");
            id = request.Properties.TryGetValue("id", out var idToken) && idToken.Type == JTokenType.Integer ? (long)idToken : 0;
        }
        catch (Exception e) when (e is FormatException or JsonParseException)
        {
            return Error(id, "request", null, e.Message, null, null);
        }

        started?.Invoke();

        var watch = Stopwatch.StartNew();
        var expression = StringOf(request, "expression");
        if (expression is null)
        {
            return Error(id, "request", null, "The request has no expression.", null, null);
        }

        JsonataQuery query;
        ReferencedPaths.Scan scan;
        try
        {
            query = new JsonataQuery(expression);
            scan = ReferencedPaths.Of(query.GetAst());
        }
        catch (JsonataException e)
        {
            return Error(id, "syntax", e.Code.ToString(), e.RawMessage, e.Position, null);
        }

        Json.Schema.JsonSchema? schema = null;
        if (request.Properties.TryGetValue("targetSchema", out var schemaToken) && schemaToken.Type is not (JTokenType.Null or JTokenType.Undefined))
        {
            try
            {
                schema = TargetSchemaCheck.Build(schemaToken.ToFlatString());
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or ArgumentException or InvalidOperationException or Json.Schema.JsonSchemaException or Json.Schema.RefResolutionException)
            {
                return Error(id, "schema", "SCHEMA_INVALID", "The target schema is not a JSON Schema: " + e.Message, null, scan);
            }
        }

        if (request.Properties.TryGetValue("check", out var check) && check.Type == JTokenType.Boolean && (bool)check)
        {
            return Success(id, null, 0, scan, watch.ElapsedMilliseconds, null);
        }

        RenderContext context;
        try
        {
            context = new RenderContext(
                DateOnly.ParseExact(StringOf(request, "today") ?? throw new FormatException("The request has no today."), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Lookups(request),
                request.Properties.TryGetValue("fields", out var fields) ? fields as JObject : null);
        }
        catch (FormatException e)
        {
            return Error(id, "request", null, e.Message, null, scan);
        }

        var input = request.Properties.TryGetValue("input", out var data) ? data : JValue.CreateUndefined();
        var maxOutput = IntOf(request, "maxOutputBytes", 5 * 1024 * 1024);
        var maxOutputDepth = IntOf(request, "maxOutputDepth", DefaultMaxOutputDepth);
        var environment = RenderFunctions.CreateEnvironment(IntOf(request, "maxDepth", 250), IntOf(request, "maxTailRecursion", 10_000));
        JToken result;
        using (RenderFunctions.Use(context))
        {
            try
            {
                result = query.Eval(input, environment);
            }
            catch (Exception e) when (Find<RequiredValueException>(e) is { } missing)
            {
                return Error(id, "required", "REQUIRED", missing.Message, null, scan);
            }
            catch (Exception e) when (Find<RenderFunctionException>(e) is { } misuse)
            {
                return Error(id, "function", "FUNCTION", misuse.Message, null, scan);
            }
            catch (JsonataException e)
            {
                // U1001 is the library's depth cap ("Stack overflow error") and tail-recursion cap.
                var kind = e.Code.ToString() == "U1001" ? "depthLimit" : "runtime";
                return Error(id, kind, e.Code.ToString(), e.RawMessage, e.Position, scan);
            }
        }

        if (result.Type == JTokenType.Undefined)
        {
            return Success(id, null, 0, scan, watch.ElapsedMilliseconds, schema is null ? null : TargetSchemaCheck.Check(schema, "null"));
        }

        // Before serialising: a value built by iteration ($reduce) can nest far deeper than the evaluation depth cap,
        // which neither the hosts (System.Text.Json reads 64 levels) nor the recursive serialiser should meet.
        if (NestsDeeperThan(result, maxOutputDepth))
        {
            return Error(id, "depthLimit", "OUTPUT_DEPTH", $"The output nests more than {maxOutputDepth} levels deep (objects and arrays).", null, scan);
        }

        var text = result.ToFlatString(new SerializationSettings { SerializeNullProperties = true });
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > maxOutput)
        {
            return Error(id, "sizeLimit", "SIZE", $"The output has {bytes} bytes, more than the limit of {maxOutput}.", null, scan);
        }

        return Success(id, text, bytes, scan, watch.ElapsedMilliseconds, schema is null ? null : TargetSchemaCheck.Check(schema, text));
    }

    /// <summary>
    /// The stack of the thread that serves the requests: 256 MB of address space, used only as deep as a render
    /// recurses. The host refuses expressions nesting brackets deeper than its limit, so this is for the shapes it does
    /// not count (long operator chains), deep enough that the library's own caps answer before the stack runs out.
    /// </summary>
    public const int StackBytes = 256 * 1024 * 1024;

    /// <summary>An error answer outside a request's own handling (the request's id is not known there).</summary>
    public static string Failure(string kind, string code, string message) => Error(0, kind, code, message, null, null);

    /// <summary>The answer for a render that ran out of memory: the process exits after writing it.</summary>
    public static string MemoryLimit(string? line) =>
        Error(0, "memoryLimit", "MEMORY", "The render needed more memory than the renderer may use.", null, null);

    /// <summary>
    /// Whether <paramref name="root"/> has objects or arrays nested more than <paramref name="max"/> levels deep,
    /// without recursion. A value can share a container in several places (<c>{"a": $x, "b": $x}</c>), so each
    /// container is walked again only when it is reached at a greater depth than before.
    /// </summary>
    internal static bool NestsDeeperThan(JToken root, int max)
    {
        var deepest = new Dictionary<JToken, int>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(JToken Token, int Depth)>();
        pending.Push((root, 0));
        while (pending.TryPop(out var item))
        {
            IEnumerable<JToken> children;
            switch (item.Token)
            {
                case JObject o:
                    children = o.Properties.Values;
                    break;
                case JArray a:
                    children = a.ChildrenTokens;
                    break;
                default:
                    continue;
            }

            var depth = item.Depth + 1;
            if (depth > max)
            {
                return true;
            }

            if (deepest.TryGetValue(item.Token, out var seen) && seen >= depth)
            {
                continue;
            }

            deepest[item.Token] = depth;
            foreach (var child in children)
            {
                if (child is JObject or JArray)
                {
                    pending.Push((child, depth));
                }
            }
        }

        return false;
    }

    private static Dictionary<string, LookupTableData> Lookups(JObject request)
    {
        var result = new Dictionary<string, LookupTableData>(StringComparer.Ordinal);
        if (!request.Properties.TryGetValue("lookups", out var lookups) || lookups is not JObject tables)
        {
            return result;
        }

        foreach (var (key, value) in tables.Properties)
        {
            if (value is not JObject table)
            {
                continue;
            }

            var caseSensitive = table.Properties.TryGetValue("caseSensitive", out var cs) && cs.Type == JTokenType.Boolean && (bool)cs;
            var entries = new List<LookupEntryData>();
            if (table.Properties.TryGetValue("entries", out var list) && list is JArray rows)
            {
                foreach (var row in rows.ChildrenTokens.OfType<JObject>())
                {
                    if (StringOf(row, "from") is { } from && StringOf(row, "to") is { } to)
                    {
                        entries.Add(new LookupEntryData(from, to, DateOf(row, "validFrom"), DateOf(row, "validTo")));
                    }
                }
            }

            result[key] = new LookupTableData(caseSensitive, entries);
        }

        return result;
    }

    private static T? Find<T>(Exception e)
        where T : Exception
    {
        for (Exception? current = e; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static string? StringOf(JObject o, string name) =>
        o.Properties.TryGetValue(name, out var value) && value.Type == JTokenType.String ? (string)value : null;

    private static DateOnly? DateOf(JObject o, string name) =>
        StringOf(o, name) is { } text ? DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    private static int IntOf(JObject o, string name, int fallback) =>
        o.Properties.TryGetValue(name, out var value) && value.Type == JTokenType.Integer ? (int)value : fallback;

    private static string Success(long id, string? output, int bytes, ReferencedPaths.Scan scan, long elapsedMs, IReadOnlyList<TargetSchemaCheck.SchemaError>? schemaErrors)
    {
        var builder = new StringBuilder((output?.Length ?? 0) + 256);
        builder.Append("{\"id\":").Append(id.ToString(CultureInfo.InvariantCulture))
            .Append(",\"ok\":true,\"undefined\":").Append(output is null ? "true" : "false")
            .Append(",\"output\":").Append(output ?? "null")
            .Append(",\"outputBytes\":").Append(bytes.ToString(CultureInfo.InvariantCulture))
            .Append(",\"elapsedMs\":").Append(elapsedMs.ToString(CultureInfo.InvariantCulture))
            .Append(",\"schemaChecked\":").Append(schemaErrors is null ? "false" : "true")
            .Append(",\"schemaErrors\":[");
        for (var i = 0; i < (schemaErrors?.Count ?? 0); i++)
        {
            var error = schemaErrors![i];
            builder.Append(i > 0 ? ",{\"path\":" : "{\"path\":");
            AppendString(builder, Shorten(error.Path, MaxSchemaErrorTextLength));
            builder.Append(",\"location\":");
            AppendString(builder, Shorten(error.Location, MaxSchemaErrorTextLength));
            builder.Append(",\"keyword\":");
            AppendString(builder, Shorten(error.Keyword, MaxSchemaErrorTextLength));
            builder.Append(",\"message\":");
            AppendString(builder, Shorten(error.Message, MaxSchemaErrorTextLength));
            builder.Append('}');
        }

        builder.Append(']');
        AppendScan(builder, scan);
        return builder.Append('}').ToString();
    }

    private static string Error(long id, string kind, string? code, string message, int? position, ReferencedPaths.Scan? scan)
    {
        var builder = new StringBuilder(256);
        builder.Append("{\"id\":").Append(id.ToString(CultureInfo.InvariantCulture)).Append(",\"ok\":false,\"error\":{\"kind\":");
        AppendString(builder, kind);
        builder.Append(",\"code\":");
        if (code is null)
        {
            builder.Append("null");
        }
        else
        {
            AppendString(builder, code);
        }

        builder.Append(",\"message\":");
        AppendString(builder, Shorten(message, MaxMessageLength));
        builder.Append(",\"position\":").Append(position is { } p && p >= 0 ? p.ToString(CultureInfo.InvariantCulture) : "null").Append('}');
        if (scan is not null)
        {
            AppendScan(builder, scan);
        }

        return builder.Append('}').ToString();
    }

    private static void AppendScan(StringBuilder builder, ReferencedPaths.Scan scan)
    {
        builder.Append(",\"referencedPaths\":[");
        for (var i = 0; i < scan.Paths.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            AppendString(builder, scan.Paths[i]);
        }

        builder.Append("],\"tables\":[");
        for (var i = 0; i < scan.Tables.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            AppendString(builder, scan.Tables[i]);
        }

        builder.Append(']');
    }

    /// <summary>At most <paramref name="max"/> characters, cut with an ellipsis (never inside a surrogate pair).</summary>
    internal static string Shorten(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var keep = max - 1;
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return string.Concat(text.AsSpan(0, keep), "\u2026");
    }

    private static void AppendString(StringBuilder builder, string value)
    {
        builder.Append('"');
        JToken.EscapeString(value, builder);
        builder.Append('"');
    }
}
