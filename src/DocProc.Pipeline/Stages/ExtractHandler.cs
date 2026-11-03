using System.Text.Json.Nodes;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Documents;
using DocProc.Definitions.Compilation;
using DocProc.Application.Pipeline;
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
/// extraction path (purchase orders, delivery notes, statements, other) skip straight on with <c>UNSUPPORTED_TYPE</c>
/// and lose any canonical data an earlier run as an invoice left. A part of a split is sent as its own pages, never
/// as the original PDF.
/// <para>
/// With <c>configurableTypes</c> on (docs/11 §5.6, P1) a type with a field tree but no extraction path (purchase
/// orders, delivery notes, price lists, a tenant's own types) is definition-driven instead: the document gets the
/// tree of its type version with every field Missing (<see cref="DefinitionTree.CreateEmpty"/>), or keeps the tree a
/// reviewer already filled for that type, completed with the fields a newer version added, stored as
/// <c>CanonicalSchemaVersion</c> 2, so the review form can show and the reviewer fill it. An invoice-family type the
/// tenant copied or created is definition-driven too: its added fields are asked of the LLM in <c>x_custom</c>
/// (docs/11 §9.1) and the tree is completed with every field of the definition. Every other document is stored
/// exactly as before.
/// </para>
/// </summary>
internal sealed class ExtractHandler(
    DocProcDbContext db,
    DocumentTimeline timeline,
    DocumentFlagWriter flags,
    PipelineBlobs blobs,
    IEnumerable<IDocumentExtractor> extractors,
    IEffectiveConfigResolver configResolver,
    ITenantFeatures features,
    ILogger<ExtractHandler> logger) : DocumentStageHandler(db, timeline, logger)
{
    // LLM_PROVIDER_UNAVAILABLE is written by the failure recorder when this stage's LLM call gives up; a later run that
    // succeeds owns it too, so the flag does not outlive the outage.
    private static readonly string[] OwnedFlags = [FlagCodes.ProfileFallback, FlagCodes.CurrencyInferred, FlagCodes.UnsupportedType, FlagCodes.LlmProviderUnavailable];
    private static readonly ExtractionMethod[] PathOrder = [ExtractionMethod.StructuredXml, ExtractionMethod.Profile, ExtractionMethod.Llm];

    public override PipelineStage Stage => PipelineStage.Extract;

    protected override bool Accepts(DocumentStatus status) => status == DocumentStatus.Matched;

    protected override async Task<StageOutcome> RunAsync(Document document, ProcessDocument message, CancellationToken cancellationToken)
    {
        // The document is processed with one type version from here on (docs/11 §5.7): the version it is pinned to
        // (a retry, a reprocess of the same type), or the tenant's published version of its type, which it now pins.
        var config = await configResolver.ResolveAsync(new EffectiveConfigRequest(document.TenantId, document.Type, document.SupplierId, document.DocumentTypeVersionId), cancellationToken).ConfigureAwait(false);
        var previousPin = document.DocumentTypeVersionId;
        document.DocumentTypeVersionId = config.Type?.VersionId;
        var configurable = await features.IsEnabledAsync(document.TenantId, TenantFeature.ConfigurableTypes, cancellationToken).ConfigureAwait(false);
        var definitionDriven = CanonicalStorage.IsDefinitionDriven(config.Type, configurable);

        if (!document.Type.IsInvoiceFamily && definitionDriven)
        {
            await StoreDefinitionTreeAsync(document, config.Type!, previousPin, cancellationToken).ConfigureAwait(false);
            return StageOutcome.Advanced;
        }

        if (!document.Type.IsInvoiceFamily)
        {
            // A reprocess may have reclassified an extracted invoice: its data describes a type the document no
            // longer has, so it goes (the costs stay, they were spent).
            ClearExtraction(document);
            await DocumentFieldIndexWriter.RefreshAsync(Db, document, [], cancellationToken).ConfigureAwait(false);
            flags.Replace(document, OwnedFlags, [FlagCodes.Create(FlagCodes.UnsupportedType, $"No extraction path exists for {document.Type} documents yet.", parameters: FlagParams.Of(("type", document.Type.ToString())))]);
            Timeline.Transition(document, DocumentStatus.Extracted, new { type = document.Type, skipped = true });
            await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return StageOutcome.Advanced;
        }

        var input = await BuildInputAsync(document, Resolved(config.Type, definitionDriven), cancellationToken).ConfigureAwait(false);
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

        var extracted = outcome.Document;
        if (definitionDriven && config.Type!.Compiled is { } compiled)
        {
            // The added fields the extraction did not fill are there as Missing, so the form and the rules see every field.
            DefinitionTree.Complete(extracted.Root, compiled.Descriptors);
        }

        Apply(document, extracted, config.Type?.Roles, definitionDriven);
        await DocumentFieldIndexWriter.RefreshAsync(Db, document, config.Type?.IndexedFields ?? [], cancellationToken).ConfigureAwait(false);
        document.AppliedConfigJson = AppliedConfig(config.Type, outcome.Method == ExtractionMethod.Llm ? input.Definition?.LlmSchema?.ToString() ?? DocProc.Llm.Schema.CanonicalSchemas.InvoiceExtraction.ToString() : null);
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

    /// <summary>
    /// Stores the outcome's canonical tree and rewrites the list columns from it through the one role projector, under
    /// the role bindings of the resolved type definition.
    /// </summary>
    private static void Apply(Document document, ExtractedDocument extracted, RoleBindings? roles, bool definitionDriven)
    {
        document.CanonicalJson = extracted.ToJson();
        document.CanonicalSchemaVersion = definitionDriven ? DefinitionTree.SchemaVersion : CanonicalDocumentSerializer.SchemaVersion;
        DocumentRoleColumns.Refresh(document, roles);
    }

    /// <summary>
    /// A definition-driven type without an extraction path (docs/11 §5.6): the tree of the type version, empty, or the
    /// tree the document already holds for this type (pinned before this run: a retry, or a reprocess after a reviewer
    /// filled it), completed with the fields it lacks. The reviewer fills it in review; <c>UNSUPPORTED_TYPE</c> says so.
    /// </summary>
    private async Task StoreDefinitionTreeAsync(Document document, EffectiveType type, Guid? previousPin, CancellationToken cancellationToken)
    {
        var compiled = type.Compiled!;
        JsonObject? tree = null;
        if (previousPin is not null && document.CanonicalSchemaVersion == DefinitionTree.SchemaVersion && document.CanonicalJson is { } stored)
        {
            try
            {
                tree = JsonNode.Parse(stored) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                tree = null;
            }
        }

        var kept = tree is not null;
        tree ??= DefinitionTree.CreateEmpty(compiled.Descriptors);
        DefinitionTree.Complete(tree, compiled.Descriptors);
        document.CanonicalJson = tree.ToJsonString(Domain.Documents.CanonicalJson.Options);
        document.CanonicalSchemaVersion = DefinitionTree.SchemaVersion;
        document.ExtractionMethod = null;
        DocumentRoleColumns.Refresh(document, type.Roles);
        await DocumentFieldIndexWriter.RefreshAsync(Db, document, type.IndexedFields, cancellationToken).ConfigureAwait(false);
        document.AppliedConfigJson = AppliedConfig(type, llmSchema: null);
        flags.Replace(document, OwnedFlags, [FlagCodes.Create(
            FlagCodes.UnsupportedType,
            $"No automatic extraction exists for {document.Type} documents yet; the fields of the type are filled in review.",
            parameters: FlagParams.Of(("type", document.Type.ToString()), (FlagParams.Reason, "definition")))]);
        Timeline.Transition(document, DocumentStatus.Extracted, new { type = document.Type, skipped = true, definition = new { key = type.TypeKey, version = type.Version }, kept });
        await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What the extraction is run against: the type version and, for an invoice-family type with added fields, those fields (<c>x_custom</c>).</summary>
    private static ResolvedDefinition? Resolved(EffectiveType? type, bool definitionDriven)
    {
        if (type?.Compiled is not { } compiled)
        {
            return null;
        }

        var custom = definitionDriven
            ? compiled.LlmSchema.CustomPaths.Select(p => compiled.Descriptors.Find(p)).OfType<FieldDescriptor>().ToList()
            : [];
        return new ResolvedDefinition(type.TypeKey, type.VersionId, type.Version, definitionDriven ? compiled.LlmSchema : null, custom);
    }

    /// <summary>The <c>AppliedConfigJson</c> of docs/11 §6.2 as far as P1 knows it: the type version and, after an LLM extraction, the schema it was sent (<c>name@version</c>).</summary>
    private string? AppliedConfig(EffectiveType? type, string? llmSchema)
    {
        if (type is null)
        {
            return null;
        }

        var applied = new JsonObject
        {
            ["typeVersionId"] = type.VersionId?.ToString(),
            ["typeKey"] = type.TypeKey,
            ["typeVersion"] = type.Version,
            ["template"] = $"{type.TemplateKey}@{type.TemplateVersion}",
            ["resolvedAt"] = Timeline.Now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
        };
        if (llmSchema is not null)
        {
            applied["llm"] = new JsonObject { ["schema"] = llmSchema };
        }

        return applied.ToJsonString();
    }

    private static void ClearExtraction(Document document)
    {
        document.CanonicalJson = null;
        document.CanonicalSchemaVersion = null;
        document.DocumentNumber = null;
        document.DocumentNumberNormalized = null;
        document.IssueDate = null;
        document.Currency = null;
        document.TotalGross = null;
        document.ValidFrom = null;
        document.ValidUntil = null;
        document.LineCount = null;
        document.ExtractionMethod = null;
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

    private async Task<ExtractionInput> BuildInputAsync(Document document, ResolvedDefinition? definition, CancellationToken cancellationToken)
    {
        // The normalised PDF is the whole file: only a document that covers all of it may send it. A part of a split
        // goes to the model as its own page images and text, or every part would be read from the first invoice.
        var item = document.IngestionItem;
        ReadOnlyMemory<byte>? pdf = item is { Kind: ItemKind.Pdf } && document.NormalizedBlob is { } normalized
                                    && !await IsSplitPartAsync(document, cancellationToken).ConfigureAwait(false)
            ? await blobs.ReadAsync(normalized, cancellationToken).ConfigureAwait(false)
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
            new TenantHints(tenant.LegalName, tenant.VatId, tenant.DefaultCurrency),
            definition);
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
}
