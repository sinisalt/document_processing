using DocProc.Llm.Contracts;

namespace DocProc.Llm.Schema;

/// <summary>The versioned schemas the pipeline sends. Bump the version whenever a contract type changes shape.</summary>
public static class CanonicalSchemas
{
    public const string InvoiceExtractionVersion = "1";
    public const string ClassificationVersion = "1";
    public const string SplitVersion = "1";

    public static JsonSchemaDefinition InvoiceExtraction { get; } =
        PortableSchemaGenerator.Generate<InvoiceExtraction>("invoice_extraction", InvoiceExtractionVersion);

    public static JsonSchemaDefinition Classification { get; } =
        PortableSchemaGenerator.Generate<ClassificationResult>("document_classification", ClassificationVersion);

    public static JsonSchemaDefinition Split { get; } =
        PortableSchemaGenerator.Generate<SplitResult>("document_split", SplitVersion);

    public static IEnumerable<JsonSchemaDefinition> All()
    {
        yield return InvoiceExtraction;
        yield return Classification;
        yield return Split;
    }
}
