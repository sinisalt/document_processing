namespace DocProc.Infrastructure.Persistence.Entities;

/// <summary>
/// A stored response for a resource-creating request that carried an <c>Idempotency-Key</c> header (docs/05
/// "idempotency keys on writes that create resources"). The same key from the same tenant replays the stored
/// response for 24 hours; the same key with a different request body is refused, because silently answering with
/// another request's result would hide a client bug.
/// </summary>
public sealed class IdempotencyKey : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>The client-chosen key, verbatim; unique per tenant.</summary>
    public required string Key { get; set; }

    /// <summary>SHA-256 over the parts of the request that define its outcome, so a reused key with a different body is detected.</summary>
    public required string RequestHash { get; set; }

    public int StatusCode { get; set; }

    /// <summary>The response body that was sent the first time, replayed verbatim.</summary>
    public required string ResponseJson { get; set; }

    /// <summary>The <c>Location</c> of the created resource, when the response had one.</summary>
    public string? Location { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
