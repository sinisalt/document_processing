using System.Net;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using DocProc.Llm.Anthropic;
using DocProc.Llm.Schema;
using Microsoft.Extensions.Time.Testing;

namespace DocProc.Llm.Tests;

public class AnthropicAdapterTests
{
    private static LlmRequest Request(DocumentInput? document) => new()
    {
        Stage = LlmStage.Extract,
        Model = "claude-opus-5-5",
        Prompt = new PromptBundle
        {
            Id = "extract-invoice",
            Version = "1",
            System = "Extract the invoice.",
            Examples = [new PromptExample("Rechnung 1", "{\"invoiceNumber\":\"1\"}")],
            Task = "Tenant: Gasthaus zur Post GmbH, DE123456789.",
        },
        Document = document,
        OutputSchema = CanonicalSchemas.InvoiceExtraction,
        Options = new LlmOptions { Effort = ReasoningEffort.Medium, MaxOutputTokens = 4096 },
        DocumentId = Guid.Parse("dddddddd-0000-0000-0000-000000000001"),
    };

    private static JsonElement Body(MessageCreateParams p) =>
        JsonSerializer.SerializeToElement(p.RawBodyData);

    [Fact]
    public void Builds_cached_system_prefix_native_pdf_block_and_json_schema_output()
    {
        var request = Request(new DocumentInput { FileName = "RE-42.pdf", Pdf = new byte[] { 1, 2, 3 }, PageTexts = ["ignored when a PDF is present"] });

        var body = Body(AnthropicRequestBuilder.Build(request, LlmProviderKind.Anthropic));

        body.GetProperty("model").GetString().ShouldBe("claude-opus-5-5");
        body.GetProperty("max_tokens").GetInt32().ShouldBe(4096);

        var system = body.GetProperty("system").EnumerateArray().ToList();
        system.Count.ShouldBe(2);
        system[0].GetProperty("text").GetString().ShouldBe("Extract the invoice.");
        system[0].TryGetProperty("cache_control", out _).ShouldBeFalse();
        system[1].GetProperty("text").GetString()!.ShouldContain("<example>");
        system[1].GetProperty("cache_control").GetProperty("ttl").GetString().ShouldBe("1h");

        var content = body.GetProperty("messages")[0].GetProperty("content").EnumerateArray().ToList();
        content.Count.ShouldBe(2);
        content[0].GetProperty("type").GetString().ShouldBe("document");
        content[0].GetProperty("source").GetProperty("type").GetString().ShouldBe("base64");
        content[0].GetProperty("source").GetProperty("media_type").GetString().ShouldBe("application/pdf");
        content[0].GetProperty("source").GetProperty("data").GetString().ShouldBe(Convert.ToBase64String([1, 2, 3]));
        content[0].GetProperty("title").GetString().ShouldBe("RE-42.pdf");
        content[1].GetProperty("type").GetString().ShouldBe("text");
        content[1].GetProperty("text").GetString().ShouldStartWith("Tenant:");

        var output = body.GetProperty("output_config");
        output.GetProperty("effort").GetString().ShouldBe("medium");
        output.GetProperty("format").GetProperty("type").GetString().ShouldBe("json_schema");
        output.GetProperty("format").GetProperty("schema").GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();

        body.GetProperty("metadata").GetProperty("user_id").GetString().ShouldBe("dddddddd000000000000000000000001");
    }

    [Fact]
    public void Falls_back_to_page_images_and_text_layer_without_a_pdf()
    {
        var request = Request(new DocumentInput
        {
            PageImages = [new PageImage(1, new byte[] { 9 }, 100, 100), new PageImage(2, new byte[] { 8 }, 100, 100)],
            PageTexts = ["Rechnung Nr. 42", "Seite 2"],
        });

        var content = Body(AnthropicRequestBuilder.Build(request, LlmProviderKind.Anthropic))
            .GetProperty("messages")[0].GetProperty("content").EnumerateArray().ToList();

        content.Select(c => c.GetProperty("type").GetString()).ShouldBe(["image", "image", "document", "text"]);
        content[0].GetProperty("source").GetProperty("media_type").GetString().ShouldBe("image/png");
        var text = content[2].GetProperty("source");
        text.GetProperty("type").GetString().ShouldBe("text");
        text.GetProperty("data").GetString()!.ShouldContain("<page number=\"2\">");
    }

    [Fact]
    public void Uses_the_vendor_specific_system_prompt_when_one_exists()
    {
        var request = Request(null) with
        {
            Prompt = Request(null).Prompt with { SystemOverrides = new Dictionary<LlmProviderKind, string> { [LlmProviderKind.Anthropic] = "Claude-specific wording." } },
        };

        Body(AnthropicRequestBuilder.Build(request, LlmProviderKind.Anthropic)).GetProperty("system")[0].GetProperty("text").GetString().ShouldBe("Claude-specific wording.");
        Body(AnthropicRequestBuilder.Build(request, LlmProviderKind.AnthropicFoundry)).GetProperty("system")[0].GetProperty("text").GetString().ShouldBe("Extract the invoice.");
    }

    [Theory]
    [InlineData(ReasoningEffort.None, "low")]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    public void Effort_degrades_to_the_nearest_supported_level(ReasoningEffort effort, string expected) =>
        AnthropicRequestBuilder.MapEffort(effort).ToString().ShouldBe(expected, StringCompareShould.IgnoreCase);

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, LlmErrorKind.Auth)]
    [InlineData(HttpStatusCode.Forbidden, LlmErrorKind.Auth)]
    [InlineData(HttpStatusCode.PaymentRequired, LlmErrorKind.Quota)]
    [InlineData(HttpStatusCode.TooManyRequests, LlmErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.BadRequest, LlmErrorKind.InvalidRequest)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, LlmErrorKind.InvalidRequest)]
    [InlineData(HttpStatusCode.InternalServerError, LlmErrorKind.Transient)]
    [InlineData((HttpStatusCode)529, LlmErrorKind.Transient)]
    public void Http_errors_map_to_vendor_neutral_kinds(HttpStatusCode status, LlmErrorKind expected)
    {
        var mapped = AnthropicErrorMapper.Map(new AnthropicApiException("boom") { StatusCode = status, ResponseBody = "{}" });

        mapped.Kind.ShouldBe(expected);
        mapped.IsRetryable.ShouldBe(expected is LlmErrorKind.RateLimited or LlmErrorKind.Transient);
        if (expected == LlmErrorKind.RateLimited)
        {
            mapped.RetryAfter.ShouldNotBeNull();
        }
    }

    [Fact]
    public void Network_failures_are_transient() =>
        AnthropicErrorMapper.Map(new HttpRequestException("connection reset")).Kind.ShouldBe(LlmErrorKind.Transient);

    [Fact]
    public async Task Completes_a_message_through_the_client_and_maps_the_response()
    {
        var handler = new FakeOpenAiHandler().Enqueue(HttpStatusCode.OK, MessageJson());
        var provider = Provider(handler);

        var result = await provider.CompleteAsync(Request(null), TestContext.Current.CancellationToken);

        result.Json.ShouldBe("{\"invoiceNumber\":\"RE-42\"}");
        result.Usage.ShouldBe(new LlmUsage(InputTokens: 100, OutputTokens: 20, CacheReadTokens: 50, CacheWriteTokens: 10));
        result.Model.ShouldBe("claude-opus-5-5");
        result.StopReason.ShouldBe(StopReasons.EndTurn);
        result.ProviderRequestId.ShouldBe("msg_1");
        var sent = handler.Requests.ShouldHaveSingleItem();
        sent.Uri.AbsolutePath.ShouldBe("/v1/messages");
        sent.Body.GetProperty("model").GetString().ShouldBe("claude-opus-5-5");
    }

    [Fact]
    public async Task The_request_timeout_bounds_the_call_and_is_a_transient_error()
    {
        // The timeout runs on the provider's clock, not on the wall clock: a minute-long timeout expires the moment the
        // fake clock passes it, and a provider still bound to the system clock would hang this test for a minute.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));
        var timeout = TimeSpan.FromMinutes(1);
        var handler = new FakeOpenAiHandler().EnqueueHang();
        var provider = Provider(handler, clock);
        var request = Request(null) with { Options = new LlmOptions { Timeout = timeout } };

        var call = Should.ThrowAsync<LlmException>(() => provider.CompleteAsync(request, CancellationToken.None));
        // The fake records the request before it starts to hang, so once the hang is signalled the request is on record
        // and the clock can be moved; nothing here waits on real latency.
        await handler.HangStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        clock.Advance(timeout - TimeSpan.FromMilliseconds(1));
        call.IsCompleted.ShouldBeFalse("the timeout has not expired yet");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var error = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        error.Kind.ShouldBe(LlmErrorKind.Transient);
        error.IsRetryable.ShouldBeTrue();
        error.RetryAfter.ShouldBe(AnthropicErrorMapper.DefaultTransientDelay);
        error.Message.ShouldContain($"did not answer within {timeout}", customMessage: "the adapter's own timeout fired, not the SDK's");
        handler.Requests.Count.ShouldBe(1, "a timed-out attempt is not retried by the adapter");
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_mapped_to_a_provider_error()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var provider = Provider(new FakeOpenAiHandler().Enqueue(HttpStatusCode.OK, MessageJson()));

        await Should.ThrowAsync<OperationCanceledException>(() => provider.CompleteAsync(Request(null), cts.Token));
    }

    [Fact]
    public void The_factory_hands_its_clock_to_every_provider_and_defaults_to_the_system_clock()
    {
        var clock = new FakeTimeProvider();

        new AnthropicProviderFactory(clock).Clock.ShouldBeSameAs(clock);
        new AnthropicProviderFactory().Clock.ShouldBeSameAs(TimeProvider.System);
    }

    /// <summary>
    /// A provider on a scripted HTTP transport (the fake is a plain <see cref="HttpMessageHandler"/>; nothing in it is
    /// OpenAI-specific). The HttpClient's own 100 s timeout is switched off so only the adapter's timeout applies.
    /// </summary>
    private static AnthropicProvider Provider(FakeOpenAiHandler handler, TimeProvider? clock = null)
    {
        var client = new AnthropicClient
        {
            ApiKey = "sk-ant-test",
            MaxRetries = 0,
            Timeout = AnthropicProviderFactory.NetworkTimeout,
            HttpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
        };

        return new AnthropicProvider(client, LlmProviderKind.Anthropic, "claude-haiku-5-5", clock);
    }

    private static string MessageJson() => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["id"] = "msg_1",
        ["type"] = "message",
        ["role"] = "assistant",
        ["model"] = "claude-opus-5-5",
        ["content"] = new[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = "{\"invoiceNumber\":\"RE-42\"}" } },
        ["stop_reason"] = "end_turn",
        ["stop_sequence"] = null,
        ["usage"] = new Dictionary<string, object?>
        {
            ["input_tokens"] = 100,
            ["output_tokens"] = 20,
            ["cache_read_input_tokens"] = 50,
            ["cache_creation_input_tokens"] = 10,
        },
    });
}
