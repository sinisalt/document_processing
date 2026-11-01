using DocProc.Domain.Documents;
using DocProc.Domain.Text;

namespace DocProc.Application.Abstractions;

public sealed record ClassificationOutcome(DocumentType Type, double Confidence, TypeSource Source, string? Language = null);

/// <summary>Stage 4 (docs/03): keyword rules over the text layer first, an LLM only below threshold.</summary>
public interface IDocumentClassifier
{
    Task<ClassificationOutcome> ClassifyAsync(Guid tenantId, TextLayer? textLayer, ReadOnlyMemory<byte>? firstPagePng, CancellationToken cancellationToken);

    /// <summary>
    /// As <see cref="ClassifyAsync(Guid, TextLayer?, ReadOnlyMemory{byte}?, CancellationToken)"/> with the document the
    /// call is for, so an implementation that spends money (the LLM classifier) can attribute its usage row to it
    /// (docs/08 "Cost attribution"). Pipeline stages call this overload whenever the document id is known.
    /// </summary>
    Task<ClassificationOutcome> ClassifyAsync(Guid tenantId, Guid? documentId, TextLayer? textLayer, ReadOnlyMemory<byte>? firstPagePng, CancellationToken cancellationToken) =>
        ClassifyAsync(tenantId, textLayer, firstPagePng, cancellationToken);
}

/// <summary>Stage 3 (docs/03): page ranges that are separate business documents.</summary>
public sealed record PageRange(int StartPage, int EndPage);

/// <summary>
/// Stage 3 (docs/03): page ranges that are separate business documents. <c>pagePngs</c> are the rendered pages in
/// page order; an implementation that sends them to a model is responsible for downscaling and capping them.
/// </summary>
public interface IDocumentSplitter
{
    Task<IReadOnlyList<PageRange>> SplitAsync(Guid tenantId, TextLayer? textLayer, IReadOnlyList<ReadOnlyMemory<byte>> pagePngs, CancellationToken cancellationToken);

    /// <summary>
    /// As <see cref="SplitAsync(Guid, TextLayer?, IReadOnlyList{ReadOnlyMemory{byte}}, CancellationToken)"/> with the
    /// attachment's document id for usage attribution (docs/08). Pipeline stages call this overload whenever the id is known.
    /// </summary>
    Task<IReadOnlyList<PageRange>> SplitAsync(Guid tenantId, Guid? documentId, TextLayer? textLayer, IReadOnlyList<ReadOnlyMemory<byte>> pagePngs, CancellationToken cancellationToken) =>
        SplitAsync(tenantId, textLayer, pagePngs, cancellationToken);
}
