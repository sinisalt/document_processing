using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Configuration;
using DocProc.Application.Outputs;
using DocProc.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocProc.Infrastructure.Outputs;

/// <summary>Settings of the output paths (section <c>Outputs</c>).</summary>
public sealed class OutputOptions
{
    public const string SectionName = "Outputs";

    /// <summary>The public base URL of the API (<c>https://dms.example</c>) the values view's <c>links</c> start with; relative links without it.</summary>
    public string? PublicBaseUrl { get; set; }
}

/// <summary>
/// <see cref="IDocumentDataViewBuilder"/> over the database: the document (in the current tenant scope), its type
/// version through <see cref="IEffectiveConfigResolver"/> (pinned, else published; the same resolution the validate
/// stage uses, so a coded invoice reads through its adopted standard template), the matched supplier with
/// <c>Suppliers.AttributesJson</c>, the tenant as the receiving entity (until receiving entities, P4), the ingestion's
/// source facts and its channel, and today in the tenant's time zone.
/// <para>
/// Not cached: every build reads the document, because a supplier's attributes can change without the document's
/// version changing. A fan-out renders several bindings from one build (P3 part B).
/// </para>
/// </summary>
public sealed class DocumentDataViewBuilder(DocProcDbContext db, IEffectiveConfigResolver configs, TimeProvider clock, IOptions<OutputOptions> options) : IDocumentDataViewBuilder
{
    public async Task<DocumentDataViewResult?> BuildAsync(DataViewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var document = await db.Documents.AsNoTracking()
            .Where(d => d.Id == request.DocumentId)
            .Select(d => new
            {
                d.Id,
                d.TenantId,
                d.Version,
                d.Type,
                d.Status,
                d.SupplierId,
                d.DocumentTypeVersionId,
                d.CanonicalJson,
                d.ApprovedAt,
                d.ApprovedBy,
                d.IngestionItemId,
                Flags = d.Flags.Where(f => f.ResolvedAt == null).Select(f => f.Code).ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        var tenant = await db.Tenants.AsNoTracking().Where(t => t.Id == document.TenantId)
            .Select(t => new { t.LegalName, t.VatId, t.TimeZone })
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var today = TenantClock.Today(tenant.TimeZone, clock.GetUtcNow());

        DocumentDataSupplier? supplier = null;
        if (document.SupplierId is { } supplierId)
        {
            var row = await db.Suppliers.AsNoTracking().Where(s => s.Id == supplierId)
                .Select(s => new { s.Id, s.Name, s.VatId, s.TaxId, s.AttributesJson })
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (row is not null)
            {
                supplier = new DocumentDataSupplier(row.Id, row.Name, row.VatId) { TaxId = row.TaxId, Attributes = ParseObject(row.AttributesJson) };
            }
        }

        var source = await db.IngestionItems.AsNoTracking().Where(i => i.Id == document.IngestionItemId)
            .Select(i => new
            {
                i.FileName,
                i.Ingestion!.ReceivedAt,
                i.Ingestion.SourceSender,
                i.Ingestion.SubmittedBy,
                i.Ingestion.SourceSubject,
                i.Ingestion.SourceFileName,
                Channel = i.Ingestion.Channel!.Name,
                Connector = i.Ingestion.Channel.Type,
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        var config = await configs.ResolveAsync(new EffectiveConfigRequest(document.TenantId, document.Type, document.SupplierId, document.DocumentTypeVersionId), cancellationToken).ConfigureAwait(false);
        var compiled = config.Type?.Compiled;
        var context = new DocumentDataContext(new DocumentDataMeta(
            document.Id,
            document.Version,
            document.Type.Value,
            config.Type?.Version,
            document.Status.ToString(),
            document.ApprovedAt,
            document.ApprovedBy,
            document.Flags.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList())
        {
            TenantId = document.TenantId,
            Event = request.Event,
            EventAt = request.EventAt,
        })
        {
            Supplier = supplier,
            Entity = new DocumentDataEntity(null, null, tenant.LegalName, tenant.VatId),
            Source = source is null ? null : new DocumentDataSource(
                source.Channel,
                source.Connector.ToString(),
                Mailbox: null,
                source.SourceSender,
                source.SubmittedBy,
                FolderPath: null,
                source.SourceFileName ?? source.FileName,
                source.SourceSubject,
                source.ReceivedAt,
                TenantClock.Today(tenant.TimeZone, source.ReceivedAt)),
            Links = Links(document.Id),
        };

        var canonical = ParseObject(document.CanonicalJson);
        var view = compiled is null ? DocumentDataView.BuildWithoutDefinition(context) : DocumentDataView.Build(canonical, compiled, context);
        var fields = request.IncludeFields && compiled is not null ? DocumentDataView.FieldEnvelopes(canonical, compiled.Descriptors) : null;
        return new DocumentDataViewResult(view, fields, document.TenantId, document.Version, document.Type.Value, today);
    }

    private DocumentDataLinks Links(Guid documentId)
    {
        var root = options.Value.PublicBaseUrl?.TrimEnd('/') ?? string.Empty;
        var api = $"{root}/api/v1/documents/{documentId}";
        return new DocumentDataLinks($"{api}/original", api);
    }

    private static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Dates in a tenant's time zone (<c>Tenants.TimeZone</c>), UTC when the zone is unknown.</summary>
public static class TenantClock
{
    public static DateOnly Today(string? timeZone, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(timeZone))
        {
            try
            {
                return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime);
            }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Fall through to UTC.
            }
        }

        return DateOnly.FromDateTime(now.UtcDateTime);
    }
}
