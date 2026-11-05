using System.Text.Json.Nodes;

namespace DocProc.Application.Outputs;

/// <summary>The limits of one render (docs/11 §13.1).</summary>
public static class RenderLimits
{
    /// <summary>Wall-clock time of one render in the renderer process; it is killed when the render takes longer.</summary>
    public static readonly TimeSpan Time = TimeSpan.FromSeconds(2);

    /// <summary>The largest output, in UTF-8 bytes.</summary>
    public const int MaxOutputBytes = 5 * 1024 * 1024;

    /// <summary>The evaluation depth (JSONata's own recursion cap: nested calls and nested expressions).</summary>
    public const int MaxDepth = 250;

    /// <summary>The iterations of a tail-recursive function.</summary>
    public const int MaxTailRecursion = 10_000;

    /// <summary>The longest expression a mapping version holds.</summary>
    public const int MaxExpressionLength = 256 * 1024;

    /// <summary>The largest target JSON Schema a mapping version holds, in characters.</summary>
    public const int MaxTargetSchemaLength = 256 * 1024;

    /// <summary>The managed heap the renderer process may use (its <c>DOTNET_GCHeapHardLimit</c>).</summary>
    public const long HeapHardLimitBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// How deeply an output may nest (objects and arrays; a scalar is 0, <c>{}</c> is 1). The renderer refuses a deeper
    /// one as <see cref="RenderErrorKinds.DepthLimit"/>: the hosts read a payload with System.Text.Json's default depth
    /// of 64, and the API embeds it a few levels down in its answers (the preview, the pull feed).
    /// </summary>
    public const int MaxOutputDepth = 48;

    /// <summary>
    /// How deeply an expression may nest brackets, parentheses and braces, and the longest run of unary minus signs:
    /// a deeper one is refused before it reaches the renderer, whose parser would run out of stack.
    /// </summary>
    public const int MaxExpressionNesting = 1000;

    /// <summary>The longest render error message kept (the renderer's <c>$error</c> text and the like); longer ones are cut with an ellipsis.</summary>
    public const int MaxErrorMessageLength = 2000;

    /// <summary>The most lookup entries one render receives, over all the tables it names.</summary>
    public const int MaxLookupEntriesPerRender = 100_000;

    /// <summary>The most characters of lookup entries (from, to) one render receives, over all the tables it names.</summary>
    public const long MaxLookupCharactersPerRender = 16L * 1024 * 1024;

    /// <summary>A message of at most <see cref="MaxErrorMessageLength"/> characters, cut with an ellipsis.</summary>
    public static string Shorten(string message, int max = MaxErrorMessageLength)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Length <= max)
        {
            return message;
        }

        var keep = max - 1;
        if (keep > 0 && char.IsHighSurrogate(message[keep - 1]))
        {
            keep--;
        }

        return string.Concat(message.AsSpan(0, keep), "\u2026");
    }
}

/// <summary>What went wrong in a render, by kind (each kind is a permanent failure but <see cref="Unavailable"/>, docs/11 §13.6).</summary>
public static class RenderErrorKinds
{
    /// <summary>The expression does not parse.</summary>
    public const string Syntax = "syntax";

    /// <summary>An evaluation error of JSONata (a type error, <c>$error</c>, <c>$assert</c>).</summary>
    public const string Runtime = "runtime";

    /// <summary><c>$required</c> found no value.</summary>
    public const string Required = "required";

    /// <summary>A custom function used wrongly (an unknown lookup table, a date that is not one).</summary>
    public const string Function = "function";

    /// <summary>The target schema is not a JSON Schema.</summary>
    public const string Schema = "schema";

    /// <summary>The render took longer than <see cref="RenderLimits.Time"/>.</summary>
    public const string TimeLimit = "timeLimit";

    /// <summary>The output is larger than <see cref="RenderLimits.MaxOutputBytes"/>.</summary>
    public const string SizeLimit = "sizeLimit";

    /// <summary>The evaluation passed <see cref="RenderLimits.MaxDepth"/> or the tail-recursion cap.</summary>
    public const string DepthLimit = "depthLimit";

    /// <summary>The renderer process ran out of its heap.</summary>
    public const string MemoryLimit = "memoryLimit";

    /// <summary>The renderer could not be started or stopped answering for another reason: retryable.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>The request was refused before it reached the renderer (an empty expression, an over-long one).</summary>
    public const string Request = "request";
}

/// <summary>One render error: its kind (<see cref="RenderErrorKinds"/>), the engine's code, a message and the position in the expression.</summary>
public sealed record RenderError(string Kind, string? Code, string Message, int? Position = null)
{
    /// <summary>A delivery that fails with this error is not retried (docs/11 §13.6).</summary>
    public bool IsPermanent => Kind != RenderErrorKinds.Unavailable;
}

/// <summary>One error of the output against the mapping's target schema: the output path (<c>prices[379].price</c>), its JSON Pointer, the keyword and the message.</summary>
public sealed record TargetSchemaError(string Path, string Location, string Keyword, string Message);

/// <summary>One entry of a lookup table as a render reads it.</summary>
public sealed record LookupEntrySnapshot(string From, string To, DateOnly? ValidFrom = null, DateOnly? ValidTo = null);

/// <summary>A lookup table as a render reads it (docs/11 §6.1): <c>$table(key, value, default?)</c> reads these.</summary>
public sealed record LookupTableSnapshot(string Key, bool CaseSensitive, IReadOnlyList<LookupEntrySnapshot> Entries);

/// <summary>
/// One render: a JSONata expression over a values view (docs/11 §5.6), with the lookup tables it may read, the
/// field envelopes <c>$field(path)</c> answers from (<see cref="Fields"/>: path to <c>{status, confidence, method,
/// normalized}</c>), today in the tenant's time zone (<c>$today()</c>, the day lookup validity is read on) and
/// optionally the target JSON Schema the output is checked against.
/// </summary>
public sealed record RenderRequest(string Expression, JsonNode? Input, DateOnly Today)
{
    public IReadOnlyList<LookupTableSnapshot> Lookups { get; init; } = [];

    public JsonObject? Fields { get; init; }

    public JsonObject? TargetSchema { get; init; }
}

/// <summary>
/// What a render produced: the output (null with <see cref="Undefined"/> when the expression yields nothing), its
/// size, the error, the target-schema errors (empty when it fits or no schema was given), the values-view paths and the
/// lookup tables the expression names, and how long it took.
/// </summary>
public sealed record RenderResult(
    JsonNode? Output,
    bool Undefined,
    int OutputBytes,
    RenderError? Error,
    IReadOnlyList<TargetSchemaError> SchemaErrors,
    IReadOnlyList<string> ReferencedPaths,
    IReadOnlyList<string> Tables,
    TimeSpan Elapsed)
{
    /// <summary>Rendered and, when a schema was given, fits it.</summary>
    public bool Succeeded => Error is null && SchemaErrors.Count == 0;

    /// <summary>The output as the renderer wrote it (numbers as written), for the payload snapshot; null when there is none.</summary>
    public string? OutputText { get; init; }

    /// <summary>How long the renderer took from reading the request to its answer (compile, run, serialise, schema check): the part the time limit bounds.</summary>
    public TimeSpan RenderTime { get; init; }

    public static RenderResult Failed(RenderError error, TimeSpan elapsed, IReadOnlyList<string>? paths = null, IReadOnlyList<string>? tables = null) =>
        new(null, false, 0, error, [], paths ?? [], tables ?? [], elapsed);
}

/// <summary>The result of checking an expression (and a target schema) without running it: the error, if any, and what it reads.</summary>
public sealed record ExpressionCheck(RenderError? Error, IReadOnlyList<string> ReferencedPaths, IReadOnlyList<string> Tables);

/// <summary>
/// The sandboxed JSONata renderer (docs/11 §13.1): Jsonata.Net.Native in a child process the host kills at
/// <see cref="RenderLimits.Time"/>, bounded by <see cref="RenderLimits"/>, with the custom functions <c>$table</c>,
/// <c>$fmtDate</c>, <c>$fmtNum</c>, <c>$today</c>, <c>$field</c> and <c>$required</c>, and no I/O.
/// </summary>
public interface IOutputRenderer
{
    Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken);

    /// <summary>Parses the expression (and builds the target schema) without running it.</summary>
    Task<ExpressionCheck> CheckAsync(string expression, JsonObject? targetSchema, CancellationToken cancellationToken);
}
