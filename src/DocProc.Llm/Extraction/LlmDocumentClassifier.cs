using System.Globalization;
using DocProc.Application.Abstractions;
using DocProc.Domain.Documents;
using DocProc.Domain.Text;
using DocProc.Llm.Contracts;
using DocProc.Llm.Gateway;
using DocProc.Llm.Prompts;
using DocProc.Llm.Schema;

namespace DocProc.Llm.Extraction;

/// <summary>
/// Stage 4 fallback (docs/03): classification by the LLM when keyword rules are below threshold. Sends the first
/// page image and the start of the text layer. The returned confidence is the model's own, clamped and capped: it
/// is weakly calibrated (docs/06 "Confidence") and the stage combines it with rule signals rather than trusting it.
/// </summary>
public sealed class LlmDocumentClassifier(
    ILlmGateway gateway,
    PromptCatalogue prompts,
    LlmExtractionOptions options) : IDocumentClassifier
{
    /// <summary>An LLM-only classification never reaches certainty; TYPE_UNKNOWN thresholds are tenant settings above this.</summary>
    public const double MaxConfidence = 0.95;

    public Task<ClassificationOutcome> ClassifyAsync(Guid tenantId, TextLayer? textLayer, ReadOnlyMemory<byte>? firstPagePng, CancellationToken cancellationToken) =>
        ClassifyAsync(tenantId, documentId: null, textLayer, firstPagePng, cancellationToken);

    /// <summary>As <see cref="ClassifyAsync(Guid, TextLayer?, ReadOnlyMemory{byte}?, CancellationToken)"/>; the document id correlates the usage row (docs/08 "Cost attribution").</summary>
    public async Task<ClassificationOutcome> ClassifyAsync(Guid tenantId, Guid? documentId, TextLayer? textLayer, ReadOnlyMemory<byte>? firstPagePng, CancellationToken cancellationToken)
    {
        var excerpt = Excerpt(textLayer, options.ClassificationTextChars);
        var hasImage = firstPagePng is { Length: > 0 };
        if (excerpt.Length == 0 && !hasImage)
        {
            return new ClassificationOutcome(DocumentTypeKey.Unknown, 0, TypeSource.Llm);
        }

        var request = new LlmRequest
        {
            Stage = LlmStage.Classify,
            Model = LlmRequestDefaults.ModelResolvedByGateway,
            Prompt = prompts.ClassifyDocument with { Task = BuildTask(excerpt, hasImage) },
            Document = hasImage ? DocumentInputFactory.ForPages([firstPagePng!.Value], "first-page.png") : null,
            OutputSchema = CanonicalSchemas.Classification,
            Options = options.ToAuxiliaryLlmOptions(),
            DocumentId = documentId,
        };

        var result = await gateway.CompleteAsync(tenantId, request, cancellationToken).ConfigureAwait(false);
        var parsed = LlmAnswers.Parse<ClassificationResult>(result, CanonicalSchemas.Classification, "classification");
        var confidence = double.IsFinite(parsed.Confidence) ? Math.Clamp(parsed.Confidence, 0, MaxConfidence) : 0;
        return new ClassificationOutcome(Map(parsed.DocumentType), confidence, TypeSource.Llm, Blank(parsed.Language));
    }

    /// <summary>The most types one tenant classification request lists.</summary>
    public const int MaxCandidates = 100;

    /// <summary>
    /// Classification against the tenant's own types (docs/11 §9.4, P5 with <c>customTypes</c>): the schema's
    /// <c>documentType</c> enum is the candidates' keys plus <c>Unknown</c> (<c>document_classification@1+t.&lt;hash8&gt;</c>,
    /// one schema per candidate set), the Task lists each key with its description, and the prompt is
    /// <c>classify-tenant-document</c>. An answer outside the set reads as Unknown.
    /// </summary>
    public async Task<ClassificationOutcome> ClassifyAsync(Guid tenantId, Guid? documentId, TextLayer? textLayer, ReadOnlyMemory<byte>? firstPagePng, IReadOnlyList<ClassificationCandidate> candidates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var excerpt = Excerpt(textLayer, options.ClassificationTextChars);
        var hasImage = firstPagePng is { Length: > 0 };
        var listed = candidates.DistinctBy(c => c.Key, StringComparer.Ordinal).Where(c => c.Key != DocumentTypeKey.Unknown.Value).Take(MaxCandidates).ToList();
        if ((excerpt.Length == 0 && !hasImage) || listed.Count == 0)
        {
            return new ClassificationOutcome(DocumentTypeKey.Unknown, 0, TypeSource.Llm);
        }

        var schema = TenantSchema(listed);
        var task = new System.Text.StringBuilder();
        task.Append("# Document types of this tenant\n");
        foreach (var candidate in listed)
        {
            task.Append("- `").Append(candidate.Key).Append("`: ").Append(Shorten(candidate.Description, 300)).Append('\n');
        }

        task.Append("- `Unknown`: none of the above.\n\n").Append(BuildTask(excerpt, hasImage));
        var request = new LlmRequest
        {
            Stage = LlmStage.Classify,
            Model = LlmRequestDefaults.ModelResolvedByGateway,
            Prompt = prompts.ClassifyTenantDocument with { Task = task.ToString() },
            Document = hasImage ? DocumentInputFactory.ForPages([firstPagePng!.Value], "first-page.png") : null,
            OutputSchema = schema,
            Options = options.ToAuxiliaryLlmOptions(),
            DocumentId = documentId,
        };

        var result = await gateway.CompleteAsync(tenantId, request, cancellationToken).ConfigureAwait(false);
        var parsed = LlmAnswers.Parse<System.Text.Json.Nodes.JsonObject>(result, schema, "classification");
        var key = parsed["documentType"] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var confidence = parsed["confidence"] is System.Text.Json.Nodes.JsonValue c && c.TryGetValue<double>(out var number) && double.IsFinite(number) ? Math.Clamp(number, 0, MaxConfidence) : 0;
        var language = parsed["language"] is System.Text.Json.Nodes.JsonValue l && l.TryGetValue<string>(out var lang) ? Blank(lang) : null;
        var type = key is not null && listed.Any(candidate => candidate.Key == key) && DocumentTypeKey.TryParse(key, out var parsedKey) ? parsedKey : DocumentTypeKey.Unknown;
        return new ClassificationOutcome(type, type == DocumentTypeKey.Unknown ? 0 : confidence, TypeSource.Llm, language);
    }

    /// <summary>The classification schema with the candidates' keys (and <c>Unknown</c>) as the <c>documentType</c> enum.</summary>
    public static JsonSchemaDefinition TenantSchema(IReadOnlyList<ClassificationCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var schema = (System.Text.Json.Nodes.JsonObject)CanonicalSchemas.Classification.Schema.DeepClone();
        var keys = candidates.Select(c => c.Key).Distinct(StringComparer.Ordinal).Append(DocumentTypeKey.Unknown.Value).ToList();
        schema["properties"]!["documentType"]!["enum"] = new System.Text.Json.Nodes.JsonArray([.. keys.Select(k => (System.Text.Json.Nodes.JsonNode?)k)]);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', keys))))[..8];
        return new JsonSchemaDefinition(CanonicalSchemas.Classification.Name, $"{CanonicalSchemas.ClassificationVersion}+t.{hash}", schema);
    }

    private static string Shorten(string text, int max)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..max];
    }

    public static DocumentTypeKey Map(ClassifiedDocumentType type) => type switch
    {
        ClassifiedDocumentType.Invoice => DocumentTypeKey.Invoice,
        ClassifiedDocumentType.CreditNote => DocumentTypeKey.CreditNote,
        ClassifiedDocumentType.PurchaseOrder => DocumentTypeKey.PurchaseOrder,
        ClassifiedDocumentType.DeliveryNote => DocumentTypeKey.DeliveryNote,
        ClassifiedDocumentType.Statement => DocumentTypeKey.Statement,
        ClassifiedDocumentType.Reminder => DocumentTypeKey.Reminder,
        _ => DocumentTypeKey.Other,
    };

    /// <summary>
    /// The first characters of the text layer in page order; the first page carries the title and header. Pages are
    /// appended one at a time and only until the budget is reached, so a long attachment never has its whole text joined.
    /// </summary>
    public static string Excerpt(TextLayer? textLayer, int maxChars)
    {
        if (textLayer is null || maxChars <= 0)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var page in textLayer.Pages.OrderBy(p => p.Number))
        {
            if (sb.Length >= maxChars)
            {
                break;
            }

            var text = page.PlainText.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            var remaining = maxChars - sb.Length;
            sb.Append(text.Length <= remaining ? text : text[..remaining]);
        }

        return sb.ToString().Trim();
    }

    private static string BuildTask(string excerpt, bool hasImage)
    {
        var intro = hasImage
            ? "Classify the attached first page."
            : "Classify the document from its text layer; no page image is available.";
        if (excerpt.Length == 0)
        {
            return intro + " The text layer is empty (scan without OCR text).";
        }

        return string.Create(CultureInfo.InvariantCulture, $"{intro} The first {excerpt.Length} characters of the text layer:\n<text>\n{excerpt}\n</text>");
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
