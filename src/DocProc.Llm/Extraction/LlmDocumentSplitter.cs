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
/// Stage 3 fallback (docs/03): finds document boundaries in a multi-page attachment from page thumbnails and the
/// first lines of each page. The PNGs handed in are the full-size renders of stage 2; this class downscales them
/// (<see cref="LlmExtractionOptions.SplitThumbnailLongEdge"/>) and attaches at most
/// <see cref="LlmExtractionOptions.SplitMaxThumbnailPages"/> of them, so the request stays small whatever the
/// attachment size; the text of every page is still sent. The answer is normalised into contiguous ranges covering
/// every page, so a sloppy model answer can never lose or duplicate a page.
/// </summary>
public sealed class LlmDocumentSplitter(
    ILlmGateway gateway,
    PromptCatalogue prompts,
    LlmExtractionOptions options) : IDocumentSplitter
{
    public Task<IReadOnlyList<PageRange>> SplitAsync(Guid tenantId, TextLayer? textLayer, IReadOnlyList<ReadOnlyMemory<byte>> pagePngs, CancellationToken cancellationToken) =>
        SplitAsync(tenantId, documentId: null, textLayer, pagePngs, cancellationToken);

    /// <summary>As <see cref="SplitAsync(Guid, TextLayer?, IReadOnlyList{ReadOnlyMemory{byte}}, CancellationToken)"/>; the document id correlates the usage row (docs/08).</summary>
    public async Task<IReadOnlyList<PageRange>> SplitAsync(Guid tenantId, Guid? documentId, TextLayer? textLayer, IReadOnlyList<ReadOnlyMemory<byte>> pagePngs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pagePngs);

        var texts = DocumentInputFactory.PageTexts(textLayer);
        var pageCount = Math.Max(pagePngs.Count, texts.Count);
        if (pageCount <= 1)
        {
            return [new PageRange(1, Math.Max(pageCount, 1))];
        }

        var thumbnails = Thumbnails(pagePngs);
        var request = new LlmRequest
        {
            Stage = LlmStage.Split,
            Model = LlmRequestDefaults.ModelResolvedByGateway,
            Prompt = prompts.SplitDocument with { Task = BuildTask(texts, pageCount, thumbnails.Count, pagePngs.Count, options.SplitTextCharsPerPage) },
            Document = thumbnails.Count > 0 ? DocumentInputFactory.ForPages(thumbnails, "pages.pdf") : null,
            OutputSchema = CanonicalSchemas.Split,
            Options = options.ToAuxiliaryLlmOptions(),
            DocumentId = documentId,
        };

        var result = await gateway.CompleteAsync(tenantId, request, cancellationToken).ConfigureAwait(false);
        var parsed = LlmAnswers.Parse<SplitResult>(result, CanonicalSchemas.Split, "split");
        return SplitRangeNormalizer.Normalize(parsed.Documents.Select(d => new PageRange(d.StartPage, d.EndPage)), pageCount);
    }

    private List<ReadOnlyMemory<byte>> Thumbnails(IReadOnlyList<ReadOnlyMemory<byte>> pagePngs)
    {
        var limit = options.SplitMaxThumbnailPages > 0 ? Math.Min(options.SplitMaxThumbnailPages, pagePngs.Count) : 0;
        var thumbnails = new List<ReadOnlyMemory<byte>>(limit);
        for (var i = 0; i < limit; i++)
        {
            thumbnails.Add(PageThumbnails.Downscale(pagePngs[i], options.SplitThumbnailLongEdge));
        }

        return thumbnails;
    }

    private static string BuildTask(IReadOnlyList<string> texts, int pageCount, int imageCount, int renderedCount, int charsPerPage)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Split this {pageCount}-page attachment into its documents.");
        if (imageCount > 0 && imageCount < renderedCount)
        {
            sb.Append(CultureInfo.InvariantCulture, $" Only the first {imageCount} of {renderedCount} page thumbnails are attached, in page order; decide the remaining pages from their text alone.");
        }
        else if (imageCount > 0)
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
