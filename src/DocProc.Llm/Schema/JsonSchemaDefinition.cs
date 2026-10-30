using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocProc.Llm.Schema;

/// <summary>A versioned JSON schema handed to the provider as the structured-output format.</summary>
public sealed record JsonSchemaDefinition(string Name, string Version, JsonObject Schema)
{
    public string Json => Schema.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    public override string ToString() => $"{Name}@{Version}";
}
