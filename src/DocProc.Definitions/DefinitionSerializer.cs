using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DocProc.Definitions;

/// <summary>
/// Reads and writes definitions (docs/11 §5.2). Reading is strict: an unknown property, a missing required one, a
/// comment or a trailing comma is an error, so a definition either means exactly what it says or does not load. The
/// meta-schema <c>schemas/document-type-definition.v1.json</c> takes over the shape checks when definitions become
/// editable (P1); until then this reader and <see cref="DefinitionChecker"/> are the checks.
/// </summary>
public static class DefinitionSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = true,
    };

    /// <summary>The definition in <paramref name="json"/>; throws <see cref="DefinitionFormatException"/> when it is not one.</summary>
    public static TypeDefinition Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize<TypeDefinition>(json, Options)
                   ?? throw new DefinitionFormatException("A definition must be a JSON object, not null.");
        }
        catch (JsonException e)
        {
            throw new DefinitionFormatException($"Not a valid definition: {e.Message}", e);
        }
    }

    public static string Write(TypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return JsonSerializer.Serialize(definition, Options);
    }
}

/// <summary>A definition that does not parse, or a template resource that is missing or malformed.</summary>
public sealed class DefinitionFormatException : Exception
{
    public DefinitionFormatException()
    {
    }

    public DefinitionFormatException(string message)
        : base(message)
    {
    }

    public DefinitionFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
