using DocProc.Application.Pipeline;
using DocProc.Pipeline.Queue;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocProc.Pipeline.Tests;

/// <summary>
/// The Development-only crash between commit and enqueue that the publish sweeper demo uses (docs/09): armed by a
/// marker file, it fires once, on a decision's publish nudge only, and never exists outside Development.
/// </summary>
public sealed class CrashBeforePublishEnqueueQueueTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "docproc-crash-queue-tests", Guid.NewGuid().ToString("N"));

    public CrashBeforePublishEnqueueQueueTests() => Directory.CreateDirectory(_directory);

    private string Marker => Path.Combine(_directory, "crash-before-publish-enqueue");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Without_the_marker_every_message_goes_to_the_inner_queue()
    {
        var inner = new RecordingQueue();
        var crashes = new List<string>();
        var queue = new CrashBeforePublishEnqueueQueue(inner, Marker, NullLogger<CrashBeforePublishEnqueueQueue>.Instance, crashes.Add);

        await queue.EnqueueAsync(Publish(), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(Publish(), TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(new ProcessIngestion(Guid.NewGuid(), Guid.NewGuid()), TestContext.Current.CancellationToken);

        crashes.ShouldBeEmpty();
        inner.Messages.Count.ShouldBe(3);
    }

    [Fact]
    public async Task With_the_marker_the_next_decision_nudge_crashes_once_before_it_is_queued()
    {
        await File.WriteAllTextAsync(Marker, string.Empty, TestContext.Current.CancellationToken);
        var inner = new RecordingQueue();
        var crashes = new List<string>();
        var queue = new CrashBeforePublishEnqueueQueue(inner, Marker, NullLogger<CrashBeforePublishEnqueueQueue>.Instance, message =>
        {
            crashes.Add(message);
            throw new CrashedException();
        });
        var nudge = Publish();

        await Should.ThrowAsync<CrashedException>(() => queue.EnqueueAsync(nudge, TestContext.Current.CancellationToken));

        crashes.ShouldHaveSingleItem().ShouldContain(nudge.DocumentId.ToString());
        inner.Messages.ShouldBeEmpty("the process ends before the message is queued");
        File.Exists(Marker).ShouldBeFalse("disarmed first, so the restarted host does not crash again");

        // After the restart: the sweeper's message and the next decision go through.
        await queue.EnqueueAsync(nudge with { Origin = PipelineMessageOrigins.PublishSweeper }, TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(Publish(), TestContext.Current.CancellationToken);
        inner.Messages.Count.ShouldBe(2);
        crashes.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(PipelineStage.Validate, null, 1)]
    [InlineData(PipelineStage.Publish, PipelineMessageOrigins.PublishSweeper, 1)]
    [InlineData(PipelineStage.Publish, null, 2)]
    public async Task Other_messages_never_trigger_it_even_when_armed(PipelineStage stage, string? origin, int attempt)
    {
        await File.WriteAllTextAsync(Marker, string.Empty, TestContext.Current.CancellationToken);
        var inner = new RecordingQueue();
        var crashes = new List<string>();
        var queue = new CrashBeforePublishEnqueueQueue(inner, Marker, NullLogger<CrashBeforePublishEnqueueQueue>.Instance, crashes.Add);

        await queue.EnqueueAsync(new ProcessDocument(Guid.NewGuid(), Guid.NewGuid(), stage, attempt, origin), TestContext.Current.CancellationToken);

        crashes.ShouldBeEmpty();
        inner.Messages.ShouldHaveSingleItem();
        File.Exists(Marker).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Development", true, true)]
    [InlineData("Development", false, false)]
    [InlineData("Production", true, false)]
    [InlineData("Testing", true, false)]
    public void It_is_registered_only_in_development_and_only_when_a_marker_is_configured(string environment, bool configured, bool expected)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment, ContentRootPath = _directory });
        var settings = new Dictionary<string, string?>
        {
            [PipelineOptions.RunInProcessKey] = "false",
            ["Secrets:Path"] = Path.Combine(_directory, "secrets"),
            ["Blobs:RootPath"] = Path.Combine(_directory, "blobs"),
        };
        if (configured)
        {
            settings[PipelineOptions.CrashBeforePublishEnqueueMarkerKey] = Marker;
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddLogging();
        builder.AddDocProcProcessingStack();

        using var provider = builder.Services.BuildServiceProvider();
        var queue = provider.GetRequiredService<IPipelineQueue>();
        (queue is CrashBeforePublishEnqueueQueue).ShouldBe(expected);
        if (!expected)
        {
            queue.ShouldBeSameAs(provider.GetRequiredService<InProcessPipelineQueue>());
        }
    }

    private static ProcessDocument Publish() => new(Guid.NewGuid(), Guid.NewGuid(), PipelineStage.Publish);

    private sealed class CrashedException : Exception;

    private sealed class RecordingQueue : IPipelineQueue
    {
        public List<object> Messages { get; } = [];

        public Task EnqueueAsync(ProcessIngestion message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task EnqueueAsync(ProcessDocument message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task EnqueueAsync(ProcessDocument message, TimeSpan delay, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
