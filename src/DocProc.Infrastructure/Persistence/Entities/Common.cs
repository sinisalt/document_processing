namespace DocProc.Infrastructure.Persistence.Entities;

/// <summary>Rows that belong to one tenant. The DbContext adds a query filter and SQL Server adds row-level security for every implementation.</summary>
public interface ITenantScoped
{
    Guid TenantId { get; }
}

/// <summary>
/// Rows the pipeline and the API both update. <see cref="Version"/> is an optimistic concurrency token: the context
/// increments it on every update, so two units of work that loaded the same row and both try to write it cannot
/// overwrite each other; the second <c>SaveChanges</c> fails with <c>DbUpdateConcurrencyException</c>
/// (docs/03 "Idempotency and retries").
/// </summary>
public interface IVersioned
{
    /// <summary>Starts at 0 and grows by one per committed update; never set by callers.</summary>
    int Version { get; set; }
}

/// <summary>Where a blob lives (mirrors <c>DocProc.Application.Abstractions.BlobRef</c>); mapped as a complex type, i.e. a group of columns.</summary>
public sealed class BlobReference
{
    public required string Container { get; set; }
    public required string Key { get; set; }
    public required string Sha256 { get; set; }
    public required long SizeBytes { get; set; }
    public required string ContentType { get; set; }
}
