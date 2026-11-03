using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;
using DocProc.Extraction.EInvoice;
using DocProc.Infrastructure.Classification;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm.Extraction;
using DocProc.Pipeline.Support;
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
/// </summary>
internal sealed class ClassifyHandler(
    DocProcDbContext db,
    DocumentTimeline timeline,
    DocumentFlagWriter flags,
    PipelineBlobs blobs,
    KeywordDocumentClassifier keywords,
    IEInvoiceParser eInvoices,
    IServiceProvider services,
    IOptions<PipelineOptions> options,
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

        ClassificationOutcome? outcome = null;
        var usedLlm = false;
        string? xmlIgnored = null;
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

        if (outcome is null)
        {
            var textLayer = await blobs.ReadTextLayerAsync(document, cancellationToken).ConfigureAwait(false);
            outcome = keywords.Classify(textLayer);
            if ((outcome.Type == DocumentTypeKey.Unknown || outcome.Confidence < _options.LlmClassificationThreshold)
                && services.GetService<LlmDocumentClassifier>() is { } llm)
            {
                var firstPage = document.PageImageKeys.Count > 0
                    ? await blobs.ReadDerivedAsync(document.PageImageKeys[0], cancellationToken).ConfigureAwait(false)
                    : (ReadOnlyMemory<byte>?)null;
                var fromLlm = await llm.ClassifyAsync(document.TenantId, document.Id, textLayer, firstPage, cancellationToken).ConfigureAwait(false);
                usedLlm = true;
                if (fromLlm.Type != DocumentTypeKey.Unknown)
                {
                    outcome = fromLlm with { Language = fromLlm.Language ?? outcome.Language };
                }
            }
        }

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
            embeddedXmlIgnored = xmlIgnored,
        });
        await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return StageOutcome.Advanced;
    }

    private async Task<StageOutcome> KeepAsync(Document document, CancellationToken cancellationToken)
    {
        flags.Replace(document, OwnedFlags, document.Type == DocumentTypeKey.Unknown
            ? [FlagCodes.Create(FlagCodes.TypeUnknown, "The document type could not be determined.")]
            : []);
        Timeline.Transition(document, DocumentStatus.Classified, new
        {
            type = document.Type,
            confidence = document.TypeConfidence,
            source = document.TypeSource,
            kept = true,
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
