namespace DocProc.Llm.Extraction;

/// <summary>
/// Knobs for the LLM stages, bound from the <c>Llm:Extraction</c> configuration section. Models and providers are
/// not here: those are resolved per tenant and stage by the gateway (docs/08).
/// </summary>
public sealed class LlmExtractionOptions
{
    /// <summary>Output budget of the first extraction attempt; a truncated answer is retried once with twice this value.</summary>
    public int MaxOutputTokens { get; set; } = LlmOptions.Default.MaxOutputTokens;

    /// <summary>Output budget for classification and split answers, which are a few hundred tokens at most.</summary>
    public int MaxAuxiliaryOutputTokens { get; set; } = 1_024;

    /// <summary>docs/06 "Model choice": start at low and raise only when the eval shows gains.</summary>
    public ReasoningEffort Effort { get; set; } = ReasoningEffort.Low;

    /// <summary>How much of the text layer the classifier sees (docs/03 stage 4: first page is enough).</summary>
    public int ClassificationTextChars { get; set; } = 2_000;

    /// <summary>How much of every page's text the splitter sees: the first lines carry the headers and footers it needs.</summary>
    public int SplitTextCharsPerPage { get; set; } = 300;

    /// <summary>Long edge in pixels of the page thumbnails the splitter attaches; a boundary is visible well below full render size.</summary>
    public int SplitThumbnailLongEdge { get; set; } = 768;

    /// <summary>How many page thumbnails the splitter attaches at most (the first ones); later pages are decided from text. Zero attaches none.</summary>
    public int SplitMaxThumbnailPages { get; set; } = 40;

    public LlmOptions ToLlmOptions() => new() { Effort = Effort, MaxOutputTokens = MaxOutputTokens };

    public LlmOptions ToAuxiliaryLlmOptions() => new() { Effort = ReasoningEffort.Low, MaxOutputTokens = MaxAuxiliaryOutputTokens };
}
