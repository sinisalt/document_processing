using System.Text.Json;
using Anthropic.Models.Messages;

namespace DocProc.Llm.Anthropic;

/// <summary>
/// Pure translation from the provider-neutral request to the Anthropic Messages API shape (docs/06 "Request design"):
/// cached system prefix (instructions + schema description + examples), the PDF as a native document block
/// (page images and text as fallback), structured output via JSON schema, normalised effort.
/// </summary>
public static class AnthropicRequestBuilder
{
    public const string DefaultCacheTtl = "1h";

    public static MessageCreateParams Build(LlmRequest request, LlmProviderKind kind)
    {
        ArgumentNullException.ThrowIfNull(request);

        var systemBlocks = new List<TextBlockParam> { new(request.Prompt.SystemFor(kind)) };
        foreach (var example in request.Prompt.Examples)
        {
            systemBlocks.Add(new TextBlockParam($"<example>\n<input>\n{example.Input}\n</input>\n<output>\n{example.Output}\n</output>\n</example>"));
        }

        // One cache breakpoint at the end of the stable prefix; everything before it is cached for an hour.
        systemBlocks[^1] = systemBlocks[^1] with { CacheControl = new CacheControlEphemeral { Ttl = Ttl.Ttl1h } };

        var content = new List<ContentBlockParam>();
        AppendDocument(request.Document, content);
        content.Add(new ContentBlockParam(new TextBlockParam(request.Prompt.Task)));

        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.Options.MaxOutputTokens,
            System = new MessageCreateParamsSystem(systemBlocks),
            Messages = [new MessageParam(new UserMessageParam(new MessageContentParam(content)))],
            OutputConfig = BuildOutputConfig(request),
        };

        if (request.DocumentId is { } documentId)
        {
            parameters = parameters with { Metadata = new Metadata { UserID = documentId.ToString("N") } };
        }

        return parameters;
    }

    public static Effort MapEffort(ReasoningEffort effort) => effort switch
    {
        // Thinking cannot be switched off on current models; "None" degrades to the cheapest level (docs/08 "Reasoning/effort").
        ReasoningEffort.None => Effort.Low,
        ReasoningEffort.Low => Effort.Low,
        ReasoningEffort.Medium => Effort.Medium,
        ReasoningEffort.High => Effort.High,
        _ => Effort.Low,
    };

    private static OutputConfig BuildOutputConfig(LlmRequest request)
    {
        var config = new OutputConfig { Effort = MapEffort(request.Options.Effort) };
        if (request.OutputSchema is { } schema)
        {
            var dictionary = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(schema.Json)
                ?? throw new LlmException(LlmErrorKind.InvalidRequest, $"Schema {schema} is not a JSON object.");
            config = config with { Format = new JsonOutputFormat { Schema = dictionary } };
        }

        return config;
    }

    private static void AppendDocument(DocumentInput? document, List<ContentBlockParam> content)
    {
        if (document is null)
        {
            return;
        }

        if (document.HasPdf)
        {
            var pdf = new DocumentBlockParam(new DocumentBlockParamSource(new Base64PdfSource(Convert.ToBase64String(document.Pdf!.Value.Span))))
            {
                Title = document.FileName,
            };
            content.Add(new ContentBlockParam(pdf));
            return;
        }

        foreach (var image in document.PageImages)
        {
            content.Add(new ContentBlockParam(new ImageBlockParam(new ImageBlockParamSource(new Base64ImageSource
            {
                Data = Convert.ToBase64String(image.Png.Span),
                MediaType = MediaType.ImagePng,
            }))));
        }

        if (document.HasText)
        {
            var pages = document.PageTexts.Select((text, i) => $"<page number=\"{i + 1}\">\n{text}\n</page>");
            content.Add(new ContentBlockParam(new DocumentBlockParam(new DocumentBlockParamSource(new PlainTextSource(string.Join('\n', pages))))
            {
                Title = $"{document.FileName} (text layer)",
            }));
        }
    }
}
