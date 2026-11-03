using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Outputs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocProc.Infrastructure.Outputs;

/// <summary>Settings of <see cref="JsonataRenderer"/> (section <c>Outputs:Renderer</c>).</summary>
public sealed class JsonataRendererOptions
{
    public const string SectionName = "Outputs:Renderer";

    /// <summary>Renderer processes kept; also the renders that run at once (default: the processor count, at most 4).</summary>
    public int PoolSize { get; set; } = Math.Clamp(Environment.ProcessorCount, 1, 4);

    /// <summary>
    /// The time limit of one render (default <see cref="RenderLimits.Time"/>): processor time of the renderer process
    /// from reading the request to its answer, so a busy host does not fail renders that fit.
    /// </summary>
    public TimeSpan TimeLimit { get; set; } = RenderLimits.Time;

    /// <summary>The wall-clock ceiling of one render however little processor time it gets (default five times <see cref="TimeLimit"/>'s default, 10 s).</summary>
    public TimeSpan WallClockLimit { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long sending a request and the renderer reading it may take (outside the render's time limit, which starts
    /// once the request is read): a large values view, a 10,000-row price list, is tens of megabytes.
    /// </summary>
    public TimeSpan TransferTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a new renderer process may take to start (outside the render's time limit).</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The managed heap a renderer process may use (default <see cref="RenderLimits.HeapHardLimitBytes"/>).</summary>
    public long HeapHardLimitBytes { get; set; } = RenderLimits.HeapHardLimitBytes;

    /// <summary>The renderer's assembly; default <c>DocProc.Outputs.Renderer.dll</c> next to the host's.</summary>
    public string? RendererPath { get; set; }

    /// <summary>The <c>dotnet</c> host that runs it; default the host of the running runtime.</summary>
    public string? DotnetHostPath { get; set; }
}

/// <summary>
/// <see cref="IOutputRenderer"/> over a pool of renderer child processes (<c>src/DocProc.Outputs.Renderer</c>,
/// docs/11 §13.1). Jsonata.Net.Native cannot cancel a running evaluation, and an exponential expression runs for hours
/// (the P3 spike), so every render runs in a process of its own: the host writes the request as one line, waits for the
/// answer at most <see cref="JsonataRendererOptions.TimeLimit"/> and kills the process when it does not come; the
/// process's GC heap is capped (<c>DOTNET_GCHeapHardLimit</c>), the output by <see cref="RenderLimits.MaxOutputBytes"/>,
/// the depth by the library's caps. The child starts with an empty environment (no connection strings or keys).
/// A process serves one render at a time and is reused; a killed or crashed one is replaced on the next render.
/// </summary>
public sealed class JsonataRenderer : IOutputRenderer, IDisposable
{
    private const string RendererAssembly = "DocProc.Outputs.Renderer.dll";

    /// <summary>What the renderer writes once it has read a request (<c>RenderEngine.StartedLine</c>).</summary>
    private const string StartedLine = """{"started":true}""";

    private readonly JsonataRendererOptions _options;
    private readonly ILogger<JsonataRenderer> _logger;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentBag<RendererProcess> _idle = [];
    private long _nextId;
    private bool _disposed;

    public JsonataRenderer(IOptions<JsonataRendererOptions> options, ILogger<JsonataRenderer>? logger = null)
    {
        _options = options.Value;
        _logger = logger ?? NullLogger<JsonataRenderer>.Instance;
        _slots = new SemaphoreSlim(Math.Max(1, _options.PoolSize));
    }

    public async Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.Expression) is { } refused)
        {
            return RenderResult.Failed(refused, TimeSpan.Zero);
        }

        // The large parts (the values view, the field envelopes) are written as text after the small ones, not cloned
        // into one tree.
        var line = new JsonObject
        {
            ["expression"] = request.Expression,
            ["today"] = request.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["maxOutputBytes"] = RenderLimits.MaxOutputBytes,
            ["maxDepth"] = RenderLimits.MaxDepth,
            ["maxTailRecursion"] = RenderLimits.MaxTailRecursion,
        };
        if (request.TargetSchema is not null)
        {
            line["targetSchema"] = request.TargetSchema.DeepClone();
        }

        if (request.Lookups.Count > 0)
        {
            var lookups = new JsonObject();
            foreach (var table in request.Lookups)
            {
                lookups[table.Key] = new JsonObject
                {
                    ["caseSensitive"] = table.CaseSensitive,
                    ["entries"] = new JsonArray([.. table.Entries.Select(e => (JsonNode)new JsonObject
                    {
                        ["from"] = e.From,
                        ["to"] = e.To,
                        ["validFrom"] = e.ValidFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["validTo"] = e.ValidTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    })]),
                };
            }

            line["lookups"] = lookups;
        }

        var watch = Stopwatch.StartNew();
        var large = new List<(string, JsonNode)>();
        if (request.Input is not null)
        {
            large.Add(("input", request.Input));
        }

        if (request.Fields is not null)
        {
            large.Add(("fields", request.Fields));
        }

        var answer = await SendAsync(line, large, cancellationToken).ConfigureAwait(false);
        watch.Stop();
        return answer switch
        {
            { Error: { } error } => RenderResult.Failed(error, watch.Elapsed),
            { Response: { } response } => ToResult(response, watch.Elapsed),
            _ => RenderResult.Failed(new RenderError(RenderErrorKinds.Unavailable, null, "The renderer gave no answer."), watch.Elapsed),
        };
    }

    public async Task<ExpressionCheck> CheckAsync(string expression, JsonObject? targetSchema, CancellationToken cancellationToken)
    {
        if (Refuse(expression) is { } refused)
        {
            return new ExpressionCheck(refused, [], []);
        }

        var line = new JsonObject { ["expression"] = expression, ["check"] = true };
        if (targetSchema is not null)
        {
            line["targetSchema"] = targetSchema.DeepClone();
        }

        var answer = await SendAsync(line, [], cancellationToken).ConfigureAwait(false);
        if (answer.Error is { } error)
        {
            return new ExpressionCheck(error, [], []);
        }

        var result = ToResult(answer.Response!, TimeSpan.Zero);
        return new ExpressionCheck(result.Error, result.ReferencedPaths, result.Tables);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        while (_idle.TryTake(out var process))
        {
            process.Dispose();
        }

        _slots.Dispose();
    }

    private static RenderError? Refuse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return new RenderError(RenderErrorKinds.Request, null, "The expression is empty.");
        }

        return expression.Length > RenderLimits.MaxExpressionLength
            ? new RenderError(RenderErrorKinds.Request, null, $"The expression has more than {RenderLimits.MaxExpressionLength} characters.")
            : null;
    }

    private static RenderResult ToResult(JsonObject response, TimeSpan elapsed)
    {
        var paths = Strings(response["referencedPaths"]);
        var tables = Strings(response["tables"]);
        if (response["ok"]?.GetValue<bool>() != true)
        {
            var error = response["error"] as JsonObject;
            return RenderResult.Failed(
                new RenderError(
                    error?["kind"]?.GetValue<string>() ?? RenderErrorKinds.Runtime,
                    error?["code"]?.GetValue<string>(),
                    error?["message"]?.GetValue<string>() ?? "The render failed.",
                    error?["position"]?.GetValue<int>()),
                elapsed,
                paths,
                tables);
        }

        var undefined = response["undefined"]?.GetValue<bool>() == true;
        var output = undefined ? null : response["output"];
        response.Remove("output");
        IReadOnlyList<TargetSchemaError> schemaErrors = response["schemaErrors"] is JsonArray errors
            ? [.. errors.OfType<JsonObject>().Select(e => new TargetSchemaError(
                e["path"]?.GetValue<string>() ?? string.Empty,
                e["location"]?.GetValue<string>() ?? string.Empty,
                e["keyword"]?.GetValue<string>() ?? string.Empty,
                e["message"]?.GetValue<string>() ?? string.Empty))]
            : [];
        return new RenderResult(output, undefined, response["outputBytes"]?.GetValue<int>() ?? 0, null, schemaErrors, paths, tables, elapsed)
        {
            OutputText = output?.ToJsonString(),
            RenderTime = TimeSpan.FromMilliseconds(response["elapsedMs"]?.GetValue<long>() ?? 0),
        };
    }

    private static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray array ? [.. array.Select(n => n?.GetValue<string>()).OfType<string>()] : [];

    private async Task<Answer> SendAsync(JsonObject request, IReadOnlyList<(string Name, JsonNode Value)> large, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = Interlocked.Increment(ref _nextId);
        request["id"] = id;
        var head = request.ToJsonString();
        var builder = new StringBuilder(head, 0, head.Length - 1, head.Length + 1024);
        foreach (var (name, value) in large)
        {
            builder.Append(",\"").Append(name).Append("\":").Append(value.ToJsonString());
        }

        var text = builder.Append('}').ToString();

        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        RendererProcess? process = null;
        try
        {
            if (!_idle.TryTake(out process) || process.HasExited)
            {
                process?.Dispose();
                try
                {
                    process = await RendererProcess.StartAsync(Start(), _options.StartTimeout, _logger, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException or FileNotFoundException)
                {
                    _logger.LogError(e, "The output renderer process could not be started.");
                    return new Answer(null, new RenderError(RenderErrorKinds.Unavailable, null, "The output renderer could not be started: " + e.Message));
                }
            }

            // The request travels and is read under the transfer timeout; the time limit starts when the renderer says
            // it has read it (RenderEngine.StartedLine), so it bounds the tenant's expression, not the size of the view.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(_options.TransferTimeout);
            string? line;
            var started = false;
            try
            {
                await process.Input.WriteLineAsync(text.AsMemory(), limit.Token).ConfigureAwait(false);
                await process.Input.FlushAsync(limit.Token).ConfigureAwait(false);
                line = await process.Output.ReadLineAsync(limit.Token).ConfigureAwait(false);
                if (line == StartedLine)
                {
                    started = true;
                    limit.CancelAfter(_options.WallClockLimit);
                    line = await ReadWithinCpuLimitAsync(process, limit.Token).ConfigureAwait(false);
                }
            }
            catch (TimeoutException)
            {
                process.Dispose();
                process = null;
                return new Answer(null, new RenderError(RenderErrorKinds.TimeLimit, "TIME", $"The render used more than {_options.TimeLimit.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s of processor time and was stopped."));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Dispose();
                process = null;
                return new Answer(null, started
                    ? new RenderError(RenderErrorKinds.TimeLimit, "TIME", $"The render took longer than {_options.WallClockLimit.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s and was stopped.")
                    : new RenderError(RenderErrorKinds.Unavailable, "TRANSFER", $"The renderer did not read the request within {_options.TransferTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s."));
            }
            catch (OperationCanceledException)
            {
                process.Dispose();
                process = null;
                throw;
            }
            catch (IOException e)
            {
                process.Dispose();
                process = null;
                return new Answer(null, new RenderError(RenderErrorKinds.Unavailable, null, "The output renderer stopped: " + e.Message));
            }

            if (line is null)
            {
                process.Dispose();
                process = null;
                return new Answer(null, new RenderError(RenderErrorKinds.Unavailable, null, "The output renderer stopped without an answer."));
            }

            JsonObject response;
            try
            {
                response = JsonNode.Parse(line, documentOptions: new JsonDocumentOptions { MaxDepth = 512 }) as JsonObject ?? throw new JsonException("not an object");
            }
            catch (JsonException e)
            {
                process.Dispose();
                process = null;
                return new Answer(null, new RenderError(RenderErrorKinds.Unavailable, null, "The output renderer gave an answer that is not JSON: " + e.Message));
            }

            if (response["error"]?["kind"]?.GetValue<string>() == RenderErrorKinds.MemoryLimit)
            {
                // The process exits after an out-of-memory answer.
                process.Dispose();
                process = null;
            }

            return new Answer(response, null);
        }
        finally
        {
            if (process is not null)
            {
                if (_disposed)
                {
                    process.Dispose();
                }
                else
                {
                    _idle.Add(process);
                }
            }

            if (!_disposed)
            {
                _slots.Release();
            }
        }
    }

    /// <summary>
    /// The answer line, while the renderer's processor time since it read the request stays within
    /// <see cref="JsonataRendererOptions.TimeLimit"/> (checked every <see cref="CpuPollInterval"/>; a
    /// <see cref="TimeoutException"/> once it passes). Processor time, not wall-clock time, so a render does not fail
    /// because the host is busy with other work; the wall-clock ceiling is <paramref name="cancellationToken"/>'s.
    /// </summary>
    private async Task<string?> ReadWithinCpuLimitAsync(RendererProcess process, CancellationToken cancellationToken)
    {
        var start = process.ProcessorTime;
        var read = process.Output.ReadLineAsync(cancellationToken).AsTask();
        while (true)
        {
            var finished = await Task.WhenAny(read, Task.Delay(CpuPollInterval, cancellationToken)).ConfigureAwait(false);
            if (finished == read)
            {
                return await read.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (process.ProcessorTime - start > _options.TimeLimit)
            {
                throw new TimeoutException();
            }
        }
    }

    private static readonly TimeSpan CpuPollInterval = TimeSpan.FromMilliseconds(25);

    private ProcessStartInfo Start()
    {
        var renderer = _options.RendererPath ?? Path.Combine(AppContext.BaseDirectory, RendererAssembly);
        if (!File.Exists(renderer))
        {
            throw new FileNotFoundException($"The renderer assembly is not at '{renderer}'.", renderer);
        }

        var start = new ProcessStartInfo(_options.DotnetHostPath ?? DotnetHost())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(renderer)!,
        };
        start.ArgumentList.Add(renderer);
        // Nothing of the host's environment (connection strings, keys) reaches the renderer.
        start.Environment.Clear();
        start.Environment["DOTNET_GCHeapHardLimit"] = "0x" + _options.HeapHardLimitBytes.ToString("X", CultureInfo.InvariantCulture);
        start.Environment["DOTNET_gcServer"] = "0";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root)
        {
            start.Environment["DOTNET_ROOT"] = root;
        }

        return start;
    }

    /// <summary>The <c>dotnet</c> host of the running runtime: the host process itself, or the one three folders above the runtime directory.</summary>
    private static string DotnetHost()
    {
        var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        if (Environment.ProcessPath is { } path && string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        // .../dotnet/shared/Microsoft.NETCore.App/<version>/
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var candidate = runtime.Parent?.Parent?.Parent is { } rootDirectory ? Path.Combine(rootDirectory.FullName, name) : null;
        if (candidate is not null && File.Exists(candidate))
        {
            return candidate;
        }

        return Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath ? hostPath : name;
    }

    private sealed record Answer(JsonObject? Response, RenderError? Error);

    /// <summary>One renderer child process with its two streams.</summary>
    private sealed class RendererProcess : IDisposable
    {
        private readonly Process _process;
        private bool _disposed;

        private RendererProcess(Process process)
        {
            _process = process;
            Input = process.StandardInput;
            Output = process.StandardOutput;
        }

        public StreamWriter Input { get; }

        public StreamReader Output { get; }

        /// <summary>The process's processor time so far (every thread); zero once it is gone.</summary>
        public TimeSpan ProcessorTime
        {
            get
            {
                try
                {
                    _process.Refresh();
                    return _process.TotalProcessorTime;
                }
                catch (InvalidOperationException)
                {
                    return TimeSpan.Zero;
                }
            }
        }

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public static async Task<RendererProcess> StartAsync(ProcessStartInfo start, TimeSpan timeout, ILogger logger, CancellationToken cancellationToken)
        {
            var process = new Process { StartInfo = start, EnableRaisingEvents = false };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("The renderer process did not start.");
            }

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    logger.LogWarning("Output renderer: {Line}", e.Data);
                }
            };
            process.BeginErrorReadLine();
            var renderer = new RendererProcess(process);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(timeout);
            try
            {
                var ready = await renderer.Output.ReadLineAsync(wait.Token).ConfigureAwait(false);
                if (ready is null || !ready.Contains("\"ready\":true", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The renderer process did not report ready.");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                renderer.Dispose();
                throw new TimeoutException("The renderer process did not start in time.");
            }
            catch
            {
                renderer.Dispose();
                throw;
            }

            return renderer;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Exited in between.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Could not be killed (already gone).
            }

            _process.Dispose();
        }
    }
}
