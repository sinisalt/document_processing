using System.Globalization;
using System.Text;
using DocProc.Application.Abstractions;
using DocProc.Domain.Text;
using DocProc.Llm.Contracts;
using DocProc.Llm.Gateway;
using DocProc.Llm.Prompts;
using DocProc.Llm.Schema;

namespace DocProc.Llm.Extraction;

/// <summary>
/// Stage 3 fallback (docs/03): finds document boundaries in a multi-page attachment from page thumbnails (the
/// PNGs handed in, rendered small by the caller) and the first lines of each page. The answer is normalised into
/// contiguous ranges covering every page, so a sloppy model answer can never lose or duplicate a page.
/// </summary>
public sealed class LlmDocumentSplitter(
    ILlmGateway gateway,
    PromptCatalogue prompts,
    LlmExtractionOptions options) : IDocumentSplitter
{
    public async Task<IReadOnlyList<PageRange>> SplitAsync(Guid tenantId, TextLayer? textLayer, IReadOnlyList<ReadOnlyMemory<byte>> pagePngs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pagePngs);

        var texts = DocumentInputFactory.PageTexts(textLayer);
        var pageCount = Math.Max(pagePngs.Count, texts.Count);
        if (pageCount <= 1)
        {
            return [new PageRange(1, Math.Max(pageCount, 1))];
        }

        var request = new LlmRequest
        {
            Stage = LlmStage.Split,
            Model = LlmRequestDefaults.ModelResolvedByGateway,
            Prompt = prompts.SplitDocument with { Task = BuildTask(texts, pageCount, pagePngs.Count, options.SplitTextCharsPerPage) },
            Document = pagePngs.Count > 0 ? DocumentInputFactory.ForPages(pagePngs, "pages.pdf") : null,
            OutputSchema = CanonicalSchemas.Split,
            Options = options.ToAuxiliaryLlmOptions(),
        };

        var result = await gateway.CompleteAsync(tenantId, request, cancellationToken).ConfigureAwait(false);
        var parsed = LlmAnswers.Parse<SplitResult>(result, CanonicalSchemas.Split, "split");
        return SplitRangeNormalizer.Normalize(parsed.Documents.Select(d => new PageRange(d.StartPage, d.EndPage)), pageCount);
    }

    private static string BuildTask(IReadOnlyList<string> texts, int pageCount, int imageCount, int charsPerPage)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Split this {pageCount}-page attachment into its documents.");
        if (imageCount > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {imageCount} page thumbnails are attached in page order.");
        }
        else
        {
            sb.Append(" No page images are available; decide from the text alone.");
        }

        sb.Append('\n');

        if (texts.Count > 0)
        {
            sb.Append("The first lines of each page:\n");
            for (var i = 0; i < pageCount; i++)
            {
                var text = i < texts.Count ? texts[i].Trim() : string.Empty;
                if (charsPerPage > 0 && text.Length > charsPerPage)
                {
                    text = text[..charsPerPage];
                }

                sb.Append(CultureInfo.InvariantCulture, $"<page number=\"{i + 1}\">\n{text}\n</page>\n");
            }
        }

        return sb.ToString().TrimEnd();
    }
}
