using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Common;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocProc.Pipeline.Stages;

/// <summary>
/// Stage 6 (docs/03 "Extract"): builds the <see cref="ExtractionInput"/> from the stored artefacts and the supplier
/// memory, then tries the registered <see cref="IDocumentExtractor"/>s in path order (structured XML, profile, LLM).
/// The first outcome is stored as canonical JSON with the denormalised columns and the costs. Types without an
/// extraction path (purchase orders, delivery notes, statements, other) skip straight on with <c>UNSUPPORTED_TYPE</c>.
/// </summary>
internal sealed class ExtractHandler(
    DocProcDbContext db,
    DocumentTimeline timeline,
    DocumentFlagWriter flags,
    PipelineBlobs blobs,
    IEnumerable<IDocumentExtractor> extractors,
    ILogger<ExtractHandler> logger) : DocumentStageHandler(db, timeline, logger)
{
    private static readonly string[] OwnedFlags = [FlagCodes.ProfileFallback, FlagCodes.CurrencyInferred, FlagCodes.UnsupportedType];
    private static readonly ExtractionMethod[] PathOrder = [ExtractionMethod.StructuredXml, ExtractionMethod.Profile, ExtractionMethod.Llm];

    public override PipelineStage Stage => PipelineStage.Extract;

    protected override bool Accepts(DocumentStatus status) => status == DocumentStatus.Matched;

    protected override async Task<StageOutcome> RunAsync(Document document, ProcessDocument message, CancellationToken cancellationToken)
    {
        if (document.Type is not (DocumentType.Invoice or DocumentType.CreditNote))
        {
            flags.Replace(document, OwnedFlags, [FlagCodes.Create(FlagCodes.UnsupportedType, $"No extraction path exists for {document.Type} documents yet.")]);
            Timeline.Transition(document, DocumentStatus.Extracted, new { type = document.Type, skipped = true });
            await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return StageOutcome.Advanced;
        }

        var input = await BuildInputAsync(document, cancellationToken).ConfigureAwait(false);
        ExtractionOutcome? outcome = null;
        var tried = new List<string>();
        foreach (var extractor in extractors.OrderBy(e => Array.IndexOf(PathOrder, e.Method)))
        {
            if (!extractor.CanHandle(input))
            {
                continue;
            }

            tried.Add(extractor.Method.ToString());
            outcome = await extractor.ExtractAsync(input, cancellationToken).ConfigureAwait(false);
            if (outcome is not null)
            {
                break;
            }
        }

        if (outcome is null)
        {
            throw new StageFailedException(tried.Count == 0
                ? "no extraction path can handle the document (no e-invoice XML, no pages, no text)"
                : $"every extraction path gave up: {string.Join(", ", tried)}");
        }

        Apply(document, outcome.Invoice);
        document.ExtractionMethod = outcome.Method;
        AddCost(document.Costs, outcome.Cost);
        flags.Replace(document, OwnedFlags, outcome.Flags);

        Timeline.Transition(document, DocumentStatus.Extracted, new
        {
            method = outcome.Method,
            pathsTried = tried,
            documentNumber = document.DocumentNumber,
            totalGross = document.TotalGross,
            currency = document.Currency,
            cost = new { outcome.Cost.LlmInputTokens, outcome.Cost.LlmOutputTokens, outcome.Cost.Usd, outcome.Cost.LlmProvider, outcome.Cost.LlmModel, outcome.Cost.KeyOwner },
            flags = outcome.Flags.Select(f => f.Code).ToList(),
        });
        await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return StageOutcome.Advanced;
    }

    private static void Apply(Document document, Invoice invoice)
    {
        document.CanonicalJson = CanonicalDocumentSerializer.Serialize(invoice);
        document.CanonicalSchemaVersion = CanonicalDocumentSerializer.SchemaVersion;
        document.DocumentNumber = Truncate(invoice.InvoiceNumber.Value?.Verbatim, 100);
        document.DocumentNumberNormalized = Truncate(invoice.InvoiceNumber.Value?.Normalized, 100);
        document.IssueDate = invoice.IssueDate.HasValue ? invoice.IssueDate.Value : null;
        document.Currency = invoice.Currency.HasValue && CurrencyCode.IsWellFormed(invoice.Currency.Value) ? invoice.Currency.Value : null;
        document.TotalGross = invoice.TotalGross.HasValue ? invoice.TotalGross.Value.Amount : null;
    }

    private static void AddCost(DocumentCosts costs, DocumentCost cost)
    {
        costs.LlmInputTokens += cost.LlmInputTokens;
        costs.LlmOutputTokens += cost.LlmOutputTokens;
        costs.OcrPages += cost.OcrPages;
        if (cost.Usd is { } usd)
        {
            costs.EstimatedUsd = (costs.EstimatedUsd ?? 0m) + usd;
        }

        costs.LlmProvider = cost.LlmProvider ?? costs.LlmProvider;
        costs.LlmModel = cost.LlmModel ?? costs.LlmModel;
        costs.KeyOwner = cost.KeyOwner ?? costs.KeyOwner;
    }

    private async Task<ExtractionInput> BuildInputAsync(Document document, CancellationToken cancellationToken)
    {
        var item = document.IngestionItem;
        ReadOnlyMemory<byte>? pdf = item is { Kind: ItemKind.Pdf }
            ? await blobs.ReadAsync(item.Blob, cancellationToken).ConfigureAwait(false)
            : null;
        var pages = await blobs.ReadPageImagesAsync(document, cancellationToken).ConfigureAwait(false);
        var textLayer = await blobs.ReadTextLayerAsync(document, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte>? xml = document.EmbeddedXmlBlob is { } xmlBlob
            ? await blobs.ReadAsync(xmlBlob, cancellationToken).ConfigureAwait(false)
            : null;

        var tenant = await LoadTenantAsync(document, cancellationToken).ConfigureAwait(false);
        var supplier = await SupplierHintsAsync(document, cancellationToken).ConfigureAwait(false);

        return new ExtractionInput(
            document.TenantId,
            document.Id,
            document.Type,
            pdf,
            pages,
            textLayer,
            xml,
            supplier,
            new TenantHints(tenant.LegalName, tenant.VatId, tenant.DefaultCurrency));
    }

    /// <summary>"Supplier memory" (docs/06): the supplier row plus its latest approved invoice as a hint for the LLM path.</summary>
    private async Task<SupplierHints?> SupplierHintsAsync(Document document, CancellationToken cancellationToken)
    {
        if (document.SupplierId is not { } supplierId)
        {
            return null;
        }

        var supplier = await Db.Suppliers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == supplierId, cancellationToken).ConfigureAwait(false);
        if (supplier is null)
        {
            return null;
        }

        // Ordered in memory: SQLite cannot ORDER BY a DateTimeOffset column (docs/09), and approved documents per supplier are few.
        var approved = await Db.Documents.AsNoTracking()
            .Where(d => d.SupplierId == supplierId && d.Id != document.Id && d.CanonicalJson != null
                        && (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.AutoApproved))
            .Select(d => new { d.Id, d.ApprovedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var latestId = approved.OrderByDescending(d => d.ApprovedAt).ThenByDescending(d => d.Id).FirstOrDefault()?.Id;

        Invoice? lastApproved = null;
        if (latestId is { } id)
        {
            var json = await Db.Documents.AsNoTracking().Where(d => d.Id == id).Select(d => d.CanonicalJson).SingleAsync(cancellationToken).ConfigureAwait(false);
            if (json is not null)
            {
                try
                {
                    lastApproved = CanonicalDocumentSerializer.DeserializeInvoice(json);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    Logger.LogWarning(ex, "Approved document {DocumentId} has unreadable canonical JSON; supplier hint skipped.", id);
                }
            }
        }

        var skus = lastApproved?.Lines
            .Select(l => l.SupplierSku.Value)
            .Where(sku => !string.IsNullOrWhiteSpace(sku))
            .Select(sku => sku!)
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];

        return new SupplierHints(supplier.Id, supplier.Name, supplier.VatId, skus, lastApproved, ActiveProfileId: null);
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
