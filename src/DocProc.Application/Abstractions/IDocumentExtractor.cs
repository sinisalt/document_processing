using DocProc.Domain.Documents;
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

/// <summary>Outcome of one extraction path. <see cref="Flags"/> carries path-specific notes such as PROFILE_FALLBACK.</summary>
public sealed record ExtractionOutcome(Invoice Invoice, ExtractionMethod Method, IReadOnlyList<DocumentFlag> Flags, DocumentCost Cost);

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
