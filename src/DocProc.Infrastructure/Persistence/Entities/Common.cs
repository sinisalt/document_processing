namespace DocProc.Infrastructure.Persistence.Entities;

/// <summary>Rows that belong to one tenant. The DbContext adds a query filter and SQL Server adds row-level security for every implementation.</summary>
public interface ITenantScoped
{
    Guid TenantId { get; }
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
