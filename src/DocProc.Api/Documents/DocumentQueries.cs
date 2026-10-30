using System.Security.Claims;
using System.Text.Json;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Api.Documents;

/// <summary>A document with the joined values the summary needs; what the list and detail queries project to.</summary>
public sealed record DocumentRow(Document Document, string? TenantName, string? SupplierName, int BlockingFlagCount, int WarningFlagCount);

/// <summary>Projections shared by the document, ingestion and review-task endpoints.</summary>
public static class DocumentQueries
{
    /// <summary>
    /// Tenant scope comes from the context's query filters; nothing here filters by tenant. The tenant name is a LEFT
    /// JOIN on <c>Tenants</c> in the same statement (no per-row lookup); the Tenants filter lets a tenant-scoped caller
    /// see its own tenant row, so the name is there in both scopes.
    /// </summary>
    public static IQueryable<DocumentRow> Rows(DocProcDbContext db, IQueryable<Document> documents) =>
        from d in documents
        join t in db.Tenants on d.TenantId equals t.Id into tenants
        from t in tenants.DefaultIfEmpty()
        select new DocumentRow(
            d,
            t != null ? t.Name : null,
            d.Supplier != null ? d.Supplier.Name : null,
            d.Flags.Count(f => f.ResolvedAt == null && f.Severity == FlagSeverity.Blocking),
            d.Flags.Count(f => f.ResolvedAt == null && f.Severity == FlagSeverity.Warning));

    public static DocumentSummary ToSummary(DocumentRow row)
    {
        var d = row.Document;
        return new DocumentSummary(
            d.Id,
            d.TenantId,
            row.TenantName,
            d.Status,
            d.Type,
            d.TypeConfidence,
            d.SupplierId,
            row.SupplierName,
            d.DocumentNumber,
            d.IssueDate,
            d.Currency,
            d.TotalGross,
            d.ExtractionMethod,
            d.PageCount,
            row.BlockingFlagCount,
            row.WarningFlagCount,
            d.CreatedAt,
            d.UpdatedAt,
            d.ApprovedAt);
    }

    /// <summary>Loads one document with everything the detail needs; tracked when the caller is going to modify it.</summary>
    public static Task<DocumentRow?> FindForDetailAsync(DocProcDbContext db, Guid id, bool track, CancellationToken ct)
    {
        var documents = track ? db.Documents : db.Documents.AsNoTracking();
        return Rows(db, documents
                .Where(d => d.Id == id)
                .Include(d => d.Flags)
                .Include(d => d.IngestionItem!)
                .ThenInclude(i => i.Ingestion))
            .SingleOrDefaultAsync(ct);
    }

    public static DocumentDetail ToDetail(DocumentRow row)
    {
        var d = row.Document;
        var item = d.IngestionItem;
        var ingestion = item is null
            ? null
            : new DocumentIngestionRef(item.IngestionId, item.Id, item.FileName, item.Ingestion?.SourceFrom, item.Ingestion?.ReceivedAt ?? item.CreatedAt);

        return new DocumentDetail(
            d.Id,
            d.Status,
            d.Type,
            d.TypeConfidence,
            d.SupplierId,
            row.SupplierName,
            d.DocumentNumber,
            d.IssueDate,
            d.Currency,
            d.TotalGross,
            d.ExtractionMethod,
            d.PageCount,
            row.BlockingFlagCount,
            row.WarningFlagCount,
            d.CreatedAt,
            d.UpdatedAt,
            d.ApprovedAt,
            d.ApprovedBy,
            d.FailureReason,
            ParseJson(d.CanonicalJson),
            d.CanonicalSchemaVersion,
            d.Flags
                .OrderBy(f => f.CreatedAt)
                .ThenBy(f => f.Id)
                .Select(f => new DocumentFlagDto(f.Id, f.Code, f.Severity, f.Field, f.Message, f.CreatedBy, f.CreatedAt, f.ResolvedAt, f.ResolvedBy))
                .ToList(),
            new DocumentCostsDto(d.Costs.LlmInputTokens, d.Costs.LlmOutputTokens, d.Costs.LlmCacheReadTokens, d.Costs.OcrPages, d.Costs.EstimatedUsd, d.Costs.LlmProvider, d.Costs.LlmModel, d.Costs.KeyOwner),
            ingestion,
            Enumerable.Range(1, d.PageImageKeys.Count).Select(n => PageImageUrl(d.Id, n)).ToList());
    }

    public static string PageImageUrl(Guid documentId, int page) => $"/api/v1/documents/{documentId}/pages/{page}.png";

    /// <summary>Stored JSON as an element the response serialiser writes verbatim; null for no or malformed JSON.</summary>
    public static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Appends a timeline row (docs/03 "Document state machine"); the caller saves.</summary>
    public static DocumentEvent AddEvent(DocProcDbContext db, Document document, string eventType, DocumentStatusChange? change, string actor, object? details, DateTimeOffset at)
    {
        var row = new DocumentEvent
        {
            TenantId = document.TenantId,
            DocumentId = document.Id,
            EventType = eventType,
            FromStatus = change?.From,
            ToStatus = change?.To,
            Actor = actor,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details, DetailsOptions),
            At = at,
        };
        db.DocumentEvents.Add(row);
        return row;
    }

    private static readonly JsonSerializerOptions DetailsOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The name recorded as actor of a human action: the token's name claim, else its subject, else the API key's name.</summary>
    public static string ActorName(ClaimsPrincipal user)
    {
        var name = user.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Length <= 200 ? name : name[..200];
        }

        return user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
    }
}

public readonly record struct DocumentStatusChange(DocumentStatus From, DocumentStatus To);
