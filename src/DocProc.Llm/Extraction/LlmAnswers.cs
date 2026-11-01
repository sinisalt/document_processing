using System.Text.Json;
using DocProc.Llm.Schema;

namespace DocProc.Llm.Extraction;

/// <summary>Shared handling of a structured-output answer: stop reason first (docs/06), then the contract type.</summary>
public static class LlmAnswers
{
    public static T Parse<T>(LlmResult result, JsonSchemaDefinition schema, string what)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(schema);

        if (result.IsRefusal)
        {
            throw new LlmException(LlmErrorKind.ContentRefused, $"The model refused the {what} request (stop reason '{result.StopReason}').");
        }

        if (result.IsTruncated)
        {
            throw new LlmExtractionException($"The {what} answer was truncated; the output budget is too small for schema {schema}.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(result.Json, PortableSchemaGenerator.SerializerOptions)
                   ?? throw new LlmExtractionException($"The {what} answer was JSON null.");
        }
        catch (JsonException ex)
        {
            throw new LlmExtractionException($"The {what} answer does not match schema {schema}: {ex.Message}", ex);
        }
    }
}
