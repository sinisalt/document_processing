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
    DocumentType Type,
    ReadOnlyMemory<byte>? Pdf,
    IReadOnlyList<ReadOnlyMemory<byte>> PageImages,
    TextLayer? TextLayer,
    ReadOnlyMemory<byte>? EmbeddedXml,
    SupplierHints? Supplier,
    TenantHints Tenant);

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
/// </summary>
public sealed record ExtractedDocument(JsonObject Root, Guid? DefinitionId = null, int? DefinitionVersion = null)
{
    // The invoice the tree was serialised from, so the compatibility reading hands back the very instance.
    private Invoice? _invoice;

    /// <summary>An invoice's tree, in the canonical JSON conventions (<see cref="CanonicalJson.Options"/>).</summary>
    public static ExtractedDocument FromInvoice(Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var root = JsonSerializer.SerializeToNode(invoice, CanonicalJson.Options) as JsonObject
                   ?? throw new JsonException("The invoice did not serialise to a JSON object.");
        return new ExtractedDocument(root) { _invoice = invoice };
    }

    /// <summary>
    /// The tree as the canonical column stores it. A tree built from an invoice is written from the invoice itself, so
    /// the stored bytes are exactly the ones the column held before the tree existed: writing the node tree escapes some
    /// characters the serialiser writes plainly (the <c>+</c> of a time-zone offset).
    /// </summary>
    public string ToJson() => _invoice is not null ? JsonSerializer.Serialize(_invoice, CanonicalJson.Options) : Root.ToJsonString(CanonicalJson.Options);

    /// <summary>The tree read as an invoice (the source instance when the tree was built from one).</summary>
    public Invoice ToInvoice() =>
        _invoice ??= Root.Deserialize<Invoice>(CanonicalJson.Options) ?? throw new JsonException("The extracted tree was JSON null.");
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
