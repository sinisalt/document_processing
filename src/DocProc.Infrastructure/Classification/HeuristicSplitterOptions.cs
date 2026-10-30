namespace DocProc.Infrastructure.Classification;

/// <summary>Tunables of <see cref="HeuristicDocumentSplitter"/> (docs/03 stage 3 "Split", step 1).</summary>
public sealed class HeuristicSplitterOptions
{
    public const string SectionName = "DocumentProcessing:Splitter";

    /// <summary>A page with at most this many words is a blank separator page.</summary>
    public int BlankPageMaxWords { get; set; } = 2;

    /// <summary>A page whose net start score reaches this value starts a new document.</summary>
    public double StartThreshold { get; set; } = 2.0;

    /// <summary>Net score that counts as a certain decision either way; smaller scores scale the page's certainty down.</summary>
    public double CertainScore { get; set; } = 3.0;

    /// <summary>Certainty assigned to a page without any signal, which the splitter treats as a continuation page.</summary>
    public double NoSignalCertainty { get; set; } = 0.6;

    /// <summary>Minimum overall confidence for <see cref="Application.Abstractions.SplitAnalysis.IsConfident"/>.</summary>
    public double ConfidenceThreshold { get; set; } = 0.7;

    /// <summary>Above this many pages the pipeline asks the LLM regardless (docs/03: "more than ~6 pages").</summary>
    public int MaxConfidentPageCount { get; set; } = 6;

    /// <summary>Fraction of the page height that forms the header band compared for the "supplier header repeated" signal.</summary>
    public double HeaderBandFraction { get; set; } = 0.12;

    /// <summary>Jaccard similarity of header-band words above which the supplier header counts as repeated.</summary>
    public double HeaderSimilarityThreshold { get; set; } = 0.5;
}
