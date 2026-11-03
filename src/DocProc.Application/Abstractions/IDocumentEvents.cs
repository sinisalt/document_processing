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

    // Review actions of docs/11 §12.1 and §12.7 (P2); timeline only, never published.

    /// <summary>A reviewer gave the document a type (<c>:set-type</c>).</summary>
    public const string TypeSet = "document.type_set";

    /// <summary>A reviewer assigned the supplier, or created it from the document.</summary>
    public const string SupplierAssigned = "document.supplier_assigned";

    /// <summary>A reviewer split the document's pages into several documents (<c>:split</c>).</summary>
    public const string Split = "document.split";

    /// <summary>A reviewer joined parts of one attachment back into one document (<c>:join</c>), on every part.</summary>
    public const string Joined = "document.joined";

    // Deliveries of docs/11 §13.6 (P3); timeline only, never published.

    /// <summary>A delivery reached its destination (a 2xx, the file written, a feed entry acknowledged).</summary>
    public const string DeliverySucceeded = "document.delivery_succeeded";

    /// <summary>A delivery attempt failed; the details say whether it is retried (<c>nextAttemptAt</c>) or dead-lettered.</summary>
    public const string DeliveryFailed = "document.delivery_failed";

    /// <summary>A delivery was not sent: superseded by a newer revision, <c>OnReapproval: Skip</c>, a binding condition that is false.</summary>
    public const string DeliverySkipped = "document.delivery_skipped";

    /// <summary>Someone asked for a delivery to be sent again (<c>POST /deliveries/{id}:redeliver</c>).</summary>
    public const string RedeliveryRequested = "document.redelivery_requested";
}
