using System.Net;
using DocProc.Llm.OpenAi;
using DocProc.Llm.Providers;
using OpenAI.Chat;
using static DocProc.Llm.Tests.OpenAiTestSupport;

namespace DocProc.Llm.Tests;

public class OpenAiCompatibleProviderTests
{
    private static readonly LlmCredential ApiKey = new("sk-test");

    private static ChatCompletion Completion(
        ChatFinishReason finish = ChatFinishReason.Stop,
        string? content = "{\"invoiceNumber\":\"RE-42\"}",
        string? refusal = null,
        ChatTokenUsage? usage = null) =>
        OpenAIChatModelFactory.ChatCompletion(
            id: "chatcmpl-1",
            finishReason: finish,
            content: content is null ? new ChatMessageContent() : new ChatMessageContent(content),
            refusal: refusal,
            model: "gpt-5-2025-08-07",
            usage: usage ?? OpenAIChatModelFactory.ChatTokenUsage(
                inputTokenCount: 100,
                outputTokenCount: 20,
                totalTokenCount: 120,
                outputTokenDetails: OpenAIChatModelFactory.ChatOutputTokenUsageDetails(reasoningTokenCount: 12),
                inputTokenDetails: OpenAIChatModelFactory.ChatInputTokenUsageDetails(audioTokenCount: 0, cachedTokenCount: 60)));

    [Fact]
    public void Maps_a_completion_and_splits_cached_and_reasoning_tokens_out_of_the_totals()
    {
        var result = OpenAiCompatibleProvider.ToResult(Completion(), TimeSpan.FromSeconds(2));

        result.Json.ShouldBe("{\"invoiceNumber\":\"RE-42\"}");
        result.Model.ShouldBe("gpt-5-2025-08-07");
        result.ProviderRequestId.ShouldBe("chatcmpl-1");
        result.StopReason.ShouldBe(StopReasons.EndTurn);
        result.IsTruncated.ShouldBeFalse();
        result.Latency.ShouldBe(TimeSpan.FromSeconds(2));
        result.Usage.ShouldBe(new LlmUsage(InputTokens: 40, OutputTokens: 8, CacheReadTokens: 60, CacheWriteTokens: 0, ReasoningTokens: 12));
        result.Usage.TotalTokens.ShouldBe(120);
    }

    [Fact]
    public void Length_becomes_max_tokens_and_tool_calls_keep_the_vendor_value()
    {
        OpenAiCompatibleProvider.ToResult(Completion(ChatFinishReason.Length), TimeSpan.Zero).StopReason.ShouldBe(StopReasons.MaxTokens);
        OpenAiCompatibleProvider.ToResult(Completion(ChatFinishReason.Length), TimeSpan.Zero).IsTruncated.ShouldBeTrue();
        OpenAiCompatibleProvider.ToResult(Completion(ChatFinishReason.ToolCalls), TimeSpan.Zero).StopReason.ShouldBe("tool_calls");
    }

    [Fact]
    public void Content_filter_and_refusals_throw_content_refused()
    {
        Should.Throw<LlmException>(() => OpenAiCompatibleProvider.ToResult(Completion(ChatFinishReason.ContentFilter), TimeSpan.Zero))
            .Kind.ShouldBe(LlmErrorKind.ContentRefused);

        var refusal = Should.Throw<LlmException>(() => OpenAiCompatibleProvider.ToResult(Completion(content: null, refusal: "I can't help with that."), TimeSpan.Zero));
        refusal.Kind.ShouldBe(LlmErrorKind.ContentRefused);
        refusal.Message.ShouldContain("I can't help with that.");
        refusal.IsRetryable.ShouldBeFalse();
    }

    [Fact]
    public void Missing_usage_or_details_count_as_zero()
    {
        OpenAiCompatibleProvider.ToUsage(null).ShouldBe(LlmUsage.Zero);

        var bare = OpenAIChatModelFactory.ChatTokenUsage(inputTokenCount: 10, outputTokenCount: 5, totalTokenCount: 15, outputTokenDetails: null);
        OpenAiCompatibleProvider.ToUsage(bare).ShouldBe(new LlmUsage(10, 5));
    }

    [Fact]
    public async Task Sends_the_prefix_then_the_document_turn_and_returns_the_json()
    {
        var handler = new FakeOpenAiHandler().Enqueue(HttpStatusCode.OK, FakeOpenAiHandler.CompletionJson(promptTokens: 100, cachedTokens: 60, completionTokens: 20, reasoningTokens: 12));
        var provider = Provider(handler, Config(LlmProviderKind.OpenAi), ApiKey);

        var result = await provider.CompleteAsync(Request(new DocumentInput { FileName = "RE-42.pdf", Pdf = new byte[] { 1, 2, 3 } }), CancellationToken.None);

        result.Json.ShouldBe("{\"invoiceNumber\":\"RE-42\"}");
        result.Usage.ShouldBe(new LlmUsage(40, 8, CacheReadTokens: 60, ReasoningTokens: 12));
        result.Latency.ShouldBeGreaterThan(TimeSpan.Zero);

        var sent = handler.Requests.ShouldHaveSingleItem();
        sent.Uri.ShouldBe(new Uri("https://api.openai.com/v1/chat/completions"));
        sent.Headers["Authorization"].ShouldBe("Bearer sk-test");
        sent.Body.GetProperty("model").GetString().ShouldBe("gpt-5");
        var messages = sent.Body.GetProperty("messages").EnumerateArray().ToList();
        messages.Select(m => m.GetProperty("role").GetString()).ShouldBe(["system", "user"]);
        messages[1].GetProperty("content")[0].GetProperty("type").GetString().ShouldBe("file");
        sent.Body.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean().ShouldBeTrue();
        sent.Body.GetProperty("reasoning_effort").GetString().ShouldBe("medium");
        sent.Body.GetProperty("max_completion_tokens").GetInt32().ShouldBe(4096);
    }

    [Fact]
    public async Task Retries_once_without_reasoning_effort_when_the_model_rejects_it_and_remembers_the_model()
    {
        var handler = new FakeOpenAiHandler()
            .Enqueue(HttpStatusCode.BadRequest, FakeOpenAiHandler.ErrorJson("Unsupported parameter: 'reasoning_effort' is not supported with this model.", param: "reasoning_effort", code: "unsupported_parameter"))
            .EnqueueCompletion()
            .EnqueueCompletion();
        var provider = Provider(handler, Config(LlmProviderKind.OpenAi), ApiKey);

        var first = await provider.CompleteAsync(Request(null, model: "gpt-4.1"), CancellationToken.None);
        var second = await provider.CompleteAsync(Request(null, model: "gpt-4.1"), CancellationToken.None);

        first.Json.ShouldBe("{\"invoiceNumber\":\"RE-42\"}");
        second.Json.ShouldBe("{\"invoiceNumber\":\"RE-42\"}");
        handler.Requests.Count.ShouldBe(3);
        handler.Requests[0].Body.GetProperty("reasoning_effort").GetString().ShouldBe("medium");
        handler.Requests[1].Body.TryGetProperty("reasoning_effort", out _).ShouldBeFalse();
        handler.Requests[2].Body.TryGetProperty("reasoning_effort", out _).ShouldBeFalse();
        handler.Requests[1].Body.GetProperty("response_format").GetProperty("type").GetString().ShouldBe("json_schema");
    }

    [Fact]
    public async Task Does_not_retry_an_unrelated_bad_request_or_a_second_rejection()
    {
        var unrelated = new FakeOpenAiHandler().Enqueue(HttpStatusCode.BadRequest, FakeOpenAiHandler.ErrorJson("Invalid schema", param: "response_format"));
        var unrelatedError = await Should.ThrowAsync<LlmException>(() => Provider(unrelated, Config(LlmProviderKind.OpenAi), ApiKey).CompleteAsync(Request(null), CancellationToken.None));
        unrelatedError.Kind.ShouldBe(LlmErrorKind.InvalidRequest);
        unrelated.Requests.Count.ShouldBe(1);

        var twice = new FakeOpenAiHandler()
            .Enqueue(HttpStatusCode.BadRequest, FakeOpenAiHandler.ErrorJson("Unsupported parameter: 'reasoning_effort'", param: "reasoning_effort"))
            .Enqueue(HttpStatusCode.BadRequest, FakeOpenAiHandler.ErrorJson("Unsupported parameter: 'reasoning_effort'", param: "reasoning_effort"));
        var twiceError = await Should.ThrowAsync<LlmException>(() => Provider(twice, Config(LlmProviderKind.OpenAi), ApiKey).CompleteAsync(Request(null), CancellationToken.None));
        twiceError.Kind.ShouldBe(LlmErrorKind.InvalidRequest);
        twice.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Rate_limits_surface_with_the_servers_retry_after()
    {
        var handler = new FakeOpenAiHandler().Enqueue(HttpStatusCode.TooManyRequests, FakeOpenAiHandler.ErrorJson("Rate limit reached", type: "rate_limit_error"), ("Retry-After", "17"));

        var error = await Should.ThrowAsync<LlmException>(() => Provider(handler, Config(LlmProviderKind.OpenAi), ApiKey).CompleteAsync(Request(null), CancellationToken.None));

        error.Kind.ShouldBe(LlmErrorKind.RateLimited);
        error.RetryAfter.ShouldBe(TimeSpan.FromSeconds(17));
        error.IsRetryable.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(1, "the SDK's own retries are switched off; the worker owns retry policy");
    }

    [Fact]
    public async Task A_refusal_on_the_wire_is_content_refused()
    {
        var handler = new FakeOpenAiHandler().Enqueue(HttpStatusCode.OK, FakeOpenAiHandler.CompletionJson(content: null, refusal: "I can't help with that."));

        var error = await Should.ThrowAsync<LlmException>(() => Provider(handler, Config(LlmProviderKind.OpenAi), ApiKey).CompleteAsync(Request(null), CancellationToken.None));

        error.Kind.ShouldBe(LlmErrorKind.ContentRefused);
    }

    [Fact]
    public async Task Validation_is_a_one_token_completion_on_the_classify_model()
    {
        var handler = new FakeOpenAiHandler().EnqueueCompletion(content: "", finishReason: "length");
        var provider = Provider(handler, Config(LlmProviderKind.OpenAi), ApiKey);

        await provider.ValidateCredentialAsync(CancellationToken.None);

        var sent = handler.Requests.ShouldHaveSingleItem();
        sent.Body.GetProperty("model").GetString().ShouldBe("gpt-5-mini");
        sent.Body.GetProperty("max_completion_tokens").GetInt32().ShouldBe(1);
        sent.Body.GetProperty("messages")[0].GetProperty("content").GetString().ShouldBe(OpenAiCompatibleProvider.ValidationPrompt);
        sent.Body.TryGetProperty("response_format", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Validation_reports_a_bad_key_as_auth()
    {
        var handler = new FakeOpenAiHandler().Enqueue(HttpStatusCode.Unauthorized, FakeOpenAiHandler.ErrorJson("Incorrect API key provided", code: "invalid_api_key"));

        var error = await Should.ThrowAsync<LlmException>(() => Provider(handler, Config(LlmProviderKind.OpenAi), ApiKey).ValidateCredentialAsync(CancellationToken.None));

        error.Kind.ShouldBe(LlmErrorKind.Auth);
        error.BlocksTenant.ShouldBeTrue();
    }

    [Fact]
    public async Task Cancellation_is_not_mapped_to_a_provider_error()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var provider = Provider(new FakeOpenAiHandler().EnqueueCompletion(), Config(LlmProviderKind.OpenAi), ApiKey);

        await Should.ThrowAsync<OperationCanceledException>(() => provider.CompleteAsync(Request(null), cts.Token));
    }

    [Fact]
    public async Task Batch_endpoints_are_not_wired_up_yet()
    {
        var provider = Provider(new FakeOpenAiHandler(), Config(LlmProviderKind.OpenAi), ApiKey);

        provider.Capabilities.Batch.ShouldBeFalse();
        (await Should.ThrowAsync<LlmException>(() => provider.SubmitBatchAsync([Request(null)], CancellationToken.None))).Kind.ShouldBe(LlmErrorKind.InvalidRequest);
        (await Should.ThrowAsync<LlmException>(() => provider.GetBatchAsync(new BatchHandle(LlmProviderKind.OpenAi, "batch_1", DateTimeOffset.UtcNow, 1), CancellationToken.None))).Kind.ShouldBe(LlmErrorKind.InvalidRequest);
    }
}
