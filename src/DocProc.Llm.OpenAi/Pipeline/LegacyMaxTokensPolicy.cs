using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DocProc.Llm.OpenAi.Pipeline;

/// <summary>
/// Rewrites the output bound for self-hosted OpenAI-compatible servers. The SDK serialises
/// <see cref="OpenAI.Chat.ChatCompletionOptions.MaxOutputTokenCount"/> only as <c>max_completion_tokens</c>; Ollama
/// (and older vLLM builds) only honour the legacy <c>max_tokens</c> and would otherwise run unbounded, so for
/// <see cref="LlmProviderKind.OpenAiCompatible"/> the request body goes out with the field renamed. The SDK's own
/// mechanism for extra body fields (<c>Patch</c>) is experimental in OpenAI 2.14.0; this policy only uses stable
/// pipeline APIs.
/// </summary>
public sealed class LegacyMaxTokensPolicy : PipelinePolicy
{
    public const string ModernName = "max_completion_tokens";
    public const string LegacyName = "max_tokens";

    public static readonly LegacyMaxTokensPolicy Instance = new();

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    /// <summary>The body with <c>max_completion_tokens</c> renamed to <c>max_tokens</c>, or null when it carries no such field.</summary>
    public static BinaryData? Rewrite(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(ModernName, out _))
        {
            return null;
        }

        using var buffer = new MemoryStream(json.Length);
        // Relaxed escaping keeps non-ASCII prompt text (umlauts, euro signs) as the SDK wrote it; the body is a JSON API payload, not HTML.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals(LegacyName))
                {
                    continue; // the modern field wins when the body somehow carries both
                }

                writer.WritePropertyName(property.NameEquals(ModernName) ? LegacyName : property.Name);
                property.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return BinaryData.FromBytes(buffer.ToArray());
    }

    private static void Apply(PipelineMessage message)
    {
        if (message.Request.Content is not { } content)
        {
            return;
        }

        using var buffer = new MemoryStream();
        content.WriteTo(buffer);
        if (Rewrite(buffer.GetBuffer().AsMemory(0, (int)buffer.Length)) is { } rewritten)
        {
            message.Request.Content = BinaryContent.Create(rewritten);
        }
    }
}
