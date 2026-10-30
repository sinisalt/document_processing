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
/// Stage 4 (docs/03 "Classify"): e-invoices take their type from the XML (<see cref="TypeSource.Structured"/>);
/// everything else goes through the keyword rules and, below <see cref="PipelineOptions.LlmClassificationThreshold"/>,
/// the LLM classifier with the first page image. <c>Unknown</c> is still a classification: the document carries
/// <c>TYPE_UNKNOWN</c> and review decides.
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
        ClassificationOutcome outcome;
        var usedLlm = false;
        if (document.EmbeddedXmlBlob is { } xmlBlob)
        {
            outcome = await ClassifyEInvoiceAsync(xmlBlob, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var textLayer = await blobs.ReadTextLayerAsync(document, cancellationToken).ConfigureAwait(false);
            outcome = keywords.Classify(textLayer);
            if ((outcome.Type == DocumentType.Unknown || outcome.Confidence < _options.LlmClassificationThreshold)
                && services.GetService<LlmDocumentClassifier>() is { } llm)
            {
                var firstPage = document.PageImageKeys.Count > 0
                    ? await blobs.ReadDerivedAsync(document.PageImageKeys[0], cancellationToken).ConfigureAwait(false)
                    : (ReadOnlyMemory<byte>?)null;
                var fromLlm = await llm.ClassifyAsync(document.TenantId, document.Id, textLayer, firstPage, cancellationToken).ConfigureAwait(false);
                usedLlm = true;
                if (fromLlm.Type != DocumentType.Unknown)
                {
                    outcome = fromLlm with { Language = fromLlm.Language ?? outcome.Language };
                }
            }
        }

        document.Type = outcome.Type;
        document.TypeConfidence = outcome.Confidence;
        document.TypeSource = outcome.Source;
        flags.Replace(document, OwnedFlags, outcome.Type == DocumentType.Unknown
            ? [FlagCodes.Create(FlagCodes.TypeUnknown, "The document type could not be determined.")]
            : []);

        Timeline.Transition(document, DocumentStatus.Classified, new
        {
            type = outcome.Type,
            confidence = outcome.Confidence,
            source = outcome.Source,
            language = outcome.Language,
            usedLlm,
        });
        await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return StageOutcome.Advanced;
    }

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

        var type = kind == InvoiceKind.CreditNote ? DocumentType.CreditNote : DocumentType.Invoice;
        return new ClassificationOutcome(type, 1.0, TypeSource.Structured);
    }
}
