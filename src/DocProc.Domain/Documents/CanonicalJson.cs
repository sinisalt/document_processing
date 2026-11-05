using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DocProc.Domain.Documents;

/// <summary>
/// The JSON conventions of the canonical tree (camelCase, enums as strings, nulls left out). They live in the
/// domain so the layers that build or read a tree without the persistence layer (the extraction outcome, the rule
/// view) write it exactly as the stored column holds it; <c>CanonicalDocumentSerializer.Options</c> is this instance.
/// </summary>
public static class CanonicalJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = false,
    };
}
