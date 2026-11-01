using System.Text;
using DocProc.Llm.Schema;
using OpenAI.Chat;

namespace DocProc.Llm.OpenAi;

/// <summary>
/// Pure translation from the provider-neutral request to the chat-completions shape (docs/08 "What stays
/// provider-specific"): the stable prefix (instructions + examples) as the first system message so automatic prefix
/// caching applies, the PDF as a base64 file part (page images plus the text layer as fallback), structured output via
/// a strict <c>json_schema</c> response format carrying the canonical schema unchanged, and normalised effort.
/// </summary>
public static class OpenAiRequestBuilder
{
    public const string PdfMediaType = "application/pdf";
    public const string PngMediaType = "image/png";

    public static OpenAiChatRequest Build(LlmRequest request, LlmProviderKind kind, LlmCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capabilities);

        var content = new List<ChatMessageContentPart>();
        AppendDocument(request.Document, capabilities, content);
        content.Add(ChatMessageContentPart.CreateTextPart(request.Prompt.Task));

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(RenderSystem(request, kind, capabilities)),
            new UserChatMessage(content),
        };

        var options = new ChatCompletionOptions
        {
            // max_completion_tokens on the wire; the factory's OpenAI-compatible client renames it to max_tokens (LegacyMaxTokensPolicy).
            MaxOutputTokenCount = request.Options.MaxOutputTokens,
            ReasoningEffortLevel = MapEffort(request.Options.Effort),
            ResponseFormat = BuildResponseFormat(request.OutputSchema, capabilities),
        };

        // LlmRequest.DocumentId is correlation for usage rows and logs only; it is never sent to the provider (no
        // `user` field: OpenAI expects a stable end-user id there, and Ollama rejects the field outright).
        return new OpenAiChatRequest(request.Model, messages, options) { Timeout = request.Options.Timeout };
    }

    /// <summary><see cref="ReasoningEffort.None"/> omits the parameter; the others map one to one (docs/08 "Reasoning/effort").</summary>
    public static ChatReasoningEffortLevel? MapEffort(ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.None => (ChatReasoningEffortLevel?)null,
        ReasoningEffort.Low => ChatReasoningEffortLevel.Low,
        ReasoningEffort.Medium => ChatReasoningEffortLevel.Medium,
        ReasoningEffort.High => ChatReasoningEffortLevel.High,
        _ => (ChatReasoningEffortLevel?)null,
    };

    /// <summary>The system prefix: vendor-specific wording when the prompt has one, then the examples rendered exactly as the Anthropic adapter does.</summary>
    public static string RenderSystem(LlmRequest request, LlmProviderKind kind, LlmCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capabilities);

        var system = new StringBuilder(request.Prompt.SystemFor(kind));
        foreach (var example in request.Prompt.Examples)
        {
            system.Append("\n\n<example>\n<input>\n").Append(example.Input).Append("\n</input>\n<output>\n").Append(example.Output).Append("\n</output>\n</example>");
        }

        if (request.OutputSchema is { } schema && !capabilities.JsonSchemaOutput)
        {
            // Servers without strict schemas get json_object mode plus the schema in the prefix.
            system.Append("\n\nRespond with a single JSON object that conforms to this JSON schema and nothing else:\n").Append(schema.Json);
        }

        return system.ToString();
    }

    private static ChatResponseFormat? BuildResponseFormat(JsonSchemaDefinition? schema, LlmCapabilities capabilities)
    {
        if (schema is null)
        {
            return null;
        }

        return capabilities.JsonSchemaOutput
            ? ChatResponseFormat.CreateJsonSchemaFormat(schema.Name, BinaryData.FromString(schema.Json), jsonSchemaFormatDescription: null, jsonSchemaIsStrict: true)
            : ChatResponseFormat.CreateJsonObjectFormat();
    }

    private static void AppendDocument(DocumentInput? document, LlmCapabilities capabilities, List<ChatMessageContentPart> content)
    {
        if (document is null)
        {
            return;
        }

        if (document.HasPdf && capabilities.NativePdfInput)
        {
            content.Add(ChatMessageContentPart.CreateFilePart(BinaryData.FromBytes(document.Pdf!.Value), PdfMediaType, document.FileName));
            return;
        }

        if (capabilities.Vision)
        {
            foreach (var image in document.PageImages)
            {
                content.Add(ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(image.Png), PngMediaType));
            }
        }

        if (document.HasText)
        {
            var pages = document.PageTexts.Select((text, i) => $"<page number=\"{i + 1}\">\n{text}\n</page>");
            content.Add(ChatMessageContentPart.CreateTextPart($"<document title=\"{document.FileName} (text layer)\">\n{string.Join('\n', pages)}\n</document>"));
        }
    }
}
