using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;
using DocProc.Domain.Text;

namespace DocProc.Application.Abstractions;

/// <summary>What stage 6 hands to an extraction path.</summary>
public sealed record ExtractionInput(
    Guid TenantId,
    Guid DocumentId,
    DocumentTypeKey Type,
    ReadOnlyMemory<byte>? Pdf,
    IReadOnlyList<ReadOnlyMemory<byte>> PageImages,
    TextLayer? TextLayer,
    ReadOnlyMemory<byte>? EmbeddedXml,
    SupplierHints? Supplier,
    TenantHints Tenant,
    ResolvedDefinition? Definition = null)
{
    /// <summary>
    /// The parameters of the pipeline's <c>llm</c> step (docs/11 §9.1 <c>StepParams</c>, §10.1), with
    /// <c>configurableSteps</c> only: <c>effort</c> sets the reasoning effort of the call. Null keeps the extractor's
    /// own options, as before P5.
    /// </summary>
    public System.Text.Json.Nodes.JsonObject? StepParams { get; init; }
}

/// <summary>
/// The type version a document is extracted against (docs/11 §9.1): its key and version, the compiled LLM schema, and
/// the fields the tenant added to an invoice-family type, which the invoice LLM call asks for in <c>x_custom</c>
/// (<see cref="CustomFields"/>, empty for an adopted standard type, whose request is exactly today's).
/// </summary>
public sealed record ResolvedDefinition(string TypeKey, Guid? VersionId, int Version, DocProc.Definitions.Compilation.CompiledLlmSchema? LlmSchema, IReadOnlyList<DocProc.Definitions.Compilation.FieldDescriptor> CustomFields)
{
    /// <summary>Whether the LLM schema carries the tenant's added fields (<c>x_custom</c>).</summary>
    public bool HasCustomFields => LlmSchema is { CustomPaths.Count: > 0 } && CustomFields.Count > 0;
}

/// <summary>"Supplier memory": what we already know about this supplier, passed to the LLM and used by profiles.</summary>
public sealed record SupplierHints(Guid SupplierId, string Name, string? VatId, IReadOnlyList<string> KnownSkus, Invoice? LastApprovedInvoice, Guid? ActiveProfileId);

public sealed record TenantHints(string LegalName, string? VatId, string DefaultCurrency);

/// <summary>
/// Outcome of one extraction path (docs/11 §9.1): the extracted tree, the method, path-specific notes such as
/// PROFILE_FALLBACK in <see cref="Flags"/>, and the cost.
/// </summary>
public sealed record ExtractionOutcome(ExtractedDocument Document, ExtractionMethod Method, IReadOnlyList<DocumentFlag> Flags, DocumentCost Cost)
{
    /// <summary>
    /// Pages a page-by-page extraction (<c>lines: perPage</c>) did not read because the step's <c>maxCostPerDocument</c>
    /// was reached; zero otherwise. The Extract stage keeps what was read and raises <c>LLM_BUDGET_EXCEEDED</c>.
    /// </summary>
    public int PagesNotRead { get; init; }

    /// <summary>
    /// The compatibility constructor of docs/11 §9.1: an invoice path hands over its typed invoice, which is
    /// serialised into the tree exactly as the canonical column stores it, so the invoice extractors compile and behave
    /// unchanged until they build trees themselves.
    /// </summary>
    public ExtractionOutcome(Invoice invoice, ExtractionMethod method, IReadOnlyList<DocumentFlag> flags, DocumentCost cost)
        : this(ExtractedDocument.FromInvoice(invoice), method, flags, cost)
    {
    }

    /// <summary>
    /// The compatibility reading for invoice consumers: the invoice an invoice path handed over, or the tree read as
    /// an invoice. Goes when the extractors build trees themselves.
    /// </summary>
    public Invoice Invoice => Document.ToInvoice();
}

/// <summary>
/// What an extraction path found (docs/11 §9.1): the canonical <see cref="FieldValue{T}"/> tree as a
/// <see cref="JsonObject"/>, and the definition it was extracted against. Until types are data (P1) no definition
/// exists, so <see cref="DefinitionId"/> and <see cref="DefinitionVersion"/> are null and the tree is a coded type's.
/// <para>
/// <see cref="Root"/> is the truth: a step that edits the tree in place, or makes a copy with another tree
/// (<c>with { Root = merged }</c>, the extract-chain merge of docs/11 §9.2), stores and reads back its edit. The
/// invoice the tree was built from, or last read as, is only a cache, used while the tree still holds what it held
/// when the cache was taken.
/// </para>
/// </summary>
public sealed record ExtractedDocument(JsonObject Root, Guid? DefinitionId = null, int? DefinitionVersion = null)
{
    // The invoice the tree reads as, and a copy of the tree as it was when that invoice was taken: the cache holds only
    // while Root still equals the copy. A record copy (with) carries both, and a new Root never equals the copy unless
    // it holds the same values.
    private Invoice? _invoice;
    private JsonNode? _invoiceTree;

    // Whether _invoice is the source the tree was serialised from (FromInvoice), so ToJson may write it instead of the tree.
    private bool _fromInvoice;

    /// <summary>An invoice's tree, in the canonical JSON conventions (<see cref="CanonicalJson.Options"/>).</summary>
    public static ExtractedDocument FromInvoice(Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var root = JsonSerializer.SerializeToNode(invoice, CanonicalJson.Options) as JsonObject
                   ?? throw new JsonException("The invoice did not serialise to a JSON object.");
        return new ExtractedDocument(root) { _invoice = invoice, _invoiceTree = root.DeepClone(), _fromInvoice = true };
    }

    /// <summary>
    /// The tree as the canonical column stores it. An unchanged tree built from an invoice is written from the invoice
    /// itself, so the stored bytes are exactly the ones the column held before the tree existed: writing the node tree
    /// escapes some characters the serialiser writes plainly (the <c>+</c> of a time-zone offset). A changed tree is
    /// written as it is.
    /// </summary>
    public string ToJson() =>
        _fromInvoice && CachedInvoice() is { } invoice
            ? JsonSerializer.Serialize(invoice, CanonicalJson.Options)
            : Root.ToJsonString(CanonicalJson.Options);

    /// <summary>The tree read as an invoice (the source instance while a tree built from one is unchanged).</summary>
    public Invoice ToInvoice()
    {
        if (CachedInvoice() is { } cached)
        {
            return cached;
        }

        var invoice = Root.Deserialize<Invoice>(CanonicalJson.Options) ?? throw new JsonException("The extracted tree was JSON null.");
        _invoice = invoice;
        _invoiceTree = Root.DeepClone();
        _fromInvoice = false;
        return invoice;
    }

    private Invoice? CachedInvoice() => _invoice is not null && JsonNode.DeepEquals(Root, _invoiceTree) ? _invoice : null;
}

public sealed record DocumentCost(int LlmInputTokens, int LlmOutputTokens, int OcrPages, decimal? Usd, string? LlmProvider, string? LlmModel, KeyOwner? KeyOwner)
{
    public static readonly DocumentCost Free = new(0, 0, 0, 0m, null, null, null);
}

/// <summary>
/// One of the three extraction paths of stage 6 (docs/03): structured XML, extractor profile, LLM. The stage tries
/// them in order; <see cref="CanHandle"/> is cheap and <see cref="ExtractAsync"/> returns null when the path gives up.
/// </summary>
public interface IDocumentExtractor
{
    ExtractionMethod Method { get; }

    bool CanHandle(ExtractionInput input);

    Task<ExtractionOutcome?> ExtractAsync(ExtractionInput input, CancellationToken cancellationToken);
}

/// <summary>
/// What the LLM path of a definition-driven type is asked for (docs/11 §9.3, <c>LlmDefinitionExtractor</c>): the
/// extraction input, the type version's compiled artifacts, its display name and LLM guidance, and in
/// <c>fill-missing</c> mode the paths earlier steps left Missing (null asks for every field).
/// </summary>
public sealed record DefinitionExtractionRequest(
    ExtractionInput Input,
    DocProc.Definitions.Compilation.CompiledDefinition Compiled,
    string TypeName,
    string? Guidance,
    IReadOnlyList<string>? FillPaths = null)
{
    /// <summary>The <c>llm</c> step's <c>exclude</c>: top-level fields not asked for (a price list's <c>lines</c>, docs/11 §5.8).</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];

    /// <summary>
    /// The <c>llm</c> step's <c>lines: perPage</c> (docs/11 §5.8, P5): the header is asked for once without the rows, and
    /// the top-level tables page by page, one call per page, until <see cref="MaxCostUsd"/> is reached.
    /// </summary>
    public bool LinesPerPage { get; init; }

    /// <summary>The step's <c>maxCostPerDocument</c> (USD), against which <see cref="SpentUsd"/> and the calls of this extraction count.</summary>
    public decimal? MaxCostUsd { get; init; }

    /// <summary>What the document's LLM calls cost before this extraction (USD).</summary>
    public decimal SpentUsd { get; init; }
}

/// <summary>
/// The LLM path of a definition-driven type (docs/11 §9.3): a type outside the invoice family, extracted against its
/// compiled LLM schema and mapped into its canonical tree (<c>DefinitionMapper</c>). Registered by the LLM project; a
/// host without it, or a tenant without a provider, keeps the definition-driven tree for the reviewer, as before P5.
/// Provider faults propagate as the gateway raises them.
/// </summary>
public interface IDefinitionExtractor
{
    Task<ExtractionOutcome> ExtractAsync(DefinitionExtractionRequest request, CancellationToken cancellationToken);
}
