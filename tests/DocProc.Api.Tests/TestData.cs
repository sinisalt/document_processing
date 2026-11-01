using System.Text.Json;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Api.Tests;

/// <summary>Structural comparison for response records that carry lists (record equality compares those by reference).</summary>
internal static class JsonAssert
{
    public static void ShouldMatch<T>(this T actual, T expected) =>
        JsonSerializer.Serialize(actual).ShouldBe(JsonSerializer.Serialize(expected));
}

/// <summary>Minimal rows for the parts of the model the tenancy endpoints only reference (ingestions, documents).</summary>
internal static class TestData
{
    public static BlobReference Blob(string key) => new()
    {
        Container = "raw",
        Key = key,
        Sha256 = new string('0', 64),
        SizeBytes = 1,
        ContentType = "application/pdf",
    };

    public static Ingestion Ingestion(Guid tenantId, Guid channelId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ChannelId = channelId,
        ReceivedAt = now,
        RawBlob = Blob($"{tenantId}/raw"),
        UpdatedAt = now,
    };

    public static IngestionItem Item(Ingestion ingestion, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = ingestion.TenantId,
        IngestionId = ingestion.Id,
        FileName = "invoice.pdf",
        MimeType = "application/pdf",
        SizeBytes = 1,
        Sha256 = new string('0', 64),
        Blob = Blob($"{ingestion.TenantId}/item"),
        Kind = ItemKind.Pdf,
        CreatedAt = now,
    };

    public static Document Document(IngestionItem item, Guid? supplierId, string number, decimal totalGross, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = item.TenantId,
        IngestionItemId = item.Id,
        SupplierId = supplierId,
        Type = DocumentType.Invoice,
        Status = DocumentStatus.Approved,
        DocumentNumber = number,
        DocumentNumberNormalized = number.ToUpperInvariant(),
        IssueDate = DateOnly.FromDateTime(now.UtcDateTime),
        Currency = "EUR",
        TotalGross = totalGross,
        CreatedAt = now,
        UpdatedAt = now,
    };
}
