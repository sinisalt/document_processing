using DocProc.Domain.Documents;
using DocProc.Domain.Text;

namespace DocProc.Application.Abstractions;

public sealed record ClassificationOutcome(DocumentType Type, double Confidence, TypeSource Source, string? Language = null);

/// <summary>Stage 4 (docs/03): keyword rules over the text layer first, an LLM only below threshold.</summary>
public interface IDocumentClassifier
{
    Task<ClassificationOutcome> ClassifyAsync(Guid tenantId, TextLayer? textLayer, ReadOnlyMemory<byte>? firstPagePng, CancellationToken cancellationToken);
}

/// <summary>Stage 3 (docs/03): page ranges that are separate business documents.</summary>
public sealed record PageRange(int StartPage, int EndPage);

public interface IDocumentSplitter
{
    Task<IReadOnlyList<PageRange>> SplitAsync(Guid tenantId, TextLayer? textLayer, IReadOnlyList<ReadOnlyMemory<byte>> pagePngs, CancellationToken cancellationToken);
}
