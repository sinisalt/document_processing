using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocProc.Api.Auth;
using DocProc.Application.Configuration;
using DocProc.Domain.Common;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

public sealed record SupplierResponse(
    Guid Id,
    string Name,
    string NormalizedName,
    string? VatId,
    string? TaxId,
    IReadOnlyList<string> Ibans,
    IReadOnlyList<string> EmailDomains,
    IReadOnlyList<string> Aliases,
    string? DefaultCurrency,
    bool AlwaysReview,
    Guid? GlobalSupplierId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The tenant's attributes of the supplier (docs/11 §6.4, <c>{"erpVendorId": "V-10042"}</c>), what output mappings read as <c>supplier.attributes</c>.</summary>
    public IReadOnlyDictionary<string, JsonElement> Attributes { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>The supplier group whose attributes and settings the supplier inherits (docs/11 §6.4); null for none.</summary>
    public Guid? GroupId { get; init; }

    /// <summary>The supplier's own settings (docs/11 §6.4): an object of the members set, every other one inherited.</summary>
    public JsonElement Settings { get; init; }

    /// <summary>The supplier's attributes per receiving entity (docs/11 §6.4); only on <c>GET /suppliers/{id}</c> and the writes of one supplier, empty in the list.</summary>
    public IReadOnlyList<SupplierEntityAttributesResponse> EntityAttributes { get; init; } = [];

    internal static SupplierResponse From(Supplier s) =>
        new(s.Id, s.Name, s.NormalizedName, s.VatId, s.TaxId, s.Ibans, s.EmailDomains, s.Aliases, s.DefaultCurrency, s.AlwaysReview, s.GlobalSupplierId, s.CreatedAt, s.UpdatedAt)
        {
            Attributes = SupplierEndpoints.AttributesOf(s.AttributesJson),
            GroupId = s.GroupId,
            Settings = SupplierConfigSupport.SettingsOf(s.SettingsJson),
        };
}

/// <summary>A supplier's attributes in one receiving entity (<c>SupplierEntityAttributes</c>, docs/11 §6.4).</summary>
public sealed record SupplierEntityAttributesResponse(Guid ReceivingEntityId, string? ReceivingEntityCode, IReadOnlyDictionary<string, JsonElement> Attributes, DateTimeOffset UpdatedAt);

/// <summary>The body of <c>PUT /suppliers/{id}/entity-attributes/{entityId}</c>: the attributes, replacing the stored ones.</summary>
public sealed record SupplierEntityAttributesRequest(IReadOnlyDictionary<string, JsonElement> Attributes);

public sealed record CreateSupplierRequest(
    string Name,
    string? VatId,
    string? TaxId,
    IReadOnlyList<string>? Ibans,
    IReadOnlyList<string>? EmailDomains,
    IReadOnlyList<string>? Aliases,
    string? DefaultCurrency,
    bool? AlwaysReview)
{
    /// <summary>The supplier's attributes: at most 50 keys, each a string (at most 400 characters), a number, a boolean or null; checked against the tenant's attribute schema when it has one.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Attributes { get; init; }

    /// <summary>The supplier group (docs/11 §6.4; needs <c>sourceRouting</c>).</summary>
    public Guid? GroupId { get; init; }

    /// <summary>The supplier's settings (docs/11 §6.4; needs <c>sourceRouting</c>).</summary>
    public JsonElement? Settings { get; init; }
}

/// <summary>Every field optional; absent fields are left as they are. A string set to empty clears it; a list replaces the stored list.</summary>
public sealed record PatchSupplierRequest(
    string? Name,
    string? VatId,
    string? TaxId,
    IReadOnlyList<string>? Ibans,
    IReadOnlyList<string>? EmailDomains,
    IReadOnlyList<string>? Aliases,
    string? DefaultCurrency,
    bool? AlwaysReview)
{
    /// <summary>The supplier's attributes, replacing the stored ones (an empty object clears them).</summary>
    public IReadOnlyDictionary<string, JsonElement>? Attributes { get; init; }

    /// <summary>The supplier group; the empty id (<c>00000000-0000-0000-0000-000000000000</c>) takes the supplier out of its group (needs <c>sourceRouting</c>).</summary>
    public Guid? GroupId { get; init; }

    /// <summary>The supplier's settings, replacing the stored ones (an empty object clears them; needs <c>sourceRouting</c>).</summary>
    public JsonElement? Settings { get; init; }
}

/// <summary>One recent document of a supplier, for the supplier activity view (docs/05 "Suppliers: ... activity").</summary>
public sealed record SupplierActivityItem(Guid Id, string Status, string Type, string? DocumentNumber, DateOnly? IssueDate, decimal? TotalGross, string? Currency, DateTimeOffset CreatedAt);

/// <summary>
/// Suppliers of the current tenant (docs/02 §1 Supplier). <c>NormalizedName</c> is always derived with
/// <see cref="Identifiers.NormalizeName"/>, the same function stage 5 matches with, so what the portal creates and
/// what the pipeline looks up agree.
/// </summary>
public static partial class SupplierEndpoints
{
    private const int ActivitySize = 20;

    /// <summary>Column width of <c>Name</c> and <c>NormalizedName</c>; folding can lengthen a name ("ß" becomes "SS").</summary>
    public const int MaxNameLength = 300;

    /// <summary>
    /// The most IBANs, e-mail domains and aliases one supplier holds. Every Match stage run of the tenant loads every
    /// supplier and walks these lists in the shared worker, and since P2 a reviewer creates suppliers too
    /// (<c>:create-supplier</c>), so they are bounded like the name.
    /// </summary>
    public const int MaxIbans = 20;

    /// <inheritdoc cref="MaxIbans"/>
    public const int MaxEmailDomains = 20;

    /// <inheritdoc cref="MaxIbans"/>
    public const int MaxAliases = 50;

    /// <summary>The longest IBAN as typed, with the spaces that group it (34 characters at most once compacted).</summary>
    public const int MaxIbanLength = 64;

    /// <summary>The longest domain name (RFC 1035).</summary>
    public const int MaxEmailDomainLength = 253;

    /// <summary>The body of a supplier write (<c>POST</c> and <c>PATCH /suppliers</c>, <c>:create-supplier</c>); the lists at their caps take a few KB.</summary>
    public const int MaxRequestBytes = 64 * 1024;

    /// <summary>An IBAN once compacted: a country code, two check digits and 11 to 30 letters or digits (ISO 13616); the check digits are not verified.</summary>
    [GeneratedRegex("^[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex IbanPattern();

    /// <summary>A domain name as a sender's address ends with it: labels of letters, digits and inner dashes, joined by dots, two at least.</summary>
    [GeneratedRegex(@"^[\p{L}\p{N}](?:[\p{L}\p{N}-]{0,61}[\p{L}\p{N}])?(?:\.[\p{L}\p{N}](?:[\p{L}\p{N}-]{0,61}[\p{L}\p{N}])?)+$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();

    public static RouteGroupBuilder MapSuppliers(this RouteGroupBuilder group)
    {
        var suppliers = group.MapGroup("/suppliers").WithTags("Suppliers");

        suppliers.MapGet("/", async Task<Results<Ok<Page<SupplierResponse>>, ValidationProblem, ProblemHttpResult>> (string? q, string? cursor, int? limit, [FromServices] DocProcDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Suppliers") is { } problem)
            {
                return problem;
            }

            var after = DecodeCursor(cursor, 2);
            if (after is { Length: 0 } || (after is not null && !Guid.TryParse(after[1], out _)))
            {
                return InvalidCursor();
            }

            var take = ClampLimit(limit);
            var query = db.Suppliers.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var normalized = Identifiers.NormalizeName(q);
                var pattern = LikeContains(normalized);
                var compact = Identifiers.NormalizeCompact(q);
                query = query.Where(s => EF.Functions.Like(s.NormalizedName, pattern, "\\") || s.VatId == compact);
            }

            if (after is not null)
            {
                var afterName = after[0];
                var afterId = Guid.Parse(after[1]);
                query = query.Where(s => s.NormalizedName.CompareTo(afterName) > 0 || (s.NormalizedName == afterName && s.Id.CompareTo(afterId) > 0));
            }

            var rows = await query.OrderBy(s => s.NormalizedName).ThenBy(s => s.Id).Take(take + 1).ToListAsync(ct);
            var items = rows.Take(take).Select(SupplierResponse.From).ToList();
            var next = rows.Count > take ? EncodeCursor(items[^1].NormalizedName, items[^1].Id.ToString()) : null;
            return TypedResults.Ok(new Page<SupplierResponse>(items, next));
        })
        .RequireAuthorization(Policies.Read)
        .WithName("ListSuppliers")
        .WithSummary("Suppliers of the current tenant, by name; q matches the folded name or the exact VAT id.");

        suppliers.MapPost("/", async Task<Results<Created<SupplierResponse>, ValidationProblem, ProblemHttpResult>> (CreateSupplierRequest request, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, [FromServices] DocProc.Application.Outputs.IOutputRenderer renderer, ITenantContext tenant, TimeProvider clock, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Suppliers") is { } problem)
            {
                return problem;
            }

            var creation = await AddAsync(db, features, tenant.TenantId!.Value, request, clock.GetUtcNow(), ct, renderer);
            if (creation.Invalid is { } invalid)
            {
                return invalid;
            }

            if (creation.Problem is { } refused)
            {
                return refused;
            }

            var supplier = creation.Supplier!;
            await db.SaveChangesAsync(ct);
            return TypedResults.Created($"/api/v1/suppliers/{supplier.Id}", SupplierResponse.From(supplier));
        })
        .RequireAuthorization(Policies.TenantAdmin)
        .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes))
        .WithName("CreateSupplier")
        .WithSummary("Creates a supplier; the folded name used for matching is derived server-side.");

        suppliers.MapGet("/{id:guid}", async Task<Results<Ok<SupplierResponse>, NotFound, ProblemHttpResult>> (Guid id, [FromServices] DocProcDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Suppliers") is { } problem)
            {
                return problem;
            }

            var supplier = await db.Suppliers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct);
            return supplier is null ? TypedResults.NotFound() : TypedResults.Ok(await WithEntityAttributesAsync(db, supplier, ct));
        })
        .RequireAuthorization(Policies.Read)
        .WithName("GetSupplier")
        .WithSummary("One supplier of the current tenant.");

        suppliers.MapPatch("/{id:guid}", async Task<Results<Ok<SupplierResponse>, NotFound, ValidationProblem, ProblemHttpResult>> (Guid id, PatchSupplierRequest request, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, [FromServices] DocProc.Application.Outputs.IOutputRenderer renderer, ITenantContext tenant, TimeProvider clock, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Suppliers") is { } problem)
            {
                return problem;
            }

            var supplier = await db.Suppliers.SingleOrDefaultAsync(s => s.Id == id, ct);

            // A list sent back as it is stored is not a change and is not checked: a supplier stored before the caps
            // (P1) keeps its lists through an edit of its name or review setting, which the portal sends with every
            // list, until a write changes them (docs/05).
            var ibans = Changed(request.Ibans, supplier?.Ibans, CleanIbans);
            var emailDomains = Changed(request.EmailDomains, supplier?.EmailDomains, CleanDomains);
            var aliases = Changed(request.Aliases, supplier?.Aliases, CleanList);
            var errors = new Errors().Optional("name", request.Name, MaxNameLength).Optional("vatId", request.VatId, 32).Optional("taxId", request.TaxId, 32);
            ValidateLists(errors, ibans, emailDomains, aliases);
            ValidateAttributes(errors, request.Attributes);
            if (errors.Count == 0)
            {
                await SupplierConfigSupport.ValidateAgainstSchemaAsync(db, errors, request.Attributes, checkRequired: true, ct);
            }

            if ((request.GroupId is not null || request.Settings is not null) && !await features.IsEnabledAsync(tenant.TenantId!.Value, TenantFeature.SourceRouting, ct))
            {
                return SupplierConfigSupport.FeatureOff();
            }

            var settings = request.Settings is { } settingsElement ? SupplierConfigSupport.ReadSettings(errors, settingsElement) : null;
            await SupplierConfigSupport.CheckPipelineAsync(db, features, renderer, tenant.TenantId!.Value, settings, supplier?.SettingsJson, errors, ct);
            if (request.GroupId is { } groupId && groupId != Guid.Empty && !await db.SupplierGroups.AnyAsync(g => g.Id == groupId, ct))
            {
                errors.Add("groupId", $"No supplier group {groupId} in this tenant.");
            }

            string? normalizedName = null;
            if (request.Name is not null)
            {
                normalizedName = Identifiers.NormalizeName(request.Name);
                ValidateNormalizedName(errors, normalizedName);
            }

            if (!string.IsNullOrEmpty(request.DefaultCurrency) && !IsCurrency(request.DefaultCurrency))
            {
                errors.Add("defaultCurrency", "Currency must be a three-letter upper-case ISO 4217 code.", ValidationCodes.CurrencyCode);
            }

            if (errors.ToProblem() is { } invalid)
            {
                return invalid;
            }

            if (supplier is null)
            {
                return TypedResults.NotFound();
            }

            if (normalizedName is not null)
            {
                if (await db.Suppliers.AnyAsync(s => s.Id != id && s.NormalizedName == normalizedName, ct))
                {
                    return Conflict("Supplier exists", $"Another supplier whose name folds to '{normalizedName}' already exists in this tenant.");
                }

                supplier.Name = request.Name!.Trim();
                supplier.NormalizedName = normalizedName;
            }

            if (request.VatId is not null)
            {
                supplier.VatId = Identifiers.NormalizeCompact(request.VatId);
            }

            if (request.TaxId is not null)
            {
                supplier.TaxId = TrimToNull(request.TaxId);
            }

            if (ibans is not null)
            {
                supplier.Ibans = CleanIbans(ibans);
            }

            if (emailDomains is not null)
            {
                supplier.EmailDomains = CleanDomains(emailDomains);
            }

            if (aliases is not null)
            {
                supplier.Aliases = CleanList(aliases);
            }

            if (request.DefaultCurrency is not null)
            {
                supplier.DefaultCurrency = TrimToNull(request.DefaultCurrency);
            }

            if (request.AlwaysReview is { } alwaysReview)
            {
                supplier.AlwaysReview = alwaysReview;
            }

            if (request.Attributes is not null)
            {
                supplier.AttributesJson = AttributesJson(request.Attributes);
            }

            if (request.GroupId is { } group)
            {
                supplier.GroupId = group == Guid.Empty ? null : group;
            }

            if (settings is not null)
            {
                supplier.SettingsJson = settings.ToJson();
            }

            supplier.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(await WithEntityAttributesAsync(db, supplier, ct));
        })
        .RequireAuthorization(Policies.TenantAdmin)
        .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes))
        .WithName("UpdateSupplier")
        .WithSummary("Changes a supplier's identifiers, aliases, domains or review setting.");

        suppliers.MapDelete("/{id:guid}", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (Guid id, [FromServices] DocProcDbContext db, ITenantContext tenant, TimeProvider clock, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Suppliers") is { } problem)
            {
                return problem;
            }

            var supplier = await db.Suppliers.SingleOrDefaultAsync(s => s.Id == id, ct);
            if (supplier is null)
            {
                return TypedResults.NotFound();
            }

            if (await db.Documents.AnyAsync(d => d.SupplierId == id, ct))
            {
                return Conflict("Supplier in use", "Documents reference this supplier; it cannot be deleted.");
            }

            // The database refuses it as well (NO ACTION: SQL Server allows only one cascade path per table).
            if (await db.ExtractorProfiles.IgnoreQueryFilters().AnyAsync(p => p.SupplierId == id, ct))
            {
                return Conflict("Supplier in use", "Extractor profiles reference this supplier; it cannot be deleted.");
            }

            // The database refuses it as well (NO ACTION). Clearing the scope instead would widen the binding to every
            // document of the tenant.
            if (await db.OutputBindings.AnyAsync(b => b.SupplierId == id, ct))
            {
                return Conflict("Supplier in use", "Output bindings are scoped to this supplier; change or delete them first.");
            }

            if (await db.IngestionChannels.AnyAsync(c => c.DefaultSupplierId == id, ct))
            {
                return Conflict("Supplier in use", "A channel names this supplier as its default; change the channel first.");
            }

            var current = await CurrentTenantAsync(db, ct);
            if (current is not null && current.Settings.AlwaysReviewSupplierIds.Remove(id))
            {
                current.UpdatedAt = clock.GetUtcNow();
            }

            db.Suppliers.Remove(supplier);
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        })
        .RequireAuthorization(Policies.TenantAdmin)
        .WithName("DeleteSupplier")
        .WithSummary("Deletes a supplier no document, extractor profile, output binding or channel default references, with its source rules and entity attributes; otherwise 409.");

        suppliers.MapGet("/{id:guid}/activity", async Task<Results<Ok<IReadOnlyList<SupplierActivityItem>>, NotFound, ProblemHttpResult>> (Guid id, [FromServices] DocProcDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Suppliers") is { } problem)
            {
                return problem;
            }

            if (!await db.Suppliers.AnyAsync(s => s.Id == id, ct))
            {
                return TypedResults.NotFound();
            }

            // Newest first, sorted and cut in SQL on SQL Server. The development SQLite provider refuses DateTimeOffset
            // in ORDER BY, so there the supplier's documents are sorted in memory (a dev database is small).
            var documents = db.Documents.AsNoTracking()
                .Where(d => d.SupplierId == id)
                .Select(d => new { d.Id, d.Status, d.Type, d.DocumentNumber, d.IssueDate, d.TotalGross, d.Currency, d.CreatedAt });
            var rows = UsesSqlite(db)
                ? (await documents.ToListAsync(ct)).OrderByDescending(d => d.CreatedAt).Take(ActivitySize).ToList()
                : await documents.OrderByDescending(d => d.CreatedAt).Take(ActivitySize).ToListAsync(ct);
            var items = rows.Select(d => new SupplierActivityItem(d.Id, d.Status.ToString(), d.Type.ToString(), d.DocumentNumber, d.IssueDate, d.TotalGross, d.Currency, d.CreatedAt)).ToList();
            return TypedResults.Ok<IReadOnlyList<SupplierActivityItem>>(items);
        })
        .RequireAuthorization(Policies.Read)
        .WithName("GetSupplierActivity")
        .WithSummary("The supplier's twenty most recent documents.");

        return group;
    }

    /// <summary>What <see cref="AddAsync"/> did: the supplier it added (not saved yet), or why it refused.</summary>
    internal sealed record SupplierCreation(Supplier? Supplier, ValidationProblem? Invalid, ProblemHttpResult? Problem);

    /// <summary>
    /// The master-data checks of <c>POST /suppliers</c> (name, identifiers, currency, a name that folds to one no other
    /// supplier of the tenant has) and the supplier they allow, added to the context unsaved, so creating a supplier
    /// from review (<c>POST /documents/{id}:create-supplier</c>) checks exactly what the supplier list does and saves the
    /// supplier with its assignment in one go. The context's scope is the tenant's (the duplicate check reads it).
    /// </summary>
    internal static async Task<SupplierCreation> AddAsync(DocProcDbContext db, ITenantFeatures features, Guid tenantId, CreateSupplierRequest request, DateTimeOffset now, CancellationToken ct, DocProc.Application.Outputs.IOutputRenderer? renderer = null)
    {
        if ((request.GroupId is not null || request.Settings is not null) && !await features.IsEnabledAsync(tenantId, TenantFeature.SourceRouting, ct))
        {
            return new SupplierCreation(null, null, SupplierConfigSupport.FeatureOff());
        }

        var errors = new Errors().Require("name", request.Name, MaxNameLength).Optional("vatId", request.VatId, 32).Optional("taxId", request.TaxId, 32);
        ValidateLists(errors, request.Ibans, request.EmailDomains, request.Aliases);
        ValidateAttributes(errors, request.Attributes);
        if (errors.Count == 0)
        {
            await SupplierConfigSupport.ValidateAgainstSchemaAsync(db, errors, request.Attributes, checkRequired: true, ct);
        }

        var settings = request.Settings is { } settingsElement ? SupplierConfigSupport.ReadSettings(errors, settingsElement) : null;
        await SupplierConfigSupport.CheckPipelineAsync(db, features, renderer, tenantId, settings, null, errors, ct);
        if (request.GroupId is { } groupId && !await db.SupplierGroups.AnyAsync(g => g.Id == groupId, ct))
        {
            errors.Add("groupId", $"No supplier group {groupId} in this tenant.");
        }
        if (request.DefaultCurrency is not null && !IsCurrency(request.DefaultCurrency))
        {
            errors.Add("defaultCurrency", "Currency must be a three-letter upper-case ISO 4217 code.", ValidationCodes.CurrencyCode);
        }

        var normalizedName = string.IsNullOrWhiteSpace(request.Name) ? string.Empty : Identifiers.NormalizeName(request.Name);
        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            ValidateNormalizedName(errors, normalizedName);
        }

        if (errors.ToProblem() is { } invalid)
        {
            return new SupplierCreation(null, invalid, null);
        }

        if (await CurrentTenantAsync(db, ct) is null)
        {
            return new SupplierCreation(null, null, UnknownTenant());
        }

        if (await db.Suppliers.AnyAsync(s => s.TenantId == tenantId && s.NormalizedName == normalizedName, ct))
        {
            return new SupplierCreation(null, null, Conflict("Supplier exists", $"A supplier whose name folds to '{normalizedName}' already exists in this tenant; add an alias to it instead."));
        }

        var supplier = new Supplier
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = request.Name.Trim(),
            NormalizedName = normalizedName,
            VatId = Identifiers.NormalizeCompact(request.VatId),
            TaxId = TrimToNull(request.TaxId),
            Ibans = CleanIbans(request.Ibans),
            EmailDomains = CleanDomains(request.EmailDomains),
            Aliases = CleanList(request.Aliases),
            DefaultCurrency = request.DefaultCurrency,
            AlwaysReview = request.AlwaysReview ?? false,
            AttributesJson = request.Attributes is null ? null : AttributesJson(request.Attributes),
            GroupId = request.GroupId,
            SettingsJson = settings?.ToJson(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Suppliers.Add(supplier);
        return new SupplierCreation(supplier, null, null);
    }

    /// <summary>The most attributes one supplier holds, and the longest key and text value.</summary>
    public const int MaxAttributes = 50;

    /// <inheritdoc cref="MaxAttributes"/>
    public const int MaxAttributeKeyLength = 64;

    /// <inheritdoc cref="MaxAttributes"/>
    public const int MaxAttributeValueLength = 400;

    /// <summary>The supplier with its attributes per receiving entity, for the responses about one supplier.</summary>
    internal static async Task<SupplierResponse> WithEntityAttributesAsync(DocProcDbContext db, Supplier supplier, CancellationToken ct)
    {
        var rows = await db.SupplierEntityAttributes.AsNoTracking()
            .Where(a => a.SupplierId == supplier.Id)
            .Select(a => new { a.ReceivingEntityId, Code = a.ReceivingEntity!.Code, a.AttributesJson, a.UpdatedAt })
            .ToListAsync(ct);
        return SupplierResponse.From(supplier) with
        {
            EntityAttributes = [.. rows.OrderBy(r => r.Code, StringComparer.Ordinal).Select(r => new SupplierEntityAttributesResponse(r.ReceivingEntityId, r.Code, AttributesOf(r.AttributesJson), r.UpdatedAt))],
        };
    }

    /// <summary>The stored attributes as the API shows them (none for a supplier without, or with a value that does not parse).</summary>
    internal static IReadOnlyDictionary<string, JsonElement> AttributesOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, JsonElement>();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
                : new Dictionary<string, JsonElement>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, JsonElement>();
        }
    }

    /// <summary>The attributes as stored: a JSON object, or null for none.</summary>
    internal static string? AttributesJson(IReadOnlyDictionary<string, JsonElement> attributes) =>
        attributes.Count == 0 ? null : JsonSerializer.Serialize(attributes.ToDictionary(p => p.Key.Trim(), p => p.Value, StringComparer.Ordinal));

    /// <summary>At most <see cref="MaxAttributes"/> keys (a letter, then letters, digits, <c>_</c>, <c>-</c> or <c>.</c>), each a scalar.</summary>
    internal static void ValidateAttributes(Errors errors, IReadOnlyDictionary<string, JsonElement>? attributes)
    {
        if (attributes is null)
        {
            return;
        }

        if (attributes.Count > MaxAttributes)
        {
            errors.Add("attributes", $"At most {MaxAttributes} attributes.");
            return;
        }

        foreach (var (key, value) in attributes)
        {
            if (key.Length > MaxAttributeKeyLength || !AttributeKeyPattern().IsMatch(key))
            {
                errors.Add("attributes", $"'{(key.Length > 40 ? key[..40] + "…" : key)}' is not an attribute key: a letter, then letters, digits, '_', '-' or '.', at most {MaxAttributeKeyLength} characters.");
                return;
            }

            var fits = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()!.Length <= MaxAttributeValueLength,
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
                _ => false,
            };
            if (!fits)
            {
                errors.Add($"attributes.{key}", $"An attribute is a string of at most {MaxAttributeValueLength} characters, a number, a boolean or null.");
                return;
            }
        }
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]*$")]
    private static partial Regex AttributeKeyPattern();

    /// <summary>
    /// The list a <c>PATCH</c> gives, or null (leave it as it is) when none is given or it is the stored list again once
    /// both are cleaned the same way (same entries in the same order).
    /// </summary>
    private static IReadOnlyList<string>? Changed(IReadOnlyList<string>? given, IReadOnlyList<string>? stored, Func<IEnumerable<string>?, List<string>> clean) =>
        given is null || (stored is not null && clean(given).SequenceEqual(clean(stored), StringComparer.Ordinal)) ? null : given;

    private static List<string> CleanIbans(IEnumerable<string>? ibans) =>
        CleanList(ibans).Select(i => Identifiers.NormalizeCompact(i)!).Distinct(StringComparer.Ordinal).ToList();

    private static List<string> CleanDomains(IEnumerable<string>? domains) =>
        CleanList(domains).Select(d => d.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// The lists of a supplier write, each counted as it is stored (trimmed, compacted or lower-cased, without blanks and
    /// repeats): at most
    /// <see cref="MaxIbans"/> IBANs that read as one, <see cref="MaxEmailDomains"/> domain names and
    /// <see cref="MaxAliases"/> aliases of at most <see cref="MaxNameLength"/> characters. A list not given is not checked.
    /// </summary>
    private static void ValidateLists(Errors errors, IReadOnlyList<string>? ibans, IReadOnlyList<string>? emailDomains, IReadOnlyList<string>? aliases)
    {
        if (ibans is not null)
        {
            var entries = CleanList(ibans);
            if (entries.Any(i => i.Length > MaxIbanLength))
            {
                errors.Add("ibans", $"An IBAN is at most {MaxIbanLength} characters.", ValidationCodes.MaxLength, ValidationCodes.Params(("maxLength", MaxIbanLength)));
            }
            else if (CleanIbans(entries).Count > MaxIbans)
            {
                TooMany(errors, "ibans", MaxIbans);
            }
            else if (entries.FirstOrDefault(i => !IbanPattern().IsMatch(Identifiers.NormalizeCompact(i)!)) is { } notAnIban)
            {
                errors.Add("ibans", $"'{notAnIban}' is not an IBAN: a country code, two check digits and 11 to 30 letters or digits (spaces, dots and dashes are ignored).", ValidationCodes.Iban, ValidationCodes.ValueParam(notAnIban));
            }
        }

        if (emailDomains is not null)
        {
            var entries = CleanList(emailDomains);
            if (entries.Any(d => d.Length > MaxEmailDomainLength))
            {
                errors.Add("emailDomains", $"A domain name is at most {MaxEmailDomainLength} characters.", ValidationCodes.MaxLength, ValidationCodes.Params(("maxLength", MaxEmailDomainLength)));
            }
            else if (CleanDomains(entries).Count > MaxEmailDomains)
            {
                TooMany(errors, "emailDomains", MaxEmailDomains);
            }
            else if (entries.FirstOrDefault(d => !DomainPattern().IsMatch(d.ToLowerInvariant())) is { } notADomain)
            {
                errors.Add("emailDomains", $"'{notADomain}' is not a domain name such as example.com (the part of a sender's address after the @).", ValidationCodes.EmailDomain, ValidationCodes.ValueParam(notADomain));
            }
        }

        if (aliases is not null)
        {
            var entries = CleanList(aliases);
            if (entries.Any(a => a.Length > MaxNameLength))
            {
                errors.Add("aliases", $"An alias is at most {MaxNameLength} characters.", ValidationCodes.MaxLength, ValidationCodes.Params(("maxLength", MaxNameLength)));
            }
            else if (entries.Count > MaxAliases)
            {
                TooMany(errors, "aliases", MaxAliases);
            }
        }

        static void TooMany(Errors errors, string field, int max) =>
            errors.Add(field, $"{field} holds at most {max.ToString(CultureInfo.InvariantCulture)} entries.", ValidationCodes.MaxItems, ValidationCodes.Params(("maxItems", max)));
    }

    private static void ValidateNormalizedName(Errors errors, string normalizedName)
    {
        if (normalizedName.Length == 0)
        {
            errors.Add("name", "name must contain at least one letter or digit.");
        }
        else if (normalizedName.Length > MaxNameLength)
        {
            errors.Add("name", $"name folds to more than {MaxNameLength} characters for matching; shorten it.");
        }
    }
}
