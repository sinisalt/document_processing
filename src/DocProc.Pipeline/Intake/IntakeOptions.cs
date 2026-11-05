namespace DocProc.Pipeline.Intake;

/// <summary>
/// The <c>Intake</c> configuration section (docs/11 §7.1): the channel pollers that read mailboxes (IMAP) and watched
/// folders for the tenants with <c>sourceRouting</c> on, and the drop zone the folders live in.
/// </summary>
public sealed class IntakeOptions
{
    public const string SectionName = "Intake";

    /// <summary>
    /// Whether this host polls the channels. They run next to the pipeline runner (the worker, and the API when it runs
    /// the pipeline in process); two hosts polling the same channel ingest each message and file once (the unique
    /// message id per channel), so turning one off only saves the second connection.
    /// </summary>
    public bool PollersEnabled { get; set; } = true;

    /// <summary>How often every polling channel is read. The first poll runs one interval after start.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The most messages one mailbox poll ingests; the rest wait for the next poll.</summary>
    public int MaxMessagesPerPoll { get; set; } = 25;

    /// <summary>
    /// The root of the drop zone (a relative path is under the host's content root). Each tenant's folders are below
    /// <c>{root}/{tenant slug}/</c>; a channel's <c>path</c> is relative to that.
    /// </summary>
    public string FolderRoot { get; set; } = "data/dropzone";

    /// <summary>How long a file's size and time must stay unchanged before the watcher takes it (a channel's <c>stableSeconds</c> overrides it).</summary>
    public TimeSpan FolderStableFor { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The most files one folder scan takes.</summary>
    public int MaxFilesPerScan { get; set; } = 50;

    /// <summary>The largest file the watcher takes; a larger one is moved to <c>failed/</c>.</summary>
    public long MaxFileBytes { get; set; } = 50L * 1024 * 1024;
}
