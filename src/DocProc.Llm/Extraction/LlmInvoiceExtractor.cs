using DocProc.Application.Abstractions;
using DocProc.Domain.Documents;
using DocProc.Llm.Contracts;
using DocProc.Llm.Gateway;
using DocProc.Llm.Prompts;
using DocProc.Llm.Schema;

namespace DocProc.Llm.Extraction;

/// <summary>
/// Stage 6c (docs/03): the schema-constrained LLM path. Builds the request from the pipeline's input, calls the
/// gateway (which resolves provider and model per tenant), parses the answer and maps it onto the canonical model
/// with located provenance. A truncated answer is retried once with twice the output budget; provider faults
/// (<see cref="LlmException"/>) propagate untouched so the pipeline can decide between retry and
/// LLM_PROVIDER_UNAVAILABLE.
/// </summary>
public sealed class LlmInvoiceExtractor(
    ILlmGateway gateway,
    PromptCatalogue prompts,
    PriceTable prices,
    LlmExtractionOptions options) : IDocumentExtractor
{
    public ExtractionMethod Method => ExtractionMethod.Llm;

    public bool CanHandle(ExtractionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.Type is DocumentType.Invoice or DocumentType.CreditNote
               && (input.Pdf is { Length: > 0 } || input.PageImages.Count > 0 || input.TextLayer is { WordCount: > 0 });
    }

    public async Task<ExtractionOutcome?> ExtractAsync(ExtractionInput input, CancellationToken cancellationToken)
    {
        if (!CanHandle(input))
        {
            return null;
        }

        var document = DocumentInputFactory.ForExtraction(input);
        var request = new LlmRequest
        {
            Stage = LlmStage.Extract,
            Model = LlmRequestDefaults.ModelResolvedByGateway,
            Prompt = prompts.ExtractInvoice with { Task = ExtractionTaskBuilder.Build(input.Tenant, input.Supplier, document) },
            // Text-only documents travel inside the task text (see the builder); otherwise adapters pick PDF or images.
            Document = document.HasPdf || document.HasImages ? document : null,
            OutputSchema = CanonicalSchemas.InvoiceExtraction,
            Options = options.ToLlmOptions(),
            DocumentId = input.DocumentId,
        };

        var (result, usage) = await CompleteAsync(input.TenantId, request, cancellationToken).ConfigureAwait(false);
        var extraction = LlmAnswers.Parse<InvoiceExtraction>(result, CanonicalSchemas.InvoiceExtraction, "extraction");
        var mapper = new InvoiceExtractionMapper(new ProvenanceLocator(input.TextLayer), input.Tenant.DefaultCurrency);
        var mapped = mapper.Map(extraction);

        var cost = new DocumentCost(
            LlmInputTokens: usage.InputTokens + usage.CacheReadTokens + usage.CacheWriteTokens,
            LlmOutputTokens: usage.OutputTokens + usage.ReasoningTokens,
            OcrPages: 0,
            Usd: prices.Estimate(result.Model, usage),
            LlmProvider: result.Provider?.ToString(),
            LlmModel: result.Model,
            KeyOwner: result.KeyOwner);

        return new ExtractionOutcome(mapped.Invoice, ExtractionMethod.Llm, mapped.Flags, cost);
    }

    private async Task<(LlmResult Result, LlmUsage Usage)> CompleteAsync(Guid tenantId, LlmRequest request, CancellationToken cancellationToken)
    {
        var result = await gateway.CompleteAsync(tenantId, request, cancellationToken).ConfigureAwait(false);
        var usage = result.Usage;
        if (!result.IsTruncated)
        {
            return (result, usage);
        }

        var doubled = request.Options.MaxOutputTokens * 2;
        var retry = request with { Options = request.Options with { MaxOutputTokens = doubled } };
        result = await gateway.CompleteAsync(tenantId, retry, cancellationToken).ConfigureAwait(false);
        usage += result.Usage;
        if (result.IsTruncated)
        {
            throw new LlmExtractionException(
                $"The extraction answer for document {request.DocumentId} was truncated twice (max output tokens {request.Options.MaxOutputTokens}, then {doubled}). " +
                "The document is too long for one request; split it or raise Llm:Extraction:MaxOutputTokens.");
        }

        return (result, usage);
    }
}
