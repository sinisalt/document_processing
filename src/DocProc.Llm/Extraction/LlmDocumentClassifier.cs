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
            return new ClassificationOutcome(DocumentType.Unknown, 0, TypeSource.Llm);
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

    public static DocumentType Map(ClassifiedDocumentType type) => type switch
    {
        ClassifiedDocumentType.Invoice => DocumentType.Invoice,
        ClassifiedDocumentType.CreditNote => DocumentType.CreditNote,
        ClassifiedDocumentType.PurchaseOrder => DocumentType.PurchaseOrder,
        ClassifiedDocumentType.DeliveryNote => DocumentType.DeliveryNote,
        ClassifiedDocumentType.Statement => DocumentType.Statement,
        ClassifiedDocumentType.Reminder => DocumentType.Reminder,
        _ => DocumentType.Other,
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
