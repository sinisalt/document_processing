using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DocProc.Application.Abstractions;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
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
/// <para>
/// The <c>llm</c> step's <c>exclude</c> leaves top-level fields out of the schema (a price list's <c>lines</c>, which
/// the layout's table reads, docs/11 §5.8). With <c>lines: perPage</c> the header is asked for once without the
/// top-level tables, and the tables page by page: one call per page with that page only (its image and text layer, or
/// its text alone), the rows appended in page order. Before every page call the cost so far is checked against the
/// step's <c>maxCostPerDocument</c>; once it is reached the remaining pages are not read
/// (<see cref="ExtractionOutcome.PagesNotRead"/>).
/// </para>
/// </summary>
public sealed class LlmDefinitionExtractor(
    ILlmGateway gateway,
    PromptCatalogue prompts,
    PriceTable prices,
    LlmExtractionOptions options) : IDefinitionExtractor
{
    /// <summary>The most pages a page-by-page extraction reads, whatever the budget.</summary>
    public const int MaxPages = 60;

    public async Task<ExtractionOutcome> ExtractAsync(DefinitionExtractionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var input = request.Input;
        var compiled = request.Compiled;
        var document = DocumentInputFactory.ForExtraction(input);
        var fields = compiled.Descriptors.Roots.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var tables = compiled.Descriptors.Roots.Where(f => f.Kind == FieldKinds.Array).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var excluded = request.Exclude.ToHashSet(StringComparer.Ordinal);
        // A key of the schema that is no field of the type (unreadableRegions) is asked for in every call.
        Func<string, bool> headerKeep = request.LinesPerPage
            ? key => !fields.Contains(key) || (!excluded.Contains(key) && !tables.Contains(key))
            : key => !fields.Contains(key) || !excluded.Contains(key);
        Func<string, bool> rowsKeep = key => !fields.Contains(key) || tables.Contains(key);

        var header = await CallAsync(request, document, Reduce(compiled.LlmSchema, headerKeep), headerKeep, request.Guidance, request.FillPaths, cancellationToken).ConfigureAwait(false);
        var tree = header.Tree;
        var usage = header.Usage;
        var result = header.Result;
        var notRead = 0;
        if (request.LinesPerPage && tables.Count > 0)
        {
            var rowsSchema = Reduce(compiled.LlmSchema, rowsKeep);
            var pages = Math.Min(MaxPages, Math.Max(document.PageCount, input.PageImages.Count));
            for (var page = 1; page <= pages; page++)
            {
                if (request.MaxCostUsd is { } limit && request.SpentUsd + (prices.Estimate(result.Model, usage) ?? 0m) >= limit)
                {
                    notRead = pages - page + 1;
                    break;
                }

                var guidance = $"{request.Guidance}\nRead only the rows of the tables printed on page {page.ToString(CultureInfo.InvariantCulture)} of {pages.ToString(CultureInfo.InvariantCulture)} (the page attached); return an empty list when the page has none.".Trim();
                var rows = await CallAsync(request, PageOnly(input, document, page), rowsSchema, rowsKeep, guidance, null, cancellationToken).ConfigureAwait(false);
                usage += rows.Usage;
                result = rows.Result;
                foreach (var key in tables)
                {
                    if (rows.Tree[key] is JsonArray found && found.Count > 0)
                    {
                        var target = tree[key] as JsonArray ?? [];
                        foreach (var row in found.ToList())
                        {
                            found.Remove(row);
                            if (target.Count < DefinitionMapper.MaxRows)
                            {
                                target.Add(row);
                            }
                        }

                        tree[key] = target;
                    }
                }
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
        // The model's unreadable regions are not flagged here: the Normalize stage owns UNREADABLE_PAGES, and a field the
        // model could not read stays Missing, which the structural check and the reviewer see.
        return new ExtractionOutcome(new ExtractedDocument(tree), ExtractionMethod.Llm, [], cost) { PagesNotRead = notRead };
    }

    private sealed record Call(JsonObject Tree, LlmUsage Usage, LlmResult Result);

    private async Task<Call> CallAsync(DefinitionExtractionRequest request, DocumentInput document, CompiledLlmSchema llmSchema, Func<string, bool> keep, string? guidance, IReadOnlyList<string>? fillPaths, CancellationToken cancellationToken)
    {
        var input = request.Input;
        var schema = DefinitionSchemaCompiler.ToDefinition(llmSchema);
        var llmRequest = new LlmRequest
        {
            Stage = LlmStage.Extract,
            Model = LlmRequestDefaults.ModelResolvedByGateway,
            Prompt = prompts.ExtractDocument with
            {
                Task = ExtractionTaskBuilder.BuildForDefinition(input.Tenant, input.Supplier, document, request.TypeName, llmSchema.Name, guidance, fillPaths),
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
        var tree = DefinitionMapper.Map(answer, request.Compiled.Descriptors, new ProvenanceLocator(input.TextLayer), input.Tenant.DefaultCurrency);
        foreach (var key in tree.Select(p => p.Key).Where(k => !keep(k)).ToList())
        {
            tree.Remove(key); // a field the call did not ask for is not taken from its answer
        }

        return new Call(tree, usage, result);
    }

    /// <summary>
    /// The compiled schema with only the top-level properties <paramref name="keep"/> accepts, under a version of its own
    /// (<c>&lt;version&gt;+x.&lt;hash8&gt;</c> of the kept keys); the schema itself when it keeps everything.
    /// </summary>
    public static CompiledLlmSchema Reduce(CompiledLlmSchema schema, Func<string, bool> keep)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(keep);
        if (schema.Schema["properties"] is not JsonObject properties || properties.All(p => keep(p.Key)))
        {
            return schema;
        }

        var reduced = schema.Schema.DeepClone().AsObject();
        var kept = reduced["properties"]!.AsObject();
        foreach (var key in kept.Select(p => p.Key).Where(k => !keep(k)).ToList())
        {
            kept.Remove(key);
        }

        if (reduced["required"] is JsonArray required)
        {
            reduced["required"] = new JsonArray([.. required.Where(r => r is JsonValue v && v.TryGetValue<string>(out var name) && kept.ContainsKey(name)).Select(r => r!.DeepClone())]);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", kept.Select(p => p.Key)))))[..8];
        return schema with { Version = $"{schema.Version}+x.{hash}", Schema = reduced, Hash = $"{schema.Hash}+x.{hash}" };
    }

    /// <summary>One page of the document: its image (when the pipeline rendered pages) and its text layer.</summary>
    private static DocumentInput PageOnly(ExtractionInput input, DocumentInput document, int page)
    {
        var text = page <= document.PageTexts.Count ? document.PageTexts[page - 1] : string.Empty;
        return new DocumentInput
        {
            FileName = document.FileName,
            PageImages = page <= input.PageImages.Count ? [DocumentInputFactory.ToPageImage(1, input.PageImages[page - 1])] : [],
            PageTexts = string.IsNullOrEmpty(text) ? [] : [text],
        };
    }
}
