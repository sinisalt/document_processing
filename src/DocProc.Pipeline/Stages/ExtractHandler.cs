using System.Text.Json.Nodes;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Documents;
using DocProc.Definitions.Compilation;
using DocProc.Application.Pipeline;
using DocProc.Definitions.Pipelines;
using DocProc.Mapping.Layouts;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Layouts;
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
/// <para>
/// A document a supplier layout identified (<c>SupplierLayoutVersionId</c>, docs/11 §8.2) is filled by that layout's
/// field mapping first (<c>supplier-mapping</c>, <see cref="ExtractionMethod.Profile"/>), its tables with their table
/// report (<c>Origin=Layout</c>); when the mapping leaves covered or required fields Missing (a drift), the LLM fills
/// only those, where a provider is configured. A document no layout identified is never filled by a layout.
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
    LayoutExtractor layouts,
    LayoutPrefiller prefiller,
    StagePipeline pipelines,
    ExtractLookups lookups,
    ILogger<ExtractHandler> logger,
    IDefinitionExtractor? definitionExtractor = null) : DocumentStageHandler(db, timeline, logger)
{
    // LLM_PROVIDER_UNAVAILABLE is written by the failure recorder when this stage's LLM call gives up; a later run that
    // succeeds owns it too, so the flag does not outlive the outage.
    private static readonly string[] OwnedFlags = [FlagCodes.ProfileFallback, FlagCodes.CurrencyInferred, FlagCodes.UnsupportedType, FlagCodes.LlmProviderUnavailable, FlagCodes.MappingDrift, FlagCodes.LookupFailed];
    private static readonly ExtractionMethod[] PathOrder = [ExtractionMethod.StructuredXml, ExtractionMethod.Profile, ExtractionMethod.Llm];

    public override PipelineStage Stage => PipelineStage.Extract;

    protected override bool Accepts(DocumentStatus status) => status == DocumentStatus.Matched;

    protected override async Task<StageOutcome> RunAsync(Document document, ProcessDocument message, CancellationToken cancellationToken)
    {
        // The document is processed with one type version from here on (docs/11 §5.7): the version it is pinned to
        // (a retry, a reprocess of the same type), or the tenant's published version of its type, which it now pins.
        var config = await configResolver.ResolveAsync(new EffectiveConfigRequest(document.TenantId, document.Type, document.SupplierId, document.DocumentTypeVersionId), cancellationToken).ConfigureAwait(false);
        document.DocumentTypeVersionId = config.Type?.VersionId;
        var configurable = await features.IsEnabledAsync(document.TenantId, TenantFeature.ConfigurableTypes, cancellationToken).ConfigureAwait(false);
        var definitionDriven = CanonicalStorage.IsDefinitionDriven(config.Type, configurable);

        var tenantRow = await LoadTenantAsync(document, cancellationToken).ConfigureAwait(false);

        // The extract steps of the document's pipeline (docs/11 §9.3, §10.1): without configurableSteps, or without
        // overrides, every built-in step runs as before; an override may switch the layout's mapping, the pre-fill, the
        // e-invoice reader or the LLM off, set the LLM's parameters, and add http-lookup steps.
        var pipeline = await pipelines.ResolveAsync(document, config.Type, PipelinePhases.Extract, cancellationToken).ConfigureAwait(false);
        var llmStep = pipeline.Enabled(StepKindCatalogue.Llm);
        var application = pipeline.Runs(StepKindCatalogue.SupplierMapping) && config.Type is { } layoutType && document.EmbeddedXmlBlob is null
            ? await layouts.ApplyAsync(document, layoutType, typedRecord: document.Type.IsInvoiceFamily, tenantRow.DefaultCurrency, tenantRow.CountryCode, cancellationToken).ConfigureAwait(false)
            : null;
        // layout-prefill (docs/11 §9.3, §12.10 step 4): a NEW_LAYOUT document of a known supplier is pre-filled from the
        // pending Candidate, the supplier's other layouts' labels, its master data and known values, never by a layout.
        var prefill = application is null && pipeline.Runs(StepKindCatalogue.LayoutPrefill) && config.Type is { } prefillType && document.EmbeddedXmlBlob is null
            ? await prefiller.PrefillAsync(document, prefillType, typedRecord: document.Type.IsInvoiceFamily, tenantRow.DefaultCurrency, tenantRow.CountryCode, cancellationToken).ConfigureAwait(false)
            : null;
        if (!document.Type.IsInvoiceFamily && definitionDriven)
        {
            pipelines.RecordBudget(document, StepKindCatalogue.Llm, null);
            if (application is not null)
            {
                await StoreLayoutTreeAsync(document, config.Type!, application, pipeline, tenantRow.DefaultCurrency, cancellationToken).ConfigureAwait(false);
                return StageOutcome.Advanced;
            }

            // LlmDefinitionExtractor (docs/11 §9.3, P5, behind customTypes): the type is extracted against its compiled
            // LLM schema, over what the pre-fill found (fill-missing). Without a provider, or when it cannot answer, the
            // document goes on as before: the pre-filled or empty tree for the reviewer.
            var byLlm = await ExtractDefinitionAsync(document, config.Type!, prefill, llmStep, cancellationToken).ConfigureAwait(false);
            if (byLlm.Budget is not null)
            {
                pipelines.RecordBudget(document, StepKindCatalogue.Llm, byLlm.Budget);
            }

            if (byLlm.Outcome is { } extractedByLlm)
            {
                await StoreDefinitionLlmTreeAsync(document, config.Type!, extractedByLlm, byLlm.Input!, prefill, pipeline, tenantRow.DefaultCurrency, cancellationToken).ConfigureAwait(false);
                return StageOutcome.Advanced;
            }

            if (prefill is not null)
            {
                await StorePrefillTreeAsync(document, config.Type!, prefill, pipeline, tenantRow.DefaultCurrency, cancellationToken).ConfigureAwait(false);
                return StageOutcome.Advanced;
            }

            await StoreDefinitionTreeAsync(document, config.Type!, pipeline, tenantRow.DefaultCurrency, cancellationToken).ConfigureAwait(false);
            return StageOutcome.Advanced;
        }

        if (!document.Type.IsInvoiceFamily)
        {
            // A reprocess may have reclassified an extracted invoice: its data describes a type the document no
            // longer has, so it goes (the costs stay, they were spent).
            ClearExtraction(document);
            pipelines.RecordBudget(document, StepKindCatalogue.Llm, null);
            await DocumentFieldIndexWriter.RefreshAsync(Db, document, [], cancellationToken).ConfigureAwait(false);
            await DocumentTableReports.RemoveAllAsync(Db, document.Id, cancellationToken).ConfigureAwait(false);
            flags.Replace(document, OwnedFlags, [FlagCodes.Create(FlagCodes.UnsupportedType, $"No extraction path exists for {document.Type} documents yet.", parameters: FlagParams.Of(("type", document.Type.ToString())))]);
            Timeline.Transition(document, DocumentStatus.Extracted, new { type = document.Type, skipped = true });
            await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return StageOutcome.Advanced;
        }

        var input = await BuildInputAsync(document, Resolved(config.Type, definitionDriven), cancellationToken).ConfigureAwait(false);
        if (llmStep is { Source: not PipelineSources.Default } && llmStep.Params.Count > 0)
        {
            // The llm step's parameters (effort) reach the extractor only when a layer or the type set them (configurableSteps).
            input = input with { StepParams = llmStep.Params };
        }

        // The llm step's maxCostPerDocument (docs/11 §16, per type): once the document's LLM cost reached it, the LLM is not asked.
        DocProc.Llm.Gateway.LlmBudgetExceededException? budget = llmStep?.Number("maxCostPerDocument") is { } maxCost && (document.Costs.EstimatedUsd ?? 0m) >= (decimal)maxCost
            ? new DocProc.Llm.Gateway.LlmBudgetExceededException(new DocProc.Llm.Gateway.LlmBudgetVerdict(false, new DocProc.Llm.Gateway.LlmBudget(DocProc.Llm.Gateway.LlmBudgetPeriod.Document, (decimal)maxCost, DocProc.Llm.LlmStage.Extract), document.Costs.EstimatedUsd ?? 0m))
            : null;
        var llmAllowed = llmStep is not null && budget is null;
        ExtractionOutcome? outcome = null;
        var tried = new List<string>();
        var filledByLlm = false;
        var unfilled = false;
        if (application is not null)
        {
            // supplier-mapping (docs/11 §8.2): the identified layout's values; the LLM fills only what it left Missing.
            tried.Add(ExtractionMethod.Profile.ToString());
            outcome = new ExtractionOutcome(application.Document, ExtractionMethod.Profile, application.Flags, DocumentCost.Free);
            if (application.NeedsFill && llmAllowed && extractors.FirstOrDefault(x => x.Method == ExtractionMethod.Llm && x.CanHandle(input)) is { } llm)
            {
                tried.Add(ExtractionMethod.Llm.ToString());
                ExtractionOutcome? filled = null;
                try
                {
                    filled = await llm.ExtractAsync(input, cancellationToken).ConfigureAwait(false);
                }
                catch (DocProc.Llm.LlmException ex) when (!ex.IsRetryable)
                {
                    // No LLM is configured for the tenant, or it cannot answer this request (a replay store without the
                    // document, a refusal, a key or quota problem, the tenant's budget): the layout's values stand, and
                    // review fills the rest (§12.10 step 4). Only a retryable failure retries the stage.
                    budget = ex as DocProc.Llm.Gateway.LlmBudgetExceededException ?? budget;
                    if (Logger.IsEnabled(LogLevel.Information))
                    {
                        Logger.LogInformation("Document {DocumentId}: the LLM did not fill what the layout left Missing ({Kind}); the layout's values stand.", document.Id, ex.Kind);
                    }
                }

                if (filled is not null)
                {
                    outcome = new ExtractionOutcome(
                        new ExtractedDocument(LayoutExtractor.Merge(application.Document.Root, filled.Document.Root)),
                        ExtractionMethod.Profile,
                        [.. application.Flags, .. filled.Flags],
                        filled.Cost);
                    filledByLlm = true;
                }
            }
        }

        if (outcome is null && prefill is not null)
        {
            // layout-prefill: the located values first; the LLM fills what is left (fill-missing) where one is configured.
            tried.Add(LayoutPrefiller.StepId);
            outcome = new ExtractionOutcome(prefill.Document, ExtractionMethod.Profile, [], DocumentCost.Free);
            if (llmAllowed && extractors.FirstOrDefault(x => x.Method == ExtractionMethod.Llm && x.CanHandle(input)) is { } llm)
            {
                tried.Add(ExtractionMethod.Llm.ToString());
                ExtractionOutcome? filled = null;
                try
                {
                    filled = await llm.ExtractAsync(input, cancellationToken).ConfigureAwait(false);
                }
                catch (DocProc.Llm.LlmException ex) when (!ex.IsRetryable)
                {
                    // No LLM is configured, or it cannot answer this request (a replay store without the document, a
                    // refusal, a key or quota problem, the tenant's budget): the pre-filled values stand, the document
                    // goes to review as it is, and the panel says the LLM source was skipped (§12.10 step 4).
                    budget = ex as DocProc.Llm.Gateway.LlmBudgetExceededException ?? budget;
                    if (Logger.IsEnabled(LogLevel.Information))
                    {
                        Logger.LogInformation("Document {DocumentId}: the LLM did not fill what the pre-fill left Missing ({Kind}).", document.Id, ex.Kind);
                    }
                }

                if (filled is not null)
                {
                    outcome = new ExtractionOutcome(new ExtractedDocument(LayoutExtractor.Merge(prefill.Document.Root, filled.Document.Root)), ExtractionMethod.Llm, filled.Flags, filled.Cost);
                    filledByLlm = true;
                }
            }
        }

        var skippedByPipeline = false;
        foreach (var extractor in outcome is null ? extractors.OrderBy(e => Array.IndexOf(PathOrder, e.Method)) : Enumerable.Empty<IDocumentExtractor>())
        {
            if (!extractor.CanHandle(input))
            {
                continue;
            }

            if ((extractor.Method == ExtractionMethod.StructuredXml && !pipeline.Runs(StepKindCatalogue.XmlEInvoice))
                || (extractor.Method == ExtractionMethod.Llm && !llmAllowed))
            {
                // Switched off by the pipeline, or kept from the LLM by the budget: the next path is tried.
                skippedByPipeline |= extractor.Method != ExtractionMethod.Llm || llmStep is null;
                continue;
            }

            tried.Add(extractor.Method.ToString());
            try
            {
                outcome = await extractor.ExtractAsync(input, cancellationToken).ConfigureAwait(false);
            }
            catch (DocProc.Llm.Gateway.LlmBudgetExceededException exception)
            {
                // The tenant's LLM budget (docs/11 §16): the call was not made; the document goes on without it.
                budget = exception;
                continue;
            }

            if (outcome is not null)
            {
                break;
            }
        }

        if (outcome is null && (budget is not null || skippedByPipeline) && config.Type?.Compiled is { } emptyOf)
        {
            // Nothing extracted because the budget or the pipeline kept every path from running: the type's tree with
            // every field Missing goes on to Validate and review, where the reviewer fills it, instead of failing.
            var empty = LayoutTreeWriter.Build(emptyOf.Descriptors, LayoutExtraction.Empty, [], new Dictionary<string, string>(), null, document.Type.IsInvoiceFamily, document.Type).Tree;
            outcome = new ExtractionOutcome(new ExtractedDocument(empty), ExtractionMethod.Manual, [], DocumentCost.Free);
            unfilled = true;
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

        var lookupRun = await lookups.RunAsync(document, config.Type, pipeline, extracted.Root, document.Type.IsInvoiceFamily, tenantRow.DefaultCurrency, cancellationToken).ConfigureAwait(false);
        Apply(document, extracted, config.Type?.Roles, definitionDriven);
        await DocumentFieldIndexWriter.RefreshAsync(Db, document, config.Type?.IndexedFields ?? [], cancellationToken).ConfigureAwait(false);
        // The extraction wrote every repeating group anew: a table report of the review tool describes rows that are gone.
        await DocumentTableReports.RemoveAllAsync(Db, document.Id, cancellationToken).ConfigureAwait(false);
        document.AppliedConfigJson = AppliedConfig(config.Type, outcome.Method == ExtractionMethod.Llm || filledByLlm ? input.Definition?.LlmSchema?.ToString() ?? DocProc.Llm.Schema.CanonicalSchemas.InvoiceExtraction.ToString() : null, application?.AppliedConfig(filledByLlm), prefill?.AppliedConfig(filledByLlm), lookupRun.Applied);
        if (application is not null)
        {
            await StoreLayoutReportsAsync(document, application, cancellationToken).ConfigureAwait(false);
        }

        document.ExtractionMethod = unfilled ? null : outcome.Method;
        AddCost(document.Costs, outcome.Cost);
        flags.Replace(document, OwnedFlags, [.. outcome.Flags, .. lookupRun.Flags]);
        pipelines.RecordBudget(document, StepKindCatalogue.Llm, budget);

        Timeline.Transition(document, DocumentStatus.Extracted, new
        {
            method = outcome.Method,
            pathsTried = tried,
            documentNumber = document.DocumentNumber,
            totalGross = document.TotalGross,
            currency = document.Currency,
            cost = new { outcome.Cost.LlmInputTokens, outcome.Cost.LlmOutputTokens, outcome.Cost.Usd, outcome.Cost.LlmProvider, outcome.Cost.LlmModel, outcome.Cost.KeyOwner },
            flags = outcome.Flags.Concat(lookupRun.Flags).Select(f => f.Code).ToList(),
            llmBudgetExceeded = budget is null ? (bool?)null : true,
            unfilled = unfilled ? true : (bool?)null,
            lookups = lookupRun.Summary,
        });
        await SaveAsync(document, application, cancellationToken).ConfigureAwait(false);
        return StageOutcome.Advanced;
    }

    /// <summary>
    /// The stage's save; with a layout applied, in one transaction with the layout version's counters, so a save that
    /// fails or is dropped as a stale twin's does not leave a count behind without its <c>layout_applied</c> event
    /// (<see cref="LayoutExtractor.SaveCountedAsync"/>). The LLM call is made before, outside any transaction.
    /// </summary>
    private Task SaveAsync(Document document, LayoutApplication? application, CancellationToken cancellationToken) =>
        application is null
            ? Db.SaveChangesAsync(cancellationToken)
            : layouts.SaveCountedAsync(document, application, cancellationToken);

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
    /// tree the document already holds for this type (a retry, or a reprocess after a reviewer filled it, onto the
    /// pinned version or, with <c>configMode: current</c>, onto the published one), completed with the fields it
    /// lacks. A tree is the document's type's unless <c>AppliedConfigJson</c> names another type; the Classify stage
    /// drops a tree when it changes the type. The reviewer fills it in review; <c>UNSUPPORTED_TYPE</c> says so, unless a
    /// reviewer set the type (<see cref="TypeSource.Human"/>) and knows it.
    /// </summary>
    private async Task StoreDefinitionTreeAsync(Document document, EffectiveType type, ResolvedPipeline pipeline, string tenantCurrency, CancellationToken cancellationToken)
    {
        var compiled = type.Compiled!;
        JsonObject? tree = null;
        if (document.CanonicalSchemaVersion == DefinitionTree.SchemaVersion && document.CanonicalJson is { } stored && IsTreeOf(document, type))
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
        if (!kept)
        {
            // A new empty tree: no group holds the rows a table report describes.
            await DocumentTableReports.RemoveAllAsync(Db, document.Id, cancellationToken).ConfigureAwait(false);
        }

        tree ??= DefinitionTree.CreateEmpty(compiled.Descriptors);
        DefinitionTree.Complete(tree, compiled.Descriptors);
        // No defaults step here: nothing was extracted, and a tree of defaults only would read as data to the approval
        // gate (NO_DATA) and to map mode, which the reviewer fills from the page (docs/11 §12.1, §12.3).
        var lookupRun = await lookups.RunAsync(document, type, pipeline, tree, typedRecord: false, tenantCurrency, cancellationToken).ConfigureAwait(false);
        document.CanonicalJson = tree.ToJsonString(Domain.Documents.CanonicalJson.Options);
        document.CanonicalSchemaVersion = DefinitionTree.SchemaVersion;
        document.ExtractionMethod = null;
        DocumentRoleColumns.Refresh(document, type.Roles);
        await DocumentFieldIndexWriter.RefreshAsync(Db, document, type.IndexedFields, cancellationToken).ConfigureAwait(false);
        document.AppliedConfigJson = AppliedConfig(type, llmSchema: null, lookups: lookupRun.Applied);
        // A type a reviewer set (:set-type, a typed split part) is filled in review by the reviewer who chose it, as
        // :set-type says by raising no UNSUPPORTED_TYPE: a reprocess (from Classify, which keeps that type) or a
        // prefill auto does not raise it either, so it does not come back as an open warning after the reviewer's work.
        flags.Replace(document, OwnedFlags, document.TypeSource == TypeSource.Human
            ? [.. lookupRun.Flags]
            : [FlagCodes.Create(
                FlagCodes.UnsupportedType,
                $"No automatic extraction exists for {document.Type} documents yet; the fields of the type are filled in review.",
                parameters: FlagParams.Of(("type", document.Type.ToString()), (FlagParams.Reason, "definition"))), .. lookupRun.Flags]);
        Timeline.Transition(document, DocumentStatus.Extracted, new { type = document.Type, skipped = true, definition = new { key = type.TypeKey, version = type.Version }, kept, lookups = lookupRun.Summary });
        await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A definition-driven type without an extraction path (a price list) that a layout identified (docs/11 §8.6): the
    /// tree the layout's field mapping and <c>column-table</c> wrote, with the table reports of its groups, instead of
    /// the empty tree for the reviewer.
    /// </summary>
    private async Task StoreLayoutTreeAsync(Document document, EffectiveType type, LayoutApplication application, ResolvedPipeline pipeline, string tenantCurrency, CancellationToken cancellationToken)
    {
        // docs/11 §5.8: the type's defaults step fills what the extraction left empty, before the lookups read the tree.
        await ExtractDefaults.RunAsync(Db, document, type, pipeline, application.Document.Root, tenantCurrency, cancellationToken).ConfigureAwait(false);
        application = application.AfterDefaults(application.Document.Root);
        var lookupRun = await lookups.RunAsync(document, type, pipeline, application.Document.Root, typedRecord: false, tenantCurrency, cancellationToken).ConfigureAwait(false);
        Apply(document, application.Document, type.Roles, definitionDriven: true);
        document.ExtractionMethod = ExtractionMethod.Profile;
        await DocumentFieldIndexWriter.RefreshAsync(Db, document, type.IndexedFields, cancellationToken).ConfigureAwait(false);
        await DocumentTableReports.RemoveAllAsync(Db, document.Id, cancellationToken).ConfigureAwait(false);
        await StoreLayoutReportsAsync(document, application, cancellationToken).ConfigureAwait(false);
        document.AppliedConfigJson = AppliedConfig(type, llmSchema: null, application.AppliedConfig(filledByLlm: false), lookups: lookupRun.Applied);
        flags.Replace(document, OwnedFlags, [.. application.Flags, .. lookupRun.Flags]);
        Timeline.Transition(document, DocumentStatus.Extracted, new
        {
            method = ExtractionMethod.Profile,
            definition = new { key = type.TypeKey, version = type.Version },
            layout = new { application.Layout.LayoutKey, application.Version.Version },
            rows = application.Extraction.Tables.ToDictionary(t => t.Key, t => t.Value.Report.Rows, StringComparer.Ordinal),
            flags = application.Flags.Concat(lookupRun.Flags).Select(f => f.Code).ToList(),
            lookups = lookupRun.Summary,
        });
        await SaveAsync(document, application, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A definition-driven type without an extraction path (a price list) held with <c>NEW_LAYOUT</c>: the tree the
    /// pre-fill wrote (docs/11 §12.10 step 4), every value Inferred with its place, for the reviewer to confirm.
    /// </summary>
    private async Task StorePrefillTreeAsync(Document document, EffectiveType type, PrefillApplication prefill, ResolvedPipeline pipeline, string tenantCurrency, CancellationToken cancellationToken)
    {
        DefinitionTree.Complete(prefill.Document.Root, type.Compiled!.Descriptors);
        // docs/11 §5.8: the type's defaults step fills what the extraction left empty, before the lookups read the tree.
        await ExtractDefaults.RunAsync(Db, document, type, pipeline, prefill.Document.Root, tenantCurrency, cancellationToken).ConfigureAwait(false);
        var lookupRun = await lookups.RunAsync(document, type, pipeline, prefill.Document.Root, typedRecord: false, tenantCurrency, cancellationToken).ConfigureAwait(false);
        Apply(document, prefill.Document, type.Roles, definitionDriven: true);
        document.ExtractionMethod = ExtractionMethod.Profile;
        await DocumentFieldIndexWriter.RefreshAsync(Db, document, type.IndexedFields, cancellationToken).ConfigureAwait(false);
        await DocumentTableReports.RemoveAllAsync(Db, document.Id, cancellationToken).ConfigureAwait(false);
        document.AppliedConfigJson = AppliedConfig(type, llmSchema: null, prefill: prefill.AppliedConfig(filledByLlm: false), lookups: lookupRun.Applied);
        flags.Replace(document, OwnedFlags, lookupRun.Flags);
        Timeline.Transition(document, DocumentStatus.Extracted, new { method = LayoutPrefiller.StepId, definition = new { key = type.TypeKey, version = type.Version }, sources = prefill.Sources.Count, lookups = lookupRun.Summary });
        await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The LLM path of a definition-driven type (docs/11 §9.3): with <c>customTypes</c>, an enabled <c>llm</c> step, a
    /// registered <see cref="IDefinitionExtractor"/> and pages or text to read, the type version's compiled schema is
    /// asked for, in <c>fill-missing</c> mode over a pre-fill (the paths it left Missing). A provider that cannot answer
    /// (none configured, a replay miss, a refusal, a malformed answer) or the tenant's budget leaves the outcome null and
    /// the stage goes on without the LLM; a retryable fault retries the stage.
    /// </summary>
    private async Task<(ExtractionOutcome? Outcome, ExtractionInput? Input, DocProc.Llm.Gateway.LlmBudgetExceededException? Budget)> ExtractDefinitionAsync(
        Document document, EffectiveType type, PrefillApplication? prefill, ResolvedStep? llmStep, CancellationToken cancellationToken)
    {
        if (llmStep is null || type.Compiled is not { } compiled || definitionExtractor is null
            || !await features.IsEnabledAsync(document.TenantId, TenantFeature.CustomTypes, cancellationToken).ConfigureAwait(false))
        {
            return (null, null, null);
        }

        if (llmStep.Number("maxCostPerDocument") is { } maxCost && (document.Costs.EstimatedUsd ?? 0m) >= (decimal)maxCost)
        {
            return (null, null, new DocProc.Llm.Gateway.LlmBudgetExceededException(new DocProc.Llm.Gateway.LlmBudgetVerdict(false, new DocProc.Llm.Gateway.LlmBudget(DocProc.Llm.Gateway.LlmBudgetPeriod.Document, (decimal)maxCost, DocProc.Llm.LlmStage.Extract), document.Costs.EstimatedUsd ?? 0m)));
        }

        var input = await BuildInputAsync(document, Resolved(type, definitionDriven: true), cancellationToken).ConfigureAwait(false);
        if (input.Pdf is not { Length: > 0 } && input.PageImages.Count == 0 && input.TextLayer is not { WordCount: > 0 })
        {
            return (null, null, null);
        }

        if (llmStep is { Source: not PipelineSources.Default } && llmStep.Params.Count > 0)
        {
            input = input with { StepParams = llmStep.Params };
        }

        var fill = prefill is null
            ? null
            : compiled.Descriptors.Fields
                .Where(f => f.IsEnvelope && !f.ReadOnly && !f.Deprecated && !f.Path.Contains("[*]", StringComparison.Ordinal) && IsMissing(prefill.Document.Root, f.Path))
                .Select(f => f.Path)
                .ToList();
        var name = type.Definition.Name.TryGetValue("en", out var english) ? english : type.Definition.Name.Values.FirstOrDefault() ?? type.TypeKey;
        // The step's own parameters (the type's or an override's): exclude and lines: perPage (docs/11 §5.8).
        var exclude = llmStep.Params["exclude"] is System.Text.Json.Nodes.JsonArray excluded
            ? excluded.Select(e => e is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var key) ? key : null).OfType<string>().ToList()
            : [];
        var request = new DefinitionExtractionRequest(input, compiled, name, type.Definition.Llm?.Guidance, fill)
        {
            Exclude = exclude,
            LinesPerPage = llmStep.Text("lines") == "perPage",
            MaxCostUsd = llmStep.Number("maxCostPerDocument") is { } most ? (decimal)most : null,
            SpentUsd = document.Costs.EstimatedUsd ?? 0m,
        };
        try
        {
            var outcome = await definitionExtractor.ExtractAsync(request, cancellationToken).ConfigureAwait(false);
            // A page-by-page read stopped by maxCostPerDocument keeps the pages it read and says so (LLM_BUDGET_EXCEEDED).
            var stopped = outcome.PagesNotRead > 0 && request.MaxCostUsd is { } limit
                ? new DocProc.Llm.Gateway.LlmBudgetExceededException(new DocProc.Llm.Gateway.LlmBudgetVerdict(false, new DocProc.Llm.Gateway.LlmBudget(DocProc.Llm.Gateway.LlmBudgetPeriod.Document, limit, DocProc.Llm.LlmStage.Extract), request.SpentUsd + (outcome.Cost.Usd ?? 0m)))
                : null;
            return (outcome, input, stopped);
        }
        catch (DocProc.Llm.Gateway.LlmBudgetExceededException exception)
        {
            return (null, null, exception);
        }
        catch (DocProc.Llm.LlmException ex) when (!ex.IsRetryable)
        {
            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation("Document {DocumentId}: the LLM did not extract the {Type} document ({Kind}); it goes on to review as before.", document.Id, type.TypeKey, ex.Kind);
            }

            return (null, null, null);
        }
        catch (DocProc.Llm.Extraction.LlmExtractionException ex)
        {
            if (Logger.IsEnabled(LogLevel.Warning))
            {
                Logger.LogWarning("Document {DocumentId}: the LLM's answer for the {Type} document could not be used ({Reason}); it goes on to review as before.", document.Id, type.TypeKey, ex.Message);
            }

            return (null, null, null);
        }
    }

    /// <summary>Whether the envelope at a top-level or group path (no rows) of <paramref name="tree"/> is Missing or absent.</summary>
    private static bool IsMissing(JsonObject tree, string path)
    {
        JsonNode? node = tree;
        foreach (var segment in path.Split('.'))
        {
            node = node is JsonObject obj ? obj[segment] : null;
        }

        return node is null || DefinitionTree.StatusOf(node) == Domain.Fields.FieldStatus.Missing;
    }

    /// <summary>
    /// A definition-driven type the LLM extracted (docs/11 §9.3): the mapped tree (over the pre-fill's values, which a
    /// fill-missing answer never replaces), completed with the definition's fields, stored as schema version 2 with the
    /// role columns, the field index, the cost and the schema in <c>AppliedConfigJson.llmSchema</c>.
    /// </summary>
    private async Task StoreDefinitionLlmTreeAsync(Document document, EffectiveType type, ExtractionOutcome outcome, ExtractionInput input, PrefillApplication? prefill, ResolvedPipeline pipeline, string tenantCurrency, CancellationToken cancellationToken)
    {
        var tree = prefill is null ? outcome.Document.Root : LayoutExtractor.Merge(prefill.Document.Root, outcome.Document.Root);
        DefinitionTree.Complete(tree, type.Compiled!.Descriptors);
        // docs/11 §5.8: the type's defaults step fills what the extraction left empty, before the lookups read the tree.
        await ExtractDefaults.RunAsync(Db, document, type, pipeline, tree, tenantCurrency, cancellationToken).ConfigureAwait(false);
        var lookupRun = await lookups.RunAsync(document, type, pipeline, tree, typedRecord: false, tenantCurrency, cancellationToken).ConfigureAwait(false);
        Apply(document, new ExtractedDocument(tree), type.Roles, definitionDriven: true);
        document.ExtractionMethod = ExtractionMethod.Llm;
        AddCost(document.Costs, outcome.Cost);
        await DocumentFieldIndexWriter.RefreshAsync(Db, document, type.IndexedFields, cancellationToken).ConfigureAwait(false);
        await DocumentTableReports.RemoveAllAsync(Db, document.Id, cancellationToken).ConfigureAwait(false);
        document.AppliedConfigJson = AppliedConfig(type, input.Definition?.LlmSchema?.ToString(), prefill: prefill?.AppliedConfig(filledByLlm: true), lookups: lookupRun.Applied);
        flags.Replace(document, OwnedFlags, [.. outcome.Flags, .. lookupRun.Flags]);
        Timeline.Transition(document, DocumentStatus.Extracted, new
        {
            method = ExtractionMethod.Llm,
            definition = new { key = type.TypeKey, version = type.Version },
            schema = input.Definition?.LlmSchema?.ToString(),
            prefilled = prefill is not null,
            cost = new { outcome.Cost.LlmInputTokens, outcome.Cost.LlmOutputTokens, outcome.Cost.Usd, outcome.Cost.LlmProvider, outcome.Cost.LlmModel, outcome.Cost.KeyOwner },
            lookups = lookupRun.Summary,
        });
        await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The table report of every group the layout's <c>column-table</c> wrote (docs/11 §6.2, <c>Origin=Layout</c>).</summary>
    private async Task StoreLayoutReportsAsync(Document document, LayoutApplication application, CancellationToken cancellationToken)
    {
        foreach (var (path, table) in application.Extraction.Tables)
        {
            if (table.Rows.Count == 0 || !application.Written.ContainsKey(path))
            {
                continue;
            }

            await DocumentTableReports.ReplaceAsync(Db, new DocumentTableReport
            {
                TenantId = document.TenantId,
                DocumentId = document.Id,
                Path = path,
                Origin = TableReportOrigin.Layout,
                StepId = LayoutExtractor.StepId,
                ProducedAtVersion = document.Version + 1,
                ReportJson = LayoutExtractor.ReportJson(table.Report),
                CreatedAt = Timeline.Now,
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Whether the stored tree was resolved for <paramref name="type"/>'s key (<c>AppliedConfigJson.typeKey</c>); a tree without a record of it is the document's type's.</summary>
    private static bool IsTreeOf(Document document, EffectiveType type)
    {
        if (string.IsNullOrWhiteSpace(document.AppliedConfigJson))
        {
            return true;
        }

        try
        {
            return JsonNode.Parse(document.AppliedConfigJson) is not JsonObject applied
                   || applied["typeKey"] is not JsonValue key
                   || !key.TryGetValue<string>(out var typeKey)
                   || string.Equals(typeKey, type.TypeKey, StringComparison.Ordinal);
        }
        catch (System.Text.Json.JsonException)
        {
            return true;
        }
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
    private string? AppliedConfig(EffectiveType? type, string? llmSchema, JsonObject? layout = null, JsonObject? prefill = null, JsonObject? lookups = null)
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

        if (layout is not null)
        {
            applied["layout"] = layout;
        }

        if (prefill is not null)
        {
            applied["prefill"] = prefill;
        }

        if (lookups is not null)
        {
            applied["lookups"] = lookups;
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
