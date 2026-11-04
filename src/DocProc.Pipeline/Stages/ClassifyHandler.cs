using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Application.Suppliers;
using DocProc.Definitions.Pipelines;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;
using DocProc.Extraction.EInvoice;
using DocProc.Infrastructure.Classification;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm.Extraction;
using DocProc.Mapping.Identification;
using DocProc.Pipeline.Layouts;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocProc.Pipeline.Stages;

/// <summary>
/// Stage 4 (docs/03 "Classify"): e-invoices take their type from the XML (<see cref="TypeSource.Structured"/>), and a
/// PDF whose embedded XML cannot be read falls back to its pages;
/// everything else goes through the keyword rules and, below <see cref="PipelineOptions.LlmClassificationThreshold"/>,
/// the LLM classifier with the first page image. <c>Unknown</c> is still a classification: the document carries
/// <c>TYPE_UNKNOWN</c> and review decides. A type a reviewer set (<see cref="TypeSource.Human"/>) is kept as it is
/// (docs/11 §9.4 rule 1), with its version pin and tree.
/// <para>
/// With <c>supplierLayouts</c> on, <c>classify.identification</c> is the fixed first step (docs/11 §9.4, §10.1) for a
/// supplier known from the source (a source rule, the channel default, a hint, a reviewer): the supplier's layouts
/// decide the layout and, unless the type is fixed, the type (<see cref="TypeSource.Identification"/>), and the keyword
/// rules and the LLM are not asked. When the supplier is layout-managed and none holds, they run only to propose a type
/// (<see cref="TypeSource.Proposed"/>) for the <c>NEW_LAYOUT</c> review.
/// </para>
/// </summary>
internal sealed class ClassifyHandler(
    DocProcDbContext db,
    DocumentTimeline timeline,
    DocumentFlagWriter flags,
    PipelineBlobs blobs,
    KeywordDocumentClassifier keywords,
    IEInvoiceParser eInvoices,
    IServiceProvider services,
    LayoutIdentification layouts,
    IOptions<PipelineOptions> options,
    StagePipeline pipelines,
    ILogger<ClassifyHandler> logger) : DocumentStageHandler(db, timeline, logger)
{
    // LLM_PROVIDER_UNAVAILABLE is written by the failure recorder when this stage's LLM call gives up; a later run that
    // succeeds owns it too, so the flag does not outlive the outage.
    private static readonly string[] OwnedFlags = [FlagCodes.TypeUnknown, FlagCodes.LlmProviderUnavailable];
    private readonly PipelineOptions _options = options.Value;

    public override PipelineStage Stage => PipelineStage.Classify;

    protected override bool Accepts(DocumentStatus status) => status == DocumentStatus.Split;

    protected override async Task<StageOutcome> RunAsync(Document document, ProcessDocument message, CancellationToken cancellationToken)
    {
        if (document.TypeSource == TypeSource.Human)
        {
            // docs/11 §9.4 rule 1: a type a reviewer set (:set-type, a typed split part) stands. A reprocess from Classify or
            // Normalize keeps it with its version pin and its tree of the type's fields (a price list captured with the
            // table tool keeps its rows), and the classifier is not asked.
            return await KeepAsync(document, cancellationToken).ConfigureAwait(false);
        }

        if (document.TypeSource == TypeSource.Supplier && document.EmbeddedXmlBlob is null)
        {
            // docs/11 §7.3: a type the supplier configuration pinned at ingest (a source rule's pinTypeKey, the channel's
            // default type, the supplier's single allowed type) is not classified again. An e-invoice still takes its
            // type from the XML, which carries it (§8.4).
            return await KeepAsync(document, cancellationToken).ConfigureAwait(false);
        }

        ClassificationOutcome? outcome = null;
        var usedLlm = false;
        string? xmlIgnored = null;
        (LayoutResolverResult Result, LayoutSetting Setting, DocProc.Mapping.Identification.DocumentWords Words)? identification = null;
        if (document.EmbeddedXmlBlob is { } xmlBlob)
        {
            try
            {
                outcome = await ClassifyEInvoiceAsync(xmlBlob, cancellationToken).ConfigureAwait(false);
            }
            catch (StageFailedException ex) when (HasPages(document))
            {
                // The XML is a fast path, not a gate (docs/03): a PDF whose attachment cannot be read still has its
                // pages, which the classifier and the extractor can use. A bare XML file has nothing else and fails.
                xmlIgnored = ex.Message;
                if (Logger.IsEnabled(LogLevel.Warning))
                {
                    Logger.LogWarning("Document {DocumentId}: {Reason} Classifying from the pages instead.", document.Id, ex.Message);
                }
            }
        }

        if (outcome is null && KnownFromSource(document))
        {
            // classify.identification (docs/11 §9.4 step 2): a layout whose rule holds decides the type, and nothing else is asked.
            identification = await layouts.ResolveAsync(document, fixedType: null, cancellationToken).ConfigureAwait(false);
            if (identification?.Result is { Resolution: LayoutResolution.Identified, Selected: { } selected })
            {
                outcome = new ClassificationOutcome(DocumentTypeKey.FromStored(selected.Candidate.DocumentTypeKey), 1.0, TypeSource.Identification);
            }
        }
        else if (outcome is not null && KnownFromSource(document))
        {
            // An e-invoice keeps the type its XML carries; only a layout restricted to e-invoices is looked for (§8.4).
            identification = await layouts.ResolveAsync(document, outcome.Type.Value, cancellationToken).ConfigureAwait(false);
        }

        DocProc.Llm.Gateway.LlmBudgetExceededException? budget = null;
        if (outcome is null)
        {
            var allowed = KnownFromSource(document) ? await AllowedTypesAsync(Db, document.SupplierId, cancellationToken).ConfigureAwait(false) : null;
            // The classify steps of the document's pipeline (docs/11 §10.1): a supplier the source named may switch the
            // keyword rules or the LLM off, or set the LLM's threshold; classify.identification above is fixed.
            var pipeline = await pipelines.ResolveAsync(document, null, PipelinePhases.Classify, cancellationToken).ConfigureAwait(false);
            var tenantTypes = await services.GetRequiredService<TenantClassification>().ResolveAsync(document.TenantId, cancellationToken).ConfigureAwait(false);
            (outcome, usedLlm, budget) = await ClassifyPagesAsync(document, blobs, keywords, services, _options, cancellationToken, allowed, pipeline, tenantTypes).ConfigureAwait(false);
            if (identification?.Result.Resolution == LayoutResolution.NewLayout && outcome.Type != DocumentTypeKey.Unknown)
            {
                // Under NEW_LAYOUT the classifiers only propose (§8.4 step 5): the type never counts as decided.
                outcome = outcome with { Source = TypeSource.Proposed };
            }
        }

        SetType(document, outcome);
        await RecordIdentificationAsync(document, identification, cancellationToken).ConfigureAwait(false);
        pipelines.RecordBudget(document, StepKindCatalogue.ClassifyLlm, budget);
        flags.Replace(document, OwnedFlags, outcome.Type == DocumentTypeKey.Unknown
            ? [FlagCodes.Create(FlagCodes.TypeUnknown, "The document type could not be determined.")]
            : []);

        Timeline.Transition(document, DocumentStatus.Classified, new
        {
            type = outcome.Type,
            confidence = outcome.Confidence,
            source = outcome.Source,
            language = outcome.Language,
            usedLlm,
            llmBudgetExceeded = budget is null ? (bool?)null : true,
            embeddedXmlIgnored = xmlIgnored,
            layout = identification?.Result.Resolution.ToString(),
        });
        await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return StageOutcome.Advanced;
    }

    /// <summary>
    /// The page path of classification (docs/03 "Classify"): the keyword rules over the text layer and, below
    /// <see cref="PipelineOptions.LlmClassificationThreshold"/> or for Unknown, the LLM classifier with the first page
    /// image. Shared with the Match stage, which classifies again when content evidence replaced a source supplier whose
    /// configuration had pinned the type (docs/11 §7.4). With <paramref name="allowedTypes"/> (the supplier's
    /// <c>allowedTypeKeys</c>, docs/11 §7.3) the keyword rules choose among those types only, and an LLM answer outside
    /// them is Unknown (the LLM's own enum restricted to the set comes with P5's classifier).
    /// <para>
    /// The <paramref name="pipeline"/>'s classify steps (docs/11 §10.1) decide whether the keyword rules
    /// (<c>classify.keyword</c>) and the LLM (<c>classify.llm</c>, with its <c>threshold</c>) are asked; without one both
    /// are, as before P5. An LLM call the tenant's budget refused (docs/11 §16) is not made: the keyword outcome stands,
    /// and the refusal comes back for the stage's <c>LLM_BUDGET_EXCEEDED</c>.
    /// </para>
    /// </summary>
    internal static async Task<(ClassificationOutcome Outcome, bool UsedLlm, DocProc.Llm.Gateway.LlmBudgetExceededException? Budget)> ClassifyPagesAsync(
        Document document,
        PipelineBlobs blobs,
        KeywordDocumentClassifier keywords,
        IServiceProvider services,
        PipelineOptions options,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? allowedTypes = null,
        ResolvedPipeline? pipeline = null,
        TenantClassificationSet? tenantTypes = null)
    {
        var textLayer = await blobs.ReadTextLayerAsync(document, cancellationToken).ConfigureAwait(false);
        // The tenant keyword set (docs/11 §9.4, customTypes): the rules of the tenant's published types; without it the built-in table.
        var outcome = pipeline is null || pipeline.Runs(StepKindCatalogue.ClassifyKeyword)
            ? keywords.Classify(textLayer, allowedTypes, tenantTypes?.Keywords ?? KeywordRuleTable.Default)
            : new ClassificationOutcome(DocumentTypeKey.Unknown, 0, TypeSource.Rule);
        var usedLlm = false;
        var llmStep = pipeline is null ? null : pipeline.Enabled(StepKindCatalogue.ClassifyLlm);
        var threshold = llmStep?.Number("threshold") ?? options.LlmClassificationThreshold;
        if ((outcome.Type == DocumentTypeKey.Unknown || outcome.Confidence < threshold)
            && (pipeline is null || llmStep is not null)
            && services.GetService<LlmDocumentClassifier>() is { } llm)
        {
            var firstPage = document.PageImageKeys.Count > 0
                ? await blobs.ReadDerivedAsync(document.PageImageKeys[0], cancellationToken).ConfigureAwait(false)
                : (ReadOnlyMemory<byte>?)null;
            ClassificationOutcome fromLlm;
            try
            {
                // With the tenant's types the LLM's enum is those types (the supplier's allowed ones when it names any) plus Unknown.
                fromLlm = tenantTypes is null
                    ? await llm.ClassifyAsync(document.TenantId, document.Id, textLayer, firstPage, cancellationToken).ConfigureAwait(false)
                    : await llm.ClassifyAsync(document.TenantId, document.Id, textLayer, firstPage, tenantTypes.CandidatesFor(allowedTypes), cancellationToken).ConfigureAwait(false);
            }
            catch (DocProc.Llm.Gateway.LlmBudgetExceededException exception)
            {
                return (outcome, false, exception);
            }

            usedLlm = true;
            if (fromLlm.Type != DocumentTypeKey.Unknown && (allowedTypes is not { Count: > 0 } || allowedTypes.Contains(fromLlm.Type.Value, StringComparer.Ordinal)))
            {
                outcome = fromLlm with { Language = fromLlm.Language ?? outcome.Language };
            }
        }

        return (outcome, usedLlm, null);
    }

    /// <summary>
    /// The supplier's allowed types (its <c>allowedTypeKeys</c> over its group's, docs/11 §7.3), or null when it names
    /// none or there is no supplier: what the page classification is restricted to (plus <c>Unknown</c>).
    /// </summary>
    internal static async Task<IReadOnlyCollection<string>?> AllowedTypesAsync(DocProcDbContext db, Guid? supplierId, CancellationToken cancellationToken)
    {
        if (supplierId is not { } id)
        {
            return null;
        }

        var row = await db.Suppliers.AsNoTracking().Where(s => s.Id == id)
            .Select(s => new { s.SettingsJson, GroupSettings = s.Group != null ? s.Group.SettingsJson : null })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var allowed = SupplierSettings.Parse(row.SettingsJson).Over(SupplierSettings.Parse(row.GroupSettings)).AllowedTypeKeys;
        return allowed is { Count: > 0 } ? [.. allowed.Select(k => DocumentTypeKey.TryParse(k, out var key) ? key.Value : k)] : null;
    }

    /// <summary>Gives the document the outcome's type; a different type drops the version pin and a definition tree of the old type.</summary>
    internal static void SetType(Document document, ClassificationOutcome outcome)
    {
        if (document.Type != outcome.Type)
        {
            // A version of another type is no pin for this one; Extract pins the new type's version.
            document.DocumentTypeVersionId = null;
            if (document.CanonicalSchemaVersion == DocProc.Application.Documents.DefinitionTree.SchemaVersion)
            {
                // A tree of the type's fields (docs/11 §5.6) describes a type the document no longer has; Extract
                // starts the new type's tree, and keeps a tree it finds only for the same type.
                document.CanonicalJson = null;
                document.CanonicalSchemaVersion = null;
            }
        }

        document.Type = outcome.Type;
        document.TypeConfidence = outcome.Confidence;
        document.TypeSource = outcome.Source;
    }

    /// <summary>Whether the supplier was known before Classify (docs/11 §8.4 "Where it runs"): from the source or from a reviewer.</summary>
    internal static bool KnownFromSource(Document document) =>
        document.SupplierId is not null
        && document.SupplierMatchMethod is SupplierMatchMethod.SourceRule or SupplierMatchMethod.ChannelDefault or SupplierMatchMethod.Hint or SupplierMatchMethod.Human;

    /// <summary>Records <c>classify.identification</c>'s outcome, or clears what an earlier run decided when the step did not run.</summary>
    private async Task RecordIdentificationAsync(Document document, (LayoutResolverResult Result, LayoutSetting Setting, DocProc.Mapping.Identification.DocumentWords Words)? identification, CancellationToken cancellationToken)
    {
        if (identification is { } found)
        {
            var proposed = document.TypeSource == TypeSource.Proposed || LayoutIdentification.IsTypeFixed(document) ? document.Type.Value : null;
            await layouts.RecordAsync(document, LayoutIdentification.ClassifyStep, found.Result, found.Setting, found.Words, proposed, cancellationToken).ConfigureAwait(false);
            return;
        }

        document.SupplierLayoutVersionId = null;
        layouts.Clear(document, LayoutIdentification.ClassifyStep);
    }

    private async Task<StageOutcome> KeepAsync(Document document, CancellationToken cancellationToken)
    {
        // Only the layout is resolved among the kept type's layouts (docs/11 §9.4 rule 1).
        var identification = KnownFromSource(document) && !document.Type.IsUnknown
            ? await layouts.ResolveAsync(document, document.Type.Value, cancellationToken).ConfigureAwait(false)
            : null;
        await RecordIdentificationAsync(document, identification, cancellationToken).ConfigureAwait(false);
        flags.Replace(document, OwnedFlags, document.Type == DocumentTypeKey.Unknown
            ? [FlagCodes.Create(FlagCodes.TypeUnknown, "The document type could not be determined.")]
            : []);
        Timeline.Transition(document, DocumentStatus.Classified, new
        {
            type = document.Type,
            confidence = document.TypeConfidence,
            source = document.TypeSource,
            kept = true,
            layout = identification?.Result.Resolution.ToString(),
        });
        await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return StageOutcome.Advanced;
    }

    private static bool HasPages(Document document) => document.PageImageKeys.Count > 0 || document.TextLayerBlob is not null;

    private async Task<ClassificationOutcome> ClassifyEInvoiceAsync(BlobReference xmlBlob, CancellationToken cancellationToken)
    {
        var xml = await blobs.ReadAsync(xmlBlob, cancellationToken).ConfigureAwait(false);
        var detection = eInvoices.Detect(xml)
            ?? throw new StageFailedException("The stored e-invoice XML is not a UBL or CII document.");

        // The UBL root names the type; CII carries it in the type code, which the parser maps to the invoice kind.
        InvoiceKind kind;
        if (detection.RootElement.Equals("CreditNote", StringComparison.Ordinal))
        {
            kind = InvoiceKind.CreditNote;
        }
        else
        {
            try
            {
                kind = eInvoices.Parse(xml, detection).Kind.Value;
            }
            catch (EInvoiceParseException ex)
            {
                // Malformed or truncated XML is a property of the document, not of the moment: no retry can fix it.
                throw new StageFailedException($"The stored e-invoice XML could not be parsed: {ex.Message}", ex);
            }
        }

        var type = kind == InvoiceKind.CreditNote ? DocumentTypeKey.CreditNote : DocumentTypeKey.Invoice;
        return new ClassificationOutcome(type, 1.0, TypeSource.Structured);
    }
}
