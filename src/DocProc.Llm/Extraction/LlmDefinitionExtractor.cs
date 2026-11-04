using System.Text.Json.Nodes;
using DocProc.Application.Abstractions;
using DocProc.Domain.Documents;
using DocProc.Llm.Gateway;
using DocProc.Llm.Prompts;
using DocProc.Llm.Schema;

namespace DocProc.Llm.Extraction;

/// <summary>
/// The LLM path of a definition-driven type (docs/11 §9.3 "Other types"): the request's schema is the type version's
/// <c>CompiledLlmSchema</c> (<c>doc_&lt;key&gt;</c>, checked portable at publish), the prompt is
/// <c>extract-document.system.md</c> (the generic rules phrased against "the schema given to you") with a Task of the
/// tenant and supplier context, the definition's guidance and, in <c>fill-missing</c> mode, the paths still to fill.
/// The answer is checked like every answer (refusal, truncation retried once with twice the budget) and mapped into the
/// tree by <see cref="DefinitionMapper"/>. Provider faults propagate untouched (<see cref="LlmException"/>), so the
/// Extract stage decides between retry and going on without the LLM.
/// </summary>
public sealed class LlmDefinitionExtractor(
    ILlmGateway gateway,
    PromptCatalogue prompts,
    PriceTable prices,
    LlmExtractionOptions options) : IDefinitionExtractor
{
    public async Task<ExtractionOutcome> ExtractAsync(DefinitionExtractionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var input = request.Input;
        var compiled = request.Compiled;
        var document = DocumentInputFactory.ForExtraction(input);
        var schema = DefinitionSchemaCompiler.ToDefinition(compiled.LlmSchema);
        var llmRequest = new LlmRequest
        {
            Stage = LlmStage.Extract,
            Model = LlmRequestDefaults.ModelResolvedByGateway,
            Prompt = prompts.ExtractDocument with
            {
                Task = ExtractionTaskBuilder.BuildForDefinition(input.Tenant, input.Supplier, document, request.TypeName, compiled.LlmSchema.Name, request.Guidance, request.FillPaths),
            },
            Document = document.HasPdf || document.HasImages ? document : null,
            OutputSchema = schema,
            Options = LlmInvoiceExtractor.Effort(input.StepParams) is { } effort ? options.ToLlmOptions() with { Effort = effort } : options.ToLlmOptions(),
            DocumentId = input.DocumentId,
        };

        var result = await gateway.CompleteAsync(input.TenantId, llmRequest, cancellationToken).ConfigureAwait(false);
        var usage = result.Usage;
        if (result.IsTruncated)
        {
            var doubled = llmRequest.Options.MaxOutputTokens * 2;
            result = await gateway.CompleteAsync(input.TenantId, llmRequest with { Options = llmRequest.Options with { MaxOutputTokens = doubled } }, cancellationToken).ConfigureAwait(false);
            usage += result.Usage;
        }

        var answer = LlmAnswers.Parse<JsonObject>(result, schema, "extraction");
        var tree = DefinitionMapper.Map(answer, compiled.Descriptors, new ProvenanceLocator(input.TextLayer), input.Tenant.DefaultCurrency);
        var cost = new DocumentCost(
            LlmInputTokens: usage.InputTokens + usage.CacheReadTokens + usage.CacheWriteTokens,
            LlmOutputTokens: usage.OutputTokens + usage.ReasoningTokens,
            OcrPages: 0,
            Usd: prices.Estimate(result.Model, usage),
            LlmProvider: result.Provider?.ToString(),
            LlmModel: result.Model,
            KeyOwner: result.KeyOwner);
        // The model's unreadable regions are not flagged here: the Normalize stage owns UNREADABLE_PAGES, and a field the
        // model could not read stays Missing, which the structural check and the reviewer see.
        return new ExtractionOutcome(new ExtractedDocument(tree), ExtractionMethod.Llm, [], cost);
    }
}
