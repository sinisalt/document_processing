using System.ClientModel.Primitives;
using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.Json;
using DocProc.Llm.OpenAi;
using DocProc.Llm.Providers;
using DocProc.Llm.Schema;

namespace DocProc.Llm.Tests;

/// <summary>Shared fixtures for the OpenAI adapter tests: requests, configurations and a fake HTTP transport. No live calls.</summary>
internal static class OpenAiTestSupport
{
    public static readonly Guid DocumentId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");

    public static LlmRequest Request(DocumentInput? document, string model = "gpt-5") => new()
    {
        Stage = LlmStage.Extract,
        Model = model,
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
        DocumentId = DocumentId,
    };

    public static LlmProviderConfig Config(
        LlmProviderKind kind,
        Uri? endpoint = null,
        string? apiVersion = null,
        LlmAuthType auth = LlmAuthType.ApiKey,
        IReadOnlyDictionary<LlmStage, string>? models = null) => new()
    {
        Id = Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
        Scope = ConfigScope.Platform,
        Kind = kind,
        Endpoint = endpoint,
        ApiVersion = apiVersion,
        Auth = new LlmAuth(auth, SecretRef: "llm-platform-openai"),
        Models = models ?? new Dictionary<LlmStage, string>
        {
            [LlmStage.Classify] = "gpt-5-mini",
            [LlmStage.Extract] = "gpt-5",
        }.ToFrozenDictionary(),
    };

    public static ILlmProvider Provider(FakeOpenAiHandler handler, LlmProviderConfig config, LlmCredential credential) =>
        new OpenAiCompatibleProviderFactory(new HttpClientPipelineTransport(new HttpClient(handler))).Create(config, credential);

    public static JsonElement Json(BinaryData data) => JsonSerializer.Deserialize<JsonElement>(data.ToMemory().Span);

    public static JsonElement Json<T>(T model) where T : IJsonModel<T> => Json(ModelReaderWriter.Write(model));
}

/// <summary>Scripted HTTP responses for the OpenAI SDK's transport; every request is recorded for assertions.</summary>
internal sealed class FakeOpenAiHandler : HttpMessageHandler
{
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public FakeOpenAiHandler Enqueue(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
    {
        _responses.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return Task.FromResult(response);
        });
        return this;
    }

    /// <summary>A server that never answers: the request completes only when the SDK's token cancels it.</summary>
    public FakeOpenAiHandler EnqueueHang()
    {
        _responses.Enqueue(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        return this;
    }

    public FakeOpenAiHandler EnqueueCompletion(string content = "{\"invoiceNumber\":\"RE-42\"}", string finishReason = "stop") =>
        Enqueue(HttpStatusCode.OK, CompletionJson(content: content, finishReason: finishReason));

    public static string CompletionJson(
        string? content = "{\"invoiceNumber\":\"RE-42\"}",
        string finishReason = "stop",
        string model = "gpt-5-2025-08-07",
        string? refusal = null,
        int promptTokens = 100,
        int completionTokens = 20,
        int cachedTokens = 0,
        int reasoningTokens = 0)
    {
        var message = new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = content, ["refusal"] = refusal };
        var body = new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl-1",
            ["object"] = "chat.completion",
            ["created"] = 1_700_000_000,
            ["model"] = model,
            ["choices"] = new[] { new Dictionary<string, object?> { ["index"] = 0, ["message"] = message, ["finish_reason"] = finishReason } },
            ["usage"] = new Dictionary<string, object?>
            {
                ["prompt_tokens"] = promptTokens,
                ["completion_tokens"] = completionTokens,
                ["total_tokens"] = promptTokens + completionTokens,
                ["prompt_tokens_details"] = new Dictionary<string, object?> { ["cached_tokens"] = cachedTokens },
                ["completion_tokens_details"] = new Dictionary<string, object?> { ["reasoning_tokens"] = reasoningTokens },
            },
        };
        return JsonSerializer.Serialize(body);
    }

    public static string ErrorJson(string message, string type = "invalid_request_error", string? param = null, string? code = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["error"] = new Dictionary<string, object?> { ["message"] = message, ["type"] = type, ["param"] = param, ["code"] = code },
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new RecordedRequest(request.RequestUri!, headers, JsonSerializer.Deserialize<JsonElement>(body)));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException($"No scripted response for request #{Requests.Count} to {request.RequestUri}.");
        }

        return await _responses.Dequeue()(cancellationToken);
    }
}

internal sealed record RecordedRequest(Uri Uri, IReadOnlyDictionary<string, string> Headers, JsonElement Body);

/// <summary>A hand-built response so <see cref="System.ClientModel.ClientResultException"/> can be constructed without a transport.</summary>
internal sealed class FakePipelineResponse(int status, string body, params (string Name, string Value)[] headers) : PipelineResponse
{
    private readonly FakeHeaders _headers = new(headers);
    private Stream? _stream = new MemoryStream(Encoding.UTF8.GetBytes(body));

    public override int Status { get; } = status;

    public override string ReasonPhrase => ((HttpStatusCode)Status).ToString();

    public override Stream? ContentStream
    {
        get => _stream;
        set => _stream = value;
    }

    public override BinaryData Content => BinaryData.FromString(body);

    protected override PipelineResponseHeaders HeadersCore => _headers;

    public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;

    public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Content);

    public override void Dispose() => _stream?.Dispose();

    private sealed class FakeHeaders((string Name, string Value)[] headers) : PipelineResponseHeaders
    {
        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).GetEnumerator();

        public override bool TryGetValue(string name, out string? value)
        {
            value = headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
            return value is not null;
        }

        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            var matches = headers.Where(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToList();
            values = matches.Count > 0 ? matches : null;
            return values is not null;
        }
    }
}
