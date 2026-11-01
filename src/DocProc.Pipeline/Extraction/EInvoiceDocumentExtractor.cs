using DocProc.Application.Abstractions;
using DocProc.Domain.Documents;
using DocProc.Extraction.EInvoice;
using Microsoft.Extensions.Logging;

namespace DocProc.Pipeline.Extraction;

/// <summary>
/// Stage 6a (docs/03): the structured path. Applies whenever stage 2 found an e-invoice XML (an attachment inside a
/// PDF/A-3 or a UBL / CII file), parses it with <see cref="IEInvoiceParser"/> and hands the canonical invoice over at
/// zero cost. A detected XML that does not parse is logged and skipped, so the next path (the pages, when there are
/// any) can still try.
/// </summary>
public sealed class EInvoiceDocumentExtractor(IEInvoiceParser parser, ILogger<EInvoiceDocumentExtractor> logger) : IDocumentExtractor
{
    public ExtractionMethod Method => ExtractionMethod.StructuredXml;

    public bool CanHandle(ExtractionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.EmbeddedXml is { Length: > 0 } && input.Type is DocumentType.Invoice or DocumentType.CreditNote;
    }

    public Task<ExtractionOutcome?> ExtractAsync(ExtractionInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanHandle(input))
        {
            return Task.FromResult<ExtractionOutcome?>(null);
        }

        var xml = input.EmbeddedXml!.Value;
        var detection = parser.Detect(xml);
        if (detection is null)
        {
            logger.LogWarning("Document {DocumentId}: the embedded XML is not a recognised e-invoice; structured path skipped.", input.DocumentId);
            return Task.FromResult<ExtractionOutcome?>(null);
        }

        try
        {
            var invoice = parser.Parse(xml, detection);
            return Task.FromResult<ExtractionOutcome?>(new ExtractionOutcome(invoice, ExtractionMethod.StructuredXml, [], DocumentCost.Free));
        }
        catch (EInvoiceParseException ex)
        {
            logger.LogWarning(ex, "Document {DocumentId}: the embedded {Flavour} XML could not be parsed; structured path skipped.", input.DocumentId, detection.Flavour);
            return Task.FromResult<ExtractionOutcome?>(null);
        }
    }
}
