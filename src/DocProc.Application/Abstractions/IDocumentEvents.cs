namespace DocProc.Application.Abstractions;

/// <summary>Event names written to the document timeline and published at stage 9 (docs/03, docs/05 "Events out").</summary>
public static class DocumentEventTypes
{
    public const string Received = "document.received";
    public const string StageCompleted = "document.stage_completed";
    public const string StageFailed = "document.stage_failed";
    public const string Flagged = "document.flagged";
    public const string ReviewRequested = "document.review_requested";
    public const string AutoApproved = "document.auto_approved";
    public const string Approved = "document.approved";
    public const string Rejected = "document.rejected";
    public const string Corrected = "document.corrected";
    public const string Reprocessed = "document.reprocessed";
}
