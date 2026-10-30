using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DocProc.Application.Abstractions;

namespace DocProc.Llm.Providers;

/// <summary>A replay store that can also record: the recorder hands over the request so the fixture gets a readable header.</summary>
public interface IRecordingReplayStore : IReplayStore
{
    void Put(string requestHash, LlmRequest request, LlmResult result);
}

/// <summary>
/// One JSON file per request hash under a directory (docs/09 "LLM providers"): <c>{hash}.json</c> holds the model,
/// stop reason, usage and the answer payload, preceded by a human-readable header (stage, prompt version, task text
/// hash) so a reviewer can tell fixtures apart without recomputing hashes. Files are written indented and atomically.
/// </summary>
public sealed class FileReplayStore(string directory, TimeProvider? clock = null) : IRecordingReplayStore
{
    private static readonly JsonSerializerOptions FileOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ConcurrentDictionary<string, LlmResult> _cache = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string Directory { get; } = Path.GetFullPath(directory ?? throw new ArgumentNullException(nameof(directory)));

    public bool TryGet(string requestHash, out LlmResult result)
    {
        if (_cache.TryGetValue(requestHash, out result!))
        {
            return true;
        }

        var path = PathFor(requestHash);
        if (!File.Exists(path))
        {
            result = null!;
            return false;
        }

        var file = JsonSerializer.Deserialize<ReplayFile>(File.ReadAllText(path), FileOptions)
            ?? throw new InvalidOperationException($"Replay fixture {path} is empty.");
        result = file.ToResult();
        _cache[requestHash] = result;
        return true;
    }

    public void Put(string requestHash, LlmResult result) => Write(requestHash, null, result);

    public void Put(string requestHash, LlmRequest request, LlmResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        Write(requestHash, ReplayHeader.From(request, _clock.GetUtcNow()), result);
    }

    public string PathFor(string requestHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        if (!requestHash.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException($"'{requestHash}' is not a request hash.", nameof(requestHash));
        }

        return Path.Combine(Directory, requestHash.ToLowerInvariant() + ".json");
    }

    private void Write(string requestHash, ReplayHeader? header, LlmResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var path = PathFor(requestHash);
        System.IO.Directory.CreateDirectory(Directory);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(ReplayFile.From(header, result), FileOptions) + "\n", new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        _cache[requestHash] = result;
    }

    /// <summary>What a reviewer needs to recognise a fixture; never prompt or document content.</summary>
    private sealed record ReplayHeader(LlmStage Stage, string PromptVersion, string? Schema, string TaskTextHash, string Model, DateTimeOffset RecordedAt)
    {
        public static ReplayHeader From(LlmRequest request, DateTimeOffset at) => new(
            request.Stage,
            request.Prompt.CacheKey,
            request.OutputSchema?.ToString(),
            ContentHash.Sha256Hex(Encoding.UTF8.GetBytes(request.Prompt.Task)),
            request.Model,
            at);
    }

    private sealed record ReplayUsage(int InputTokens, int OutputTokens, int CacheReadTokens, int CacheWriteTokens, int ReasoningTokens)
    {
        public static ReplayUsage From(LlmUsage u) => new(u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens, u.ReasoningTokens);

        public LlmUsage ToUsage() => new(InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens, ReasoningTokens);
    }

    private sealed record ReplayFile(
        ReplayHeader? Header,
        string Model,
        string StopReason,
        string? ProviderRequestId,
        double LatencyMs,
        ReplayUsage Usage,
        JsonNode? Json,
        string? JsonText)
    {
        public static ReplayFile From(ReplayHeader? header, LlmResult result)
        {
            JsonNode? node = null;
            try
            {
                node = JsonNode.Parse(result.Json);
            }
            catch (JsonException)
            {
                // Not JSON (a refusal message, a truncated answer): keep it as text so the fixture still replays faithfully.
            }

            return new ReplayFile(
                header,
                result.Model,
                result.StopReason,
                result.ProviderRequestId,
                result.Latency.TotalMilliseconds,
                ReplayUsage.From(result.Usage),
                node,
                node is null ? result.Json : null);
        }

        public LlmResult ToResult() => new()
        {
            Json = Json?.ToJsonString() ?? JsonText ?? string.Empty,
            Usage = Usage.ToUsage(),
            Model = Model,
            StopReason = StopReason,
            ProviderRequestId = ProviderRequestId,
            Latency = TimeSpan.FromMilliseconds(LatencyMs),
        };
    }
}
