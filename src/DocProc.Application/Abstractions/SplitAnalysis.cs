using DocProc.Domain.Text;

namespace DocProc.Application.Abstractions;

/// <summary>The page-level "document start" signals of docs/03 stage 3, step 1.</summary>
public enum SplitSignalKind
{
    /// <summary>Page numbering restarted ("Seite 1/3", "Page 1 of 2") on a page that is not the first.</summary>
    PageNumberingReset,

    /// <summary>Page numbering continued from the previous page ("Seite 2/3" after "Seite 1/3"): evidence against a split.</summary>
    PageNumberingContinued,

    /// <summary>A document title ("Rechnung", "Invoice", "Lieferschein") reappeared in the header of the page.</summary>
    DocumentHeaderRepeated,

    /// <summary>A document-number label ("Rechnungsnummer", "Invoice no.") reappeared with a different value.</summary>
    DocumentNumberChanged,

    /// <summary>A document-number label reappeared with the same value as before: evidence against a split.</summary>
    DocumentNumberRepeated,

    /// <summary>The supplier header block of the first page repeated on this page together with a title.</summary>
    SupplierHeaderRepeated,

    /// <summary>The page carries (almost) no words and separates two documents.</summary>
    BlankSeparatorPage,

    /// <summary>A carry-over marker ("Übertrag", "continued", "Fortsetzung") sits in the header: evidence against a split.</summary>
    ContinuationMarker,
}

/// <summary>One signal found on a page. <paramref name="Evidence"/> is the text that triggered it, for the admin timeline.</summary>
public sealed record SplitSignal(int Page, SplitSignalKind Kind, string Evidence);

/// <summary>
/// Result of the heuristic split (docs/03 stage 3, step 1). <see cref="Ranges"/> are contiguous and cover every page.
/// When <see cref="IsConfident"/> is false the pipeline asks the LLM for split points (step 2) and uses the ranges
/// only as a fallback.
/// </summary>
public sealed record SplitAnalysis(
    IReadOnlyList<PageRange> Ranges,
    double Confidence,
    bool IsConfident,
    IReadOnlyList<SplitSignal> Signals);

/// <summary>
/// The non-LLM splitter. <see cref="IDocumentSplitter.SplitAsync"/> keeps the pipeline contract; <see cref="AnalyzeAsync"/>
/// adds the confidence the pipeline needs to decide whether to spend an LLM call (docs/03 stage 3, step 2).
/// </summary>
public interface IHeuristicSplitter : IDocumentSplitter
{
    Task<SplitAnalysis> AnalyzeAsync(Guid tenantId, TextLayer? textLayer, IReadOnlyList<ReadOnlyMemory<byte>> pagePngs, CancellationToken cancellationToken);
}
