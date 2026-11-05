using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DocProc.Definitions.Pipelines;

/// <summary>Reads and writes <c>pipelineOverrides</c> arrays (camelCase, strict: an unknown member is an error).</summary>
public static class PipelineOverridesJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>The overrides of <paramref name="node"/> (a JSON array), or null with the reason when it does not read.</summary>
    public static IReadOnlyList<PipelineOverride>? TryRead(JsonNode? node, out string? error)
    {
        error = null;
        if (node is null)
        {
            return [];
        }

        if (node is not JsonArray)
        {
            error = "pipelineOverrides is an array of {op, step, params?, with?}.";
            return null;
        }

        try
        {
            var read = node.Deserialize<List<PipelineOverride?>>(Options) ?? [];
            if (read.Any(o => o is null))
            {
                error = "pipelineOverrides holds an entry that is not an object.";
                return null;
            }

            return [.. read!];
        }
        catch (JsonException e)
        {
            error = $"pipelineOverrides does not read as {{op, step, params?, with?}} entries: {e.Message}";
            return null;
        }
    }

    public static JsonArray Write(IReadOnlyList<PipelineOverride> overrides) =>
        JsonSerializer.SerializeToNode(overrides, Options) as JsonArray ?? [];
}
