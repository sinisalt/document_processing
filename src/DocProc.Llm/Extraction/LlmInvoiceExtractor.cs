using System.Text.Json;
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
/// <para>
/// For an invoice-family type the tenant added fields to (docs/11 §9.1, <see cref="ResolvedDefinition.HasCustomFields"/>)
/// the request carries the type's compiled schema: <c>invoice_extraction@1</c> plus the nullable <c>x_custom</c> object
/// of those fields (<c>invoice_extraction@1+x.&lt;hash8&gt;</c>), and the answer's <c>x_custom</c> values are stored as
/// fields of the tree (<see cref="CustomFieldMapper"/>). Without added fields the request is exactly today's.
/// </para>
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
        return input.Type.IsInvoiceFamily
               && (input.Pdf is { Length: > 0 } || input.PageImages.Count > 0 || input.TextLayer is { WordCount: > 0 });
    }

    public async Task<ExtractionOutcome?> ExtractAsync(ExtractionInput input, CancellationToken cancellationToken)
    {
        if (!CanHandle(input))
        {
            return null;
        }

        var document = DocumentInputFactory.ForExtraction(input);
        var custom = input.Definition is { HasCustomFields: true } definition ? definition : null;
        var schema = custom is null ? CanonicalSchemas.InvoiceExtraction : DefinitionSchemaCompiler.ToDefinition(custom.LlmSchema!);
        var request = new LlmRequest
        {
            Stage = LlmStage.Extract,
            Model = LlmRequestDefaults.ModelResolvedByGateway,
            Prompt = prompts.ExtractInvoice with { Task = ExtractionTaskBuilder.Build(input.Tenant, input.Supplier, document) },
            // Text-only documents travel inside the task text (see the builder); otherwise adapters pick PDF or images.
            Document = document.HasPdf || document.HasImages ? document : null,
            OutputSchema = schema,
            Options = options.ToLlmOptions(),
            DocumentId = input.DocumentId,
        };

        var (result, usage) = await CompleteAsync(input.TenantId, request, cancellationToken).ConfigureAwait(false);
        var extraction = LlmAnswers.Parse<InvoiceExtraction>(result, schema, "extraction");
        var locator = new ProvenanceLocator(input.TextLayer);
        var mapper = new InvoiceExtractionMapper(locator, input.Tenant.DefaultCurrency);
        var mapped = mapper.Map(extraction);
        var invoice = mapped.Invoice;
        if (custom is not null)
        {
            var added = CustomFieldMapper.Map(result.Json, custom.CustomFields, locator, invoice.Currency.Value);
            if (added.Count > 0)
            {
                var extra = invoice.Extra is null ? new Dictionary<string, JsonElement>(StringComparer.Ordinal) : new Dictionary<string, JsonElement>(invoice.Extra, StringComparer.Ordinal);
                foreach (var (key, value) in added)
                {
                    extra[key] = value;
                }

                invoice = invoice with { Extra = extra };
            }
        }

        var cost = new DocumentCost(
            LlmInputTokens: usage.InputTokens + usage.CacheReadTokens + usage.CacheWriteTokens,
            LlmOutputTokens: usage.OutputTokens + usage.ReasoningTokens,
            OcrPages: 0,
            Usd: prices.Estimate(result.Model, usage),
            LlmProvider: result.Provider?.ToString(),
            LlmModel: result.Model,
            KeyOwner: result.KeyOwner);

        return new ExtractionOutcome(invoice, ExtractionMethod.Llm, mapped.Flags, cost);
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
