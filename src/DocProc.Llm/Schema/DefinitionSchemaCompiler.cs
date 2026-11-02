using System.Text.Json.Nodes;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;

namespace DocProc.Llm.Schema;

/// <summary>
/// The LLM side of the definition compiler (docs/11 §5.5, §9.3, §20): the fixed schemas of the extraction adapters a
/// definition can name, and the provider portability check over a compiled schema. The compiler itself lives in
/// <c>DocProc.Definitions</c>, which cannot see this project; publishing hands it these.
/// </summary>
public static class DefinitionSchemaCompiler
{
    /// <summary>The extraction adapters by id: <c>invoice_extraction@1</c> is today's invoice contract, unchanged.</summary>
    public static IReadOnlyDictionary<string, JsonObject> AdapterSchemas { get; } = new Dictionary<string, JsonObject>(StringComparer.Ordinal)
    {
        [$"{CanonicalSchemas.InvoiceExtraction.Name}@{CanonicalSchemas.InvoiceExtraction.Version}"] = CanonicalSchemas.InvoiceExtraction.Schema,
    };

    /// <summary>Compiler options with the adapter schemas.</summary>
    public static CompileOptions Options { get; } = new() { AdapterSchemas = AdapterSchemas };

    /// <summary>Compiles <paramref name="definition"/> with the adapter schemas.</summary>
    public static CompileResult Compile(TypeDefinition definition) => DefinitionCompiler.Compile(definition, Options);

    /// <summary>The violations of the structured-output intersection in a compiled LLM schema (<see cref="SchemaPortability"/>).</summary>
    public static IReadOnlyList<string> CheckPortability(JsonObject schema) => SchemaPortability.Check(schema);

    /// <summary>The compiled schema as the gateway sends it.</summary>
    public static JsonSchemaDefinition ToDefinition(CompiledLlmSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return new JsonSchemaDefinition(schema.Name, schema.Version, schema.Schema);
    }

    /// <summary>The publish checks with this deployment's adapters and portability check filled in.</summary>
    public static PublishReport RunPublishChecks(PublishCheckInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return PublishChecks.Run(input with { CompileOptions = Options, PortabilityCheck = CheckPortability });
    }
}
