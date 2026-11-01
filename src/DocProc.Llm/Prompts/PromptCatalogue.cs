namespace DocProc.Llm.Prompts;

/// <summary>Stable prompt identifiers; together with the version they form the cache key and the usage row's prompt version.</summary>
public static class PromptIds
{
    public const string ExtractInvoice = "extract-invoice";
    public const string ClassifyDocument = "classify-document";
    public const string SplitDocument = "split-document";
}

/// <summary>
/// The prompts the pipeline sends, loaded once from the Markdown and JSON resources embedded next to this class
/// (docs/06 "Request design"). Each bundle carries the stable prefix only (system text and worked examples);
/// callers set <see cref="PromptBundle.Task"/> per document. Bump a version whenever its resource changes, since
/// the version is what invalidates provider caches and replay fixtures.
/// </summary>
public sealed class PromptCatalogue
{
    public const string ExtractInvoiceVersion = "1";
    public const string ClassifyDocumentVersion = "1";
    public const string SplitDocumentVersion = "1";

    private PromptCatalogue(PromptBundle extractInvoice, PromptBundle classifyDocument, PromptBundle splitDocument)
    {
        ExtractInvoice = extractInvoice;
        ClassifyDocument = classifyDocument;
        SplitDocument = splitDocument;
    }

    /// <summary>Stage 6c: the extraction rules, the schema description and two worked examples.</summary>
    public PromptBundle ExtractInvoice { get; }

    /// <summary>Stage 4 fallback when keyword rules are below threshold.</summary>
    public PromptBundle ClassifyDocument { get; }

    /// <summary>Stage 3 fallback for multi-page attachments.</summary>
    public PromptBundle SplitDocument { get; }

    public static PromptCatalogue Load() => new(
        new PromptBundle
        {
            Id = PromptIds.ExtractInvoice,
            Version = ExtractInvoiceVersion,
            System = ReadResource("extract-invoice.system.md"),
            Examples =
            [
                new PromptExample(ReadResource("extract-invoice.example-1.input.md"), ReadResource("extract-invoice.example-1.output.json")),
                new PromptExample(ReadResource("extract-invoice.example-2.input.md"), ReadResource("extract-invoice.example-2.output.json")),
            ],
            Task = string.Empty,
        },
        new PromptBundle
        {
            Id = PromptIds.ClassifyDocument,
            Version = ClassifyDocumentVersion,
            System = ReadResource("classify-document.system.md"),
            Task = string.Empty,
        },
        new PromptBundle
        {
            Id = PromptIds.SplitDocument,
            Version = SplitDocumentVersion,
            System = ReadResource("split-document.system.md"),
            Task = string.Empty,
        });

    /// <summary>Reads an embedded prompt resource with line endings normalised, so request hashes do not depend on the build machine.</summary>
    public static string ReadResource(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var stream = typeof(PromptCatalogue).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded prompt resource '{name}' was not found in {typeof(PromptCatalogue).Assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n").Trim();
    }
}
