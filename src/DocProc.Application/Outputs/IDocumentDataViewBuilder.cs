using System.Text.Json.Nodes;

namespace DocProc.Application.Outputs;

/// <summary>
/// Which document's values view to build, for which event (<c>meta.event</c> and <c>meta.eventAt</c>; null outside
/// a delivery, a preview), and whether to add the field envelopes <c>$field(path)</c> reads.
/// </summary>
public sealed record DataViewRequest(Guid DocumentId)
{
    public string? Event { get; init; }

    public DateTimeOffset? EventAt { get; init; }

    public bool IncludeFields { get; init; }
}

/// <summary>
/// A built values view: the view, the field envelopes when asked for, the document's tenant, version and type, and
/// today in the tenant's time zone (what <c>$today()</c> and lookup validity read).
/// </summary>
public sealed record DocumentDataViewResult(JsonObject View, JsonObject? Fields, Guid TenantId, int DocumentVersion, string TypeKey, DateOnly Today);

/// <summary>
/// Builds the values view of one document (docs/11 §5.6): its canonical tree through its pinned (or published) type
/// version, the roles, <c>meta</c>, the matched supplier with its attributes, the receiving entity, the source facts
/// of its ingestion and the links. The view is what output mappings render (§13.1).
/// </summary>
public interface IDocumentDataViewBuilder
{
    /// <summary>The view, or null when the document is not visible in the current tenant scope.</summary>
    Task<DocumentDataViewResult?> BuildAsync(DataViewRequest request, CancellationToken cancellationToken);
}
