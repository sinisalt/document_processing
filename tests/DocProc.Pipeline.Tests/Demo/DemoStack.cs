using System.Collections.Concurrent;
using DocProc.Application.Pipeline;
using DocProc.Infrastructure.Persistence;
using DocProc.Llm;
using DocProc.Llm.Providers;
using DocProc.Pipeline.Demo;
using DocProc.Pipeline.Queue;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocProc.Pipeline.Tests.Demo;

/// <summary>
/// The processing stack composed like the API in development mode (docs/09 "Demo data"): the API's own
/// <c>appsettings.json</c> and <c>appsettings.Development.json</c>, the demo data switched on, with only the database,
/// blob and replay folders moved into a work directory. Restarting it on the same work directory is a restart of
/// <c>dotnet run</c> on the same <c>data/</c> folder.
/// </summary>
internal static class DemoStack
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <param name="work">Holds the database, the blobs and an empty replay folder; reuse it to restart on the same data.</param>
    /// <param name="provider">Registered ahead of every other adapter, so it answers the Replay kind; null keeps the real replay store (empty).</param>
    /// <param name="configure">Further settings, e.g. <c>Persistence:DemoDocumentsPath</c> or <c>Pipeline:RunInProcess</c>.</param>
    /// <param name="logs">Captures every log line of the host when given.</param>
    /// <param name="services">Registrations made before the stack's own, which therefore win over its TryAdd ones.</param>
    public static async Task<IHost> StartAsync(
        string work,
        ILlmProviderFactory? provider = null,
        Action<Dictionary<string, string?>>? configure = null,
        LogSink? logs = null,
        Action<IServiceCollection>? services = null)
    {
        var api = Path.Combine(DemoFixtureTests.RepositoryRoot(), "src", "DocProc.Api");
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Testing",
            ContentRootPath = api,
            ApplicationName = "DocProc.Pipeline.Tests",
        });
        var settings = new Dictionary<string, string?>
        {
            ["Persistence:SqlitePath"] = Path.Combine(work, "docproc.db"),
            ["Blobs:RootPath"] = Path.Combine(work, "blobs"),
            // A replay store must be configured for the demo data to add the Replay provider, but it must not be the
            // committed folder: the tests' provider answers instead, and a stale fixture must not hide a change.
            ["Llm:Replay:Directory"] = Path.Combine(work, "empty-replay"),
            ["Pipeline:RetryBaseDelay"] = "00:00:00.050",
            ["Pipeline:MaxAttempts"] = "2",
        };
        configure?.Invoke(settings);
        builder.Configuration
            .AddJsonFile(Path.Combine(api, "appsettings.json"), optional: false)
            .AddJsonFile(Path.Combine(api, "appsettings.Development.json"), optional: false)
            .AddInMemoryCollection(settings);
        builder.Services.AddLogging(logging =>
        {
            if (logs is not null)
            {
                logging.AddProvider(logs);
            }
        });
        if (provider is not null)
        {
            // Registered first: the gateway takes the first factory that supports the Replay kind.
            builder.Services.AddSingleton(provider);
        }

        services?.Invoke(builder.Services);

        builder.Services.TryAddDocProcPersistence(builder.Configuration, builder.Environment, migrateByDefault: false).ShouldBeTrue();
        builder.AddDocProcProcessingStack(runInProcessDefault: true);
        builder.AddDocProcDemoData();

        var host = builder.Build();
        await host.StartAsync(Ct);
        return host;
    }

    /// <summary>A copy of the committed demo documents (tenant folders only), optionally restricted to some file names.</summary>
    public static string CopyDemoDocuments(string target, params string[] only)
    {
        var source = Path.Combine(DemoFixtureTests.RepositoryRoot(), "fixtures", "demo");
        foreach (var slug in new[] { "tenant-a", "tenant-b" })
        {
            var folder = Directory.CreateDirectory(Path.Combine(target, slug)).FullName;
            foreach (var file in DemoDataHostedService.FilesFor(source, slug))
            {
                if (only.Length == 0 || only.Contains(Path.GetFileName(file), StringComparer.Ordinal))
                {
                    File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
                }
            }
        }

        return target;
    }
}

/// <summary>
/// Answers every request from <see cref="DemoAnswers"/>, whatever its hash, and records it; optionally holds the
/// requests of one stage until the host shuts down, to leave a document in the middle of the pipeline.
/// </summary>
internal sealed class DemoAnswersProvider : ILlmProviderFactory, ILlmProvider
{
    private readonly ConcurrentQueue<LlmRequest> _requests = new();

    /// <summary>Requests of this stage wait for the caller's token instead of being answered.</summary>
    public LlmStage? Hold { get; init; }

    /// <summary>Completed when the first held request arrived.</summary>
    public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<LlmRequest> Requests => _requests.ToList();

    public LlmProviderKind Kind => LlmProviderKind.Replay;

    public LlmCapabilities Capabilities => ReplayLlmProvider.ReplayCapabilities;

    public bool Supports(LlmProviderKind kind) => kind == LlmProviderKind.Replay;

    public ILlmProvider Create(LlmProviderConfig config, LlmCredential credential) => this;

    public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(request);
        if (request.Stage == Hold)
        {
            Held.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        return DemoAnswers.For(request);
    }

    public Task<BatchHandle> SubmitBatchAsync(IReadOnlyList<LlmRequest> requests, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The demo data never batches.");

    public Task<BatchResult> GetBatchAsync(BatchHandle handle, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The demo data never batches.");

    public Task ValidateCredentialAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The in-process queue as the slowest possible start-up sees it: queuing an ingestion waits until the runner has
/// unpacked it, and the document messages are held back until <see cref="OpenAsync"/>. Whatever the demo data does
/// after queuing an ingestion therefore finds that ingestion's documents, still Received, as an interrupted run would
/// have left them.
/// </summary>
internal sealed class GatedQueue : IPipelineQueue
{
    private readonly Lock _gate = new();
    private readonly List<ProcessDocument> _held = [];
    private InProcessPipelineQueue? _inner;
    private bool _open;

    private InProcessPipelineQueue Inner => _inner ?? throw new InvalidOperationException("Not attached to a host yet.");

    /// <summary>Wraps the host's queue; registered as <see cref="IPipelineQueue"/> through a factory.</summary>
    public GatedQueue Attach(InProcessPipelineQueue inner)
    {
        _inner = inner;
        return this;
    }

    public async Task EnqueueAsync(ProcessIngestion message, CancellationToken cancellationToken)
    {
        await Inner.EnqueueAsync(message, cancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        await Inner.WaitForIdleAsync(cts.Token);
    }

    public Task EnqueueAsync(ProcessDocument message, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_open)
            {
                _held.Add(message);
                return Task.CompletedTask;
            }
        }

        return Inner.EnqueueAsync(message, cancellationToken);
    }

    public Task EnqueueAsync(ProcessDocument message, TimeSpan delay, CancellationToken cancellationToken) => Inner.EnqueueAsync(message, delay, cancellationToken);

    /// <summary>Lets the held document messages through, and every later one directly.</summary>
    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        List<ProcessDocument> held;
        lock (_gate)
        {
            _open = true;
            held = [.. _held];
            _held.Clear();
        }

        foreach (var message in held)
        {
            await Inner.EnqueueAsync(message, cancellationToken);
        }
    }
}

/// <summary>Every log line of a host, formatted, for assertions on what the demo data reported.</summary>
internal sealed class LogSink : ILoggerProvider
{
    private readonly ConcurrentQueue<(LogLevel Level, string Category, string Message)> _lines = new();

    public IReadOnlyList<(LogLevel Level, string Category, string Message)> Lines => _lines.ToList();

    /// <summary>The lines the demo data hosted service wrote.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Demo =>
        Lines.Where(l => l.Category == typeof(DemoDataHostedService).FullName).Select(l => (l.Level, l.Message)).ToList();

    public void Clear() => _lines.Clear();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(LogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                sink._lines.Enqueue((logLevel, category, formatter(state, exception)));
            }
        }
    }
}
