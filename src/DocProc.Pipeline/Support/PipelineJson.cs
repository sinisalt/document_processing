using System.Text.Json;
using System.Text.Json.Serialization;
using DocProc.Domain.Text;

namespace DocProc.Pipeline.Support;

/// <summary>JSON conventions for what the pipeline writes besides the canonical model: event details, outbox payloads, text layers.</summary>
internal static class PipelineJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Text layers are stored without the derived members (plain text, word counts, box sizes) that the records compute
    /// from their positional properties, so the derived blob stays a quarter of the size it would otherwise be.
    /// </summary>
    private static readonly JsonSerializerOptions TextLayerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        IgnoreReadOnlyProperties = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static byte[] SerializeTextLayer(TextLayer layer) => JsonSerializer.SerializeToUtf8Bytes(layer, TextLayerOptions);

    public static TextLayer DeserializeTextLayer(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize<TextLayer>(json, TextLayerOptions) ?? throw new JsonException("The stored text layer was JSON null.");
}
