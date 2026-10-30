using System.Text.Json.Nodes;
using DocProc.Llm.Gateway;
using DocProc.Llm.Providers;
using DocProc.Llm.Schema;
using Microsoft.Extensions.Time.Testing;

namespace DocProc.Llm.Tests.Providers;

public class ReplayProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "docproc-replay-" + Guid.NewGuid().ToString("N"));

    private static LlmRequest Request(string task = "Tenant: Gasthaus zur Post") => new()
    {
        Stage = LlmStage.Extract,
        Model = "claude-opus-5-5",
        Prompt = new PromptBundle { Id = "extract-invoice", Version = "1", System = "You extract invoices.", Task = task },
        OutputSchema = CanonicalSchemas.InvoiceExtraction,
        Document = new DocumentInput { Pdf = new byte[] { 0x25, 0x50, 0x44, 0x46 } },
    };

    private static LlmResult Result(string json = "{\"kind\":\"Invoice\"}") => new()
    {
        Json = json,
        Usage = new LlmUsage(1_000, 200, CacheReadTokens: 50, CacheWriteTokens: 10, ReasoningTokens: 5),
        Model = "claude-opus-5-5",
        StopReason = StopReasons.EndTurn,
        ProviderRequestId = "req_42",
        Latency = TimeSpan.FromMilliseconds(1234.5),
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void File_store_round_trips_a_result_with_a_readable_header()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));
        var store = new FileReplayStore(_directory, clock);
        var request = Request();
        var hash = RequestHasher.Hash(request);

        store.Put(hash, request, Result());

        var path = Path.Combine(_directory, hash + ".json");
        File.Exists(path).ShouldBeTrue();
        var file = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        file["header"]!["stage"]!.GetValue<string>().ShouldBe("Extract");
        file["header"]!["promptVersion"]!.GetValue<string>().ShouldBe("extract-invoice@1");
        file["header"]!["schema"]!.GetValue<string>().ShouldBe("invoice_extraction@1");
        file["header"]!["taskTextHash"]!.GetValue<string>().Length.ShouldBe(64);
        file["header"]!["recordedAt"]!.GetValue<DateTimeOffset>().ShouldBe(clock.GetUtcNow());
        file["model"]!.GetValue<string>().ShouldBe("claude-opus-5-5");
        file["stopReason"]!.GetValue<string>().ShouldBe("end_turn");
        file["usage"]!["cacheReadTokens"]!.GetValue<int>().ShouldBe(50);
        file["json"]!["kind"]!.GetValue<string>().ShouldBe("Invoice");
        File.ReadAllText(path).ShouldNotContain("Gasthaus zur Post", customMessage: "fixtures never carry prompt content");

        // A fresh store instance reads from disk.
        new FileReplayStore(_directory).TryGet(hash, out var replayed).ShouldBeTrue();
        replayed.ShouldBe(Result());
        new FileReplayStore(_directory).TryGet(RequestHasher.Hash(Request("other")), out _).ShouldBeFalse();
    }

    [Fact]
    public void File_store_keeps_non_json_payloads_and_rejects_bad_hashes()
    {
        var store = new FileReplayStore(_directory);
        var truncated = Result("{\"kind\":\"Inv") with { StopReason = StopReasons.MaxTokens };

        store.Put("abc123", truncated);

        new FileReplayStore(_directory).TryGet("ABC123", out var replayed).ShouldBeTrue();
        replayed.Json.ShouldBe("{\"kind\":\"Inv");
        replayed.IsTruncated.ShouldBeTrue();
        Should.Throw<ArgumentException>(() => store.TryGet("../etc/passwd", out _));
    }

    [Fact]
    public async Task Replay_provider_serves_file_fixtures_and_misses_throw()
    {
        var store = new FileReplayStore(_directory);
        var request = Request();
        store.Put(RequestHasher.Hash(request), request, Result());
        var provider = new ReplayLlmProvider(store);

        (await provider.CompleteAsync(request, CancellationToken.None)).Json.ShouldBe("{\"kind\":\"Invoice\"}");
        var ex = await Should.ThrowAsync<LlmException>(() => provider.CompleteAsync(Request("other"), CancellationToken.None));
        ex.Kind.ShouldBe(LlmErrorKind.InvalidRequest);
    }

    [Fact]
    public async Task Recording_provider_calls_the_real_provider_once_per_request_and_writes_the_fixture()
    {
        var store = new FileReplayStore(_directory);
        var inner = new ScriptedLlmProvider().Enqueue(Result());
        var recording = new RecordingLlmProvider(inner, store);
        var request = Request();

        var first = await recording.CompleteAsync(request, CancellationToken.None);
        var second = await recording.CompleteAsync(request, CancellationToken.None);

        first.ShouldBe(Result());
        second.ShouldBe(first);
        inner.Requests.Count.ShouldBe(1, "the second call is served from the fixture");
        File.Exists(Path.Combine(_directory, RequestHasher.Hash(request) + ".json")).ShouldBeTrue();
        recording.Kind.ShouldBe(LlmProviderKind.Replay);
        recording.Capabilities.ShouldBe(inner.Capabilities);
    }

    [Fact]
    public async Task Recording_provider_records_completed_batches()
    {
        var store = new FileReplayStore(_directory);
        var inner = new ScriptedLlmProvider().Enqueue(Result()).Enqueue(Result("{\"kind\":\"CreditNote\"}"));
        var recording = new RecordingLlmProvider(inner, store);
        var requests = new[] { Request("a"), Request("b") };

        var handle = await recording.SubmitBatchAsync(requests, CancellationToken.None);
        var batch = await recording.GetBatchAsync(handle, CancellationToken.None);

        batch.Status.ShouldBe(BatchStatus.Completed);
        store.TryGet(RequestHasher.Hash(requests[1]), out var replayed).ShouldBeTrue();
        replayed.Json.ShouldBe("{\"kind\":\"CreditNote\"}");
    }

    [Fact]
    public void Recording_factory_wraps_whatever_adapter_supports_the_kind()
    {
        var store = new FileReplayStore(_directory);
        var scripted = new ScriptedLlmProvider();
        var factory = new RecordingLlmProviderFactory([new ScriptedLlmProviderFactory(scripted)], store);
        var config = new LlmProviderConfig { Id = Guid.NewGuid(), Scope = ConfigScope.Platform, Kind = LlmProviderKind.Replay, Auth = new LlmAuth(LlmAuthType.ApiKey, "ref") };

        factory.Supports(LlmProviderKind.Replay).ShouldBeTrue();
        factory.Supports(LlmProviderKind.OpenAi).ShouldBeFalse();
        factory.Create(config, LlmCredential.None).ShouldBeOfType<RecordingLlmProvider>().Inner.ShouldBeSameAs(scripted);
        Should.Throw<LlmException>(() => factory.Create(config with { Kind = LlmProviderKind.OpenAi }, LlmCredential.None)).Kind.ShouldBe(LlmErrorKind.Unavailable);
    }

    [Fact]
    public async Task Scripted_provider_answers_in_order_regardless_of_hash_then_runs_dry()
    {
        var provider = new ScriptedLlmProvider()
            .Enqueue("{\"a\":1}")
            .Enqueue(Result("{\"b\":2}"))
            .Enqueue(new LlmException(LlmErrorKind.RateLimited, "slow down", TimeSpan.FromSeconds(2)));

        var first = await provider.CompleteAsync(Request("x"), CancellationToken.None);
        var second = await provider.CompleteAsync(Request("y"), CancellationToken.None);
        var third = await Should.ThrowAsync<LlmException>(() => provider.CompleteAsync(Request("z"), CancellationToken.None));
        var dry = await Should.ThrowAsync<LlmException>(() => provider.CompleteAsync(Request("z"), CancellationToken.None));

        first.Json.ShouldBe("{\"a\":1}");
        first.Model.ShouldBe("claude-opus-5-5", "a scripted json answer echoes the requested model");
        second.Json.ShouldBe("{\"b\":2}");
        third.Kind.ShouldBe(LlmErrorKind.RateLimited);
        dry.Kind.ShouldBe(LlmErrorKind.InvalidRequest);
        dry.Message.ShouldContain("no answer left for request 4");
        provider.Requests.Select(r => r.Prompt.Task).ShouldBe(["x", "y", "z", "z"]);
        provider.Remaining.ShouldBe(0);
    }
}
