namespace DocProc.Pipeline;

/// <summary>The <c>Pipeline</c> configuration section: how the runner consumes the queue and how it retries (docs/03 "Idempotency and retries").</summary>
public sealed class PipelineOptions
{
    public const string SectionName = "Pipeline";
    public const string RunInProcessKey = SectionName + ":RunInProcess";

    /// <summary>How many messages the runner processes at the same time. Each one gets its own DI scope and DbContext.</summary>
    public int MaxParallelism { get; set; } = 2;

    /// <summary>Delay before the second attempt; each further attempt doubles it (5 s, 10 s, 20 s, 40 s).</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Attempts per stage message including the first; the last failure moves the document to Failed.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Upper bound for one backoff delay however many attempts were made.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Page raster resolution of stage 2 (docs/03: 200 dpi for classification, LLM input and the review UI).</summary>
    public int RenderDpi { get; set; } = 200;

    /// <summary>Below this keyword-classifier confidence stage 4 asks the LLM classifier (docs/03 stage 4).</summary>
    public double LlmClassificationThreshold { get; set; } = 0.6;

    /// <summary>Archives nested deeper than this are not expanded (docs/03 stage 2: "refuse nested archives beyond a depth of 2").</summary>
    public int MaxArchiveDepth { get; set; } = 2;

    /// <summary>Largest single archive entry that is unpacked; bigger entries are skipped with a recorded reason.</summary>
    public long MaxArchiveEntryBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>Most entries unpacked from one archive; the rest are skipped with a recorded reason.</summary>
    public int MaxArchiveEntries { get; set; } = 200;

    /// <summary>
    /// Budget of one ingestion across all its archives, nested ones included: once this many inflated bytes were
    /// unpacked, the current archive stops and every archive still waiting is ignored with a recorded reason. Without it
    /// a few kilobytes of nested zips expand to terabytes of stored blobs (docs/03 stage 2).
    /// </summary>
    public long MaxUnpackedBytesPerIngestion { get; set; } = 256 * 1024 * 1024;

    /// <summary>Budget of one ingestion across all its archives: the most files unpacked in total (each can become a document).</summary>
    public int MaxUnpackedItemsPerIngestion { get; set; } = 500;

    /// <summary>
    /// Largest inflated-to-compressed ratio of an archive entry that inflates past <see cref="CompressionRatioFloorBytes"/>;
    /// a more compressed entry is skipped. Documents compress a few times, a zip bomb a thousand times. 0 turns the check off.
    /// </summary>
    public int MaxArchiveCompressionRatio { get; set; } = 100;

    /// <summary>Entries up to this size are unpacked whatever their compression ratio (a small text file can compress well).</summary>
    public const long CompressionRatioFloorBytes = 1024 * 1024;

    /// <summary>Images smaller than this are signatures, logos and tracking pixels, not documents (docs/03 stage 2).</summary>
    public long MinimumImageBytes { get; set; } = 20 * 1024;

    /// <summary>How long the runner waits for in-flight messages when the host stops.</summary>
    public TimeSpan ShutdownGracePeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Exponential backoff for the attempt that just failed: base × 2^(attempt − 1), capped at <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan BackoffAfter(int failedAttempt)
    {
        var factor = Math.Pow(2, Math.Max(0, failedAttempt - 1));
        var delay = RetryBaseDelay.TotalMilliseconds * factor;
        return TimeSpan.FromMilliseconds(Math.Min(delay, MaxRetryDelay.TotalMilliseconds));
    }
}
