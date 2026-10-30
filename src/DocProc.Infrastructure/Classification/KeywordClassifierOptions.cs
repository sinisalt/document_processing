namespace DocProc.Infrastructure.Classification;

/// <summary>Tunables of <see cref="KeywordDocumentClassifier"/> (docs/03 stage 4 "Classify").</summary>
public sealed class KeywordClassifierOptions
{
    public const string SectionName = "DocumentProcessing:Classifier";

    /// <summary>Below this confidence the classifier answers <c>Unknown</c> so the pipeline can ask the LLM.</summary>
    public double ConfidenceThreshold { get; set; } = 0.6;

    /// <summary>Multiplier for hits in the header (top third) of the first page, where titles live.</summary>
    public double FirstPageHeaderFactor { get; set; } = 2.0;

    /// <summary>Multiplier for hits elsewhere on the first page.</summary>
    public double FirstPageFactor { get; set; } = 1.0;

    /// <summary>Multiplier for hits on later pages, where references to other documents accumulate.</summary>
    public double LaterPageFactor { get; set; } = 0.4;

    /// <summary>
    /// Extra multiplier (on top of <see cref="FirstPageFactor"/>) for a title below the header third of the first page
    /// whose type has no title in the header while another type has one: on an invoice headed "Invoice", a body line
    /// "Delivery note DN-5" refers to the delivery note rather than being one.
    /// </summary>
    public double BodyTitleFactorWhenHeaderTaken { get; set; } = 0.5;

    /// <summary>
    /// Score that counts as full evidence: a title in the first-page header (title weight × header factor). A winning
    /// type with less than this is scaled down, so a single body mention never reaches full confidence.
    /// </summary>
    public double StrongEvidenceScore { get; set; } = 8.0;
}
