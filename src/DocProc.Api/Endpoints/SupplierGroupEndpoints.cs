using System.Text.Json;
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

/// <summary>A supplier group (docs/11 §6.4) with the number of its members.</summary>
public sealed record SupplierGroupResponse(Guid Id, string Name, IReadOnlyDictionary<string, JsonElement> Attributes, JsonElement Settings, int SupplierCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>The body of <c>POST /supplier-groups</c>.</summary>
public sealed record CreateSupplierGroupRequest(string Name, IReadOnlyDictionary<string, JsonElement>? Attributes = null, JsonElement? Settings = null);

/// <summary>The body of <c>PATCH /supplier-groups/{id}</c>: every member optional; attributes and settings replace the stored ones.</summary>
public sealed record PatchSupplierGroupRequest(string? Name = null, IReadOnlyDictionary<string, JsonElement>? Attributes = null, JsonElement? Settings = null);

/// <summary>A receiving entity (docs/11 §6.4): one of the tenant's own companies or branches.</summary>
public sealed record ReceivingEntityResponse(Guid Id, string Code, string Name, IReadOnlyList<string> VatIds, JsonElement? Addresses, IReadOnlyDictionary<string, JsonElement> Attributes, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>The body of <c>POST /receiving-entities</c>.</summary>
public sealed record CreateReceivingEntityRequest(string Code, string Name, IReadOnlyList<string>? VatIds = null, JsonElement? Addresses = null, IReadOnlyDictionary<string, JsonElement>? Attributes = null);

/// <summary>The body of <c>PATCH /receiving-entities/{id}</c>: every member optional; lists and attributes replace the stored ones.</summary>
public sealed record PatchReceivingEntityRequest(string? Code = null, string? Name = null, IReadOnlyList<string>? VatIds = null, JsonElement? Addresses = null, IReadOnlyDictionary<string, JsonElement>? Attributes = null);

/// <summary>
/// Supplier groups, receiving entities and a supplier's attributes per entity (docs/11 §6.4), behind the
/// <c>sourceRouting</c> switch. Reads need <c>Read</c>, writes <c>TenantAdmin</c> (supplier master data). Attributes are
/// scalars, checked against the tenant's attribute schema when it has one (its <c>required</c> applies to a supplier's
/// own attributes only).
/// </summary>
public static class SupplierGroupEndpoints
{
    public const int MaxVatIds = 10;
    public const int MaxAddressesLength = 8 * 1024;

    public static RouteGroupBuilder MapSupplierGroups(this RouteGroupBuilder group)
    {
        var groups = group.MapGroup("/supplier-groups").WithTags("Suppliers");
        groups.MapGet("/", ListGroupsAsync).RequireAuthorization(Policies.Read).WithName("ListSupplierGroups")
            .WithSummary("The tenant's supplier groups, by name, with their member count.");
        groups.MapPost("/", CreateGroupAsync).RequireAuthorization(Policies.TenantAdmin).WithName("CreateSupplierGroup")
            .WithMetadata(new RequestSizeLimitAttribute(SupplierEndpoints.MaxRequestBytes))
            .WithSummary("Creates a supplier group whose attributes and settings its members inherit (docs/11 §6.4).");
        groups.MapGet("/{id:guid}", GetGroupAsync).RequireAuthorization(Policies.Read).WithName("GetSupplierGroup")
            .WithSummary("One supplier group.");
        groups.MapPatch("/{id:guid}", PatchGroupAsync).RequireAuthorization(Policies.TenantAdmin).WithName("UpdateSupplierGroup")
            .WithMetadata(new RequestSizeLimitAttribute(SupplierEndpoints.MaxRequestBytes))
            .WithSummary("Renames a supplier group or replaces its attributes or settings.");
        groups.MapDelete("/{id:guid}", DeleteGroupAsync).RequireAuthorization(Policies.TenantAdmin).WithName("DeleteSupplierGroup")
            .WithSummary("Deletes a supplier group without members (409 otherwise).");

        var entities = group.MapGroup("/receiving-entities").WithTags("Suppliers");
        entities.MapGet("/", ListEntitiesAsync).RequireAuthorization(Policies.Read).WithName("ListReceivingEntities")
            .WithSummary("The tenant's receiving entities (its own companies and branches), by code.");
        entities.MapPost("/", CreateEntityAsync).RequireAuthorization(Policies.TenantAdmin).WithName("CreateReceivingEntity")
            .WithMetadata(new RequestSizeLimitAttribute(SupplierEndpoints.MaxRequestBytes))
            .WithSummary("Creates a receiving entity: a code unique in the tenant, a name, VAT ids, addresses and attributes (docs/11 §6.4).");
        entities.MapGet("/{id:guid}", GetEntityAsync).RequireAuthorization(Policies.Read).WithName("GetReceivingEntity")
            .WithSummary("One receiving entity.");
        entities.MapPatch("/{id:guid}", PatchEntityAsync).RequireAuthorization(Policies.TenantAdmin).WithName("UpdateReceivingEntity")
            .WithMetadata(new RequestSizeLimitAttribute(SupplierEndpoints.MaxRequestBytes))
            .WithSummary("Changes a receiving entity.");
        entities.MapDelete("/{id:guid}", DeleteEntityAsync).RequireAuthorization(Policies.TenantAdmin).WithName("DeleteReceivingEntity")
            .WithSummary("Deletes a receiving entity no document, source rule, channel or supplier attribute names (409 otherwise).");

        var attributes = group.MapGroup("/suppliers/{supplierId:guid}/entity-attributes").WithTags("Suppliers");
        attributes.MapGet("/", ListEntityAttributesAsync).RequireAuthorization(Policies.Read).WithName("ListSupplierEntityAttributes")
            .WithSummary("The supplier's attributes per receiving entity.");
        attributes.MapPut("/{entityId:guid}", PutEntityAttributesAsync).RequireAuthorization(Policies.TenantAdmin).WithName("PutSupplierEntityAttributes")
            .WithMetadata(new RequestSizeLimitAttribute(SupplierEndpoints.MaxRequestBytes))
            .WithSummary("Sets the supplier's attributes in one receiving entity (the vendor number in company 1000); the values view merges them over the supplier's.");
        attributes.MapDelete("/{entityId:guid}", DeleteEntityAttributesAsync).RequireAuthorization(Policies.TenantAdmin).WithName("DeleteSupplierEntityAttributes")
            .WithSummary("Removes the supplier's attributes in one receiving entity.");
        return group;
    }

    private static async Task<Results<Ok<IReadOnlyList<SupplierGroupResponse>>, ProblemHttpResult>> ListGroupsAsync(ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await SupplierConfigSupport.GuardAsync(tenant, features, "Supplier groups", ct) is { } problem)
        {
            return problem;
        }

        var rows = await db.SupplierGroups.AsNoTracking().ToListAsync(ct);
        var counts = await db.Suppliers.AsNoTracking().Where(s => s.GroupId != null).GroupBy(s => s.GroupId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return TypedResults.Ok<IReadOnlyList<SupplierGroupResponse>>([.. rows.OrderBy(g => g.Name, StringComparer.Ordinal).Select(g => GroupResponse(g, counts.FirstOrDefault(c => c.Key == g.Id)?.Count ?? 0))]);
    }

    private static async Task<Results<Ok<SupplierGroupResponse>, ProblemHttpResult>> GetGroupAsync(Guid id, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await SupplierConfigSupport.GuardAsync(tenant, features, "Supplier groups", ct) is { } problem)
        {
            return problem;
        }

        var row = await db.SupplierGroups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == id, ct);
        return row is null ? NotFound("Supplier group", id) : TypedResults.Ok(GroupResponse(row, await db.Suppliers.CountAsync(s => s.GroupId == id, ct)));
    }

    private static async Task<Results<Created<SupplierGroupResponse>, ValidationProblem, ProblemHttpResult>> CreateGroupAsync(CreateSupplierGroupRequest request, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, TimeProvider clock, CancellationToken ct)
    {
        if (await SupplierConfigSupport.GuardAsync(tenant, features, "Supplier groups", ct) is { } problem)
        {
            return problem;
        }

        var errors = new Errors().Require("name", request.Name, SupplierGroup.MaxNameLength);
        var settings = await ValidateConfigAsync(db, errors, request.Attributes, request.Settings, ct);
        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        var name = request.Name.Trim();
        if (await db.SupplierGroups.AnyAsync(g => g.Name == name, ct))
        {
            return Conflict("Supplier group exists", $"A supplier group named '{name}' already exists in this tenant.");
        }

        var now = clock.GetUtcNow();
        var row = new SupplierGroup
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.TenantId!.Value,
            Name = name,
            AttributesJson = request.Attributes is null ? null : SupplierEndpoints.AttributesJson(request.Attributes),
            SettingsJson = settings?.ToJson(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.SupplierGroups.Add(row);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/supplier-groups/{row.Id}", GroupResponse(row, 0));
    }

    private static async Task<Results<Ok<SupplierGroupResponse>, ValidationProblem, ProblemHttpResult>> PatchGroupAsync(Guid id, PatchSupplierGroupRequest request, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, TimeProvider clock, CancellationToken ct)
    {
        if (await SupplierConfigSupport.GuardAsync(tenant, features, "Supplier groups", ct) is { } problem)
        {
            return problem;
        }

        var errors = new Errors().Optional("name", request.Name, SupplierGroup.MaxNameLength);
        if (request.Name is not null && string.IsNullOrWhiteSpace(request.Name))
        {
            errors.Add("name", "name cannot be blank.", ValidationCodes.NotBlank);
        }

        var settings = await ValidateConfigAsync(db, errors, request.Attributes, request.Settings, ct);
        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        var row = await db.SupplierGroups.SingleOrDefaultAsync(g => g.Id == id, ct);
        if (row is null)
        {
            return NotFound("Supplier group", id);
        }

        if (request.Name is { } newName)
        {
            var name = newName.Trim();
            if (await db.SupplierGroups.AnyAsync(g => g.Id != id && g.Name == name, ct))
            {
                return Conflict("Supplier group exists", $"A supplier group named '{name}' already exists in this tenant.");
            }

            row.Name = name;
        }

        if (request.Attributes is not null)
        {
            row.AttributesJson = SupplierEndpoints.AttributesJson(request.Attributes);
        }

        if (settings is not null)
        {
            row.SettingsJson = settings.ToJson();
        }

        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(GroupResponse(row, await db.Suppliers.CountAsync(s => s.GroupId == id, ct)));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteGroupAsync(Guid id, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await SupplierConfigSupport.GuardAsync(tenant, features, "Supplier groups", ct) is { } problem)
        {
            return problem;
        }

        var row = await db.SupplierGroups.SingleOrDefaultAsync(g => g.Id == id, ct);
        if (row is null)
        {
            return NotFound("Supplier group", id);
        }

        if (await db.Suppliers.AnyAsync(s => s.GroupId == id, ct))
        {
            return Conflict("Supplier group in use", "Suppliers belong to this group; take them out of it first.");
        }

        db.SupplierGroups.Remove(row);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<IReadOnlyList<ReceivingEntityResponse>>, ProblemHttpResult>> ListEntitiesAsync(ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await SupplierConfigSupport.GuardAsync(tenant, features, "Receiving entities", ct) is { } problem)
        {
            return problem;
        }

        var rows = await db.ReceivingEntities.AsNoTracking().ToListAsync(ct);
        return TypedResults.Ok<IReadOnlyList<ReceivingEntityResponse>>([.. rows.OrderBy(e => e.Code, StringComparer.Ordinal).Select(EntityResponse)]);
    }

    private static async Task<Results<Ok<ReceivingEntityResponse>, ProblemHttpResult>> GetEntityAsync(Guid id, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await SupplierConfigSupport.GuardAsync(tenant, features, "Receiving entities", ct) is { } problem)
        {
            return problem;
        }

        var row = await db.ReceivingEntities.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, ct);
        return row is null ? NotFound("Receiving entity", id) : TypedResults.Ok(EntityResponse(row));
    }

    private static async Task<Results<Created<ReceivingEntityResponse>, ValidationProblem, ProblemHttpResult>> CreateEntityAsync(CreateReceivingEntityRequest request, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, TimeProvider clock, CancellationToken ct)
    {
        if (await SupplierConfigSupport.GuardAsync(tenant, features, "Receiving entities", ct) is { } problem)
        {
            return problem;
        }

        var errors = new Errors().Require("code", request.Code, ReceivingEntity.MaxCodeLength).Require("name", request.Name, ReceivingEntity.MaxNameLength);
        var vatIds = ValidateEntity(errors, request.VatIds, request.Addresses);
        SupplierEndpoints.ValidateAttributes(errors, request.Attributes);
        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        await SupplierConfigSupport.ValidateAgainstSchemaAsync(db, errors, request.Attributes, checkRequired: false, ct);
        if (errors.ToProblem() is { } schemaInvalid)
        {
            return schemaInvalid;
        }

        var code = request.Code.Trim();
        if (await db.ReceivingEntities.AnyAsync(e => e.Code == code, ct))
        {
            return Conflict("Receiving entity exists", $"A receiving entity with code '{code}' already exists in this tenant.");
        }

        var now = clock.GetUtcNow();
        var row = new ReceivingEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.TenantId!.Value,
            Code = code,
            Name = request.Name.Trim(),
            VatIds = vatIds ?? [],
            AddressesJson = request.Addresses is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } a ? a.GetRawText() : null,
            AttributesJson = request.Attributes is null ? null : SupplierEndpoints.AttributesJson(request.Attributes),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ReceivingEntities.Add(row);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/receiving-entities/{row.Id}", EntityResponse(row));
    }

    private static async Task<Results<Ok<ReceivingEntityResponse>, ValidationProblem, ProblemHttpResult>> PatchEntityAsync(Guid id, PatchReceivingEntityRequest request, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, TimeProvider clock, CancellationToken ct)
    {
        if (await SupplierConfigSupport.GuardAsync(tenant, features, "Receiving entities", ct) is { } problem)
        {
            return problem;
        }

        var errors = new Errors().Optional("code", request.Code, ReceivingEntity.MaxCodeLength).Optional("name", request.Name, ReceivingEntity.MaxNameLength);
        if (request.Code is not null && string.IsNullOrWhiteSpace(request.Code))
        {
            errors.Add("code", "code cannot be blank.", ValidationCodes.NotBlank);
        }

        if (request.Name is not null && string.IsNullOrWhiteSpace(request.Name))
        {
            errors.Add("name", "name cannot be blank.", ValidationCodes.NotBlank);
        }

        var vatIds = ValidateEntity(errors, request.VatIds, request.Addresses);
        SupplierEndpoints.ValidateAttributes(errors, request.Attributes);
        if (errors.Count == 0)
        {
            await SupplierConfigSupport.ValidateAgainstSchemaAsync(db, errors, request.Attributes, checkRequired: false, ct);
        }

        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        var row = await db.ReceivingEntities.SingleOrDefaultAsync(e => e.Id == id, ct);
        if (row is null)
        {
            return NotFound("Receiving entity", id);
        }

        if (request.Code is { } newCode)
        {
            var code = newCode.Trim();
            if (await db.ReceivingEntities.AnyAsync(e => e.Id != id && e.Code == code, ct))
            {
                return Conflict("Receiving entity exists", $"A receiving entity with code '{code}' already exists in this tenant.");
            }

            row.Code = code;
        }

        if (request.Name is { } name)
        {
            row.Name = name.Trim();
        }

        if (vatIds is not null)
        {
            row.VatIds = vatIds;
        }

        if (request.Addresses is { } addresses)
        {
            row.AddressesJson = addresses.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? addresses.GetRawText() : null;
        }

        if (request.Attributes is not null)
        {
            row.AttributesJson = SupplierEndpoints.AttributesJson(request.Attributes);
        }

        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(EntityResponse(row));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteEntityAsync(Guid id, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await SupplierConfigSupport.GuardAsync(tenant, features, "Receiving entities", ct) is { } problem)
        {
            return problem;
        }

        var row = await db.ReceivingEntities.SingleOrDefaultAsync(e => e.Id == id, ct);
        if (row is null)
        {
            return NotFound("Receiving entity", id);
        }

        if (await db.Documents.AnyAsync(d => d.ReceivingEntityId == id, ct)
            || await db.SupplierSourceRules.AnyAsync(r => r.ReceivingEntityId == id, ct)
            || await db.IngestionChannels.AnyAsync(c => c.DefaultReceivingEntityId == id, ct)
            || await db.SupplierEntityAttributes.AnyAsync(a => a.ReceivingEntityId == id, ct))
        {
            return Conflict("Receiving entity in use", "Documents, source rules, channels or supplier attributes name this receiving entity.");
        }

        db.ReceivingEntities.Remove(row);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<IReadOnlyList<SupplierEntityAttributesResponse>>, ProblemHttpResult>> ListEntityAttributesAsync(Guid supplierId, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await SupplierConfigSupport.GuardAsync(tenant, features, "Supplier attributes", ct) is { } problem)
        {
            return problem;
        }

        var supplier = await db.Suppliers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == supplierId, ct);
        return supplier is null ? NotFound("Supplier", supplierId) : TypedResults.Ok((await SupplierEndpoints.WithEntityAttributesAsync(db, supplier, ct)).EntityAttributes);
    }

    private static async Task<Results<Ok<SupplierEntityAttributesResponse>, ValidationProblem, ProblemHttpResult>> PutEntityAttributesAsync(Guid supplierId, Guid entityId, SupplierEntityAttributesRequest request, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, TimeProvider clock, CancellationToken ct)
    {
        if (await SupplierConfigSupport.GuardAsync(tenant, features, "Supplier attributes", ct) is { } problem)
        {
            return problem;
        }

        var errors = new Errors();
        if (request.Attributes is null)
        {
            errors.Add("attributes", "attributes is an object.");
        }

        SupplierEndpoints.ValidateAttributes(errors, request.Attributes);
        if (errors.Count == 0)
        {
            await SupplierConfigSupport.ValidateAgainstSchemaAsync(db, errors, request.Attributes, checkRequired: false, ct);
        }

        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        if (!await db.Suppliers.AnyAsync(s => s.Id == supplierId, ct))
        {
            return NotFound("Supplier", supplierId);
        }

        var entity = await db.ReceivingEntities.AsNoTracking().SingleOrDefaultAsync(e => e.Id == entityId, ct);
        if (entity is null)
        {
            return NotFound("Receiving entity", entityId);
        }

        var now = clock.GetUtcNow();
        var json = SupplierEndpoints.AttributesJson(request.Attributes!) ?? "{}";
        var row = await db.SupplierEntityAttributes.SingleOrDefaultAsync(a => a.SupplierId == supplierId && a.ReceivingEntityId == entityId, ct);
        if (row is null)
        {
            row = new SupplierEntityAttributes { TenantId = tenant.TenantId!.Value, SupplierId = supplierId, ReceivingEntityId = entityId, AttributesJson = json, UpdatedAt = now };
            db.SupplierEntityAttributes.Add(row);
        }
        else
        {
            row.AttributesJson = json;
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new SupplierEntityAttributesResponse(entityId, entity.Code, SupplierEndpoints.AttributesOf(json), now));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteEntityAttributesAsync(Guid supplierId, Guid entityId, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await SupplierConfigSupport.GuardAsync(tenant, features, "Supplier attributes", ct) is { } problem)
        {
            return problem;
        }

        var row = await db.SupplierEntityAttributes.SingleOrDefaultAsync(a => a.SupplierId == supplierId && a.ReceivingEntityId == entityId, ct);
        if (row is null)
        {
            return NotFound("Supplier entity attributes", entityId);
        }

        db.SupplierEntityAttributes.Remove(row);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Application.Suppliers.SupplierSettings?> ValidateConfigAsync(DocProcDbContext db, Errors errors, IReadOnlyDictionary<string, JsonElement>? attributes, JsonElement? settings, CancellationToken ct)
    {
        SupplierEndpoints.ValidateAttributes(errors, attributes);
        if (errors.Count == 0)
        {
            await SupplierConfigSupport.ValidateAgainstSchemaAsync(db, errors, attributes, checkRequired: false, ct);
        }

        return settings is { } element ? SupplierConfigSupport.ReadSettings(errors, element) : null;
    }

    /// <summary>The VAT ids compacted (null when none are given) and the addresses' shape and size checked.</summary>
    private static List<string>? ValidateEntity(Errors errors, IReadOnlyList<string>? vatIds, JsonElement? addresses)
    {
        List<string>? cleaned = null;
        if (vatIds is not null)
        {
            cleaned = [.. vatIds.Select(Identifiers.NormalizeCompact).Where(v => v is { Length: > 0 }).Select(v => v!).Distinct(StringComparer.Ordinal)];
            if (cleaned.Count > MaxVatIds)
            {
                errors.Add("vatIds", $"vatIds holds at most {MaxVatIds} VAT ids.");
            }
            else if (cleaned.Any(v => v.Length > 32))
            {
                errors.Add("vatIds", "A VAT id is at most 32 characters.");
            }
        }

        if (addresses is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null or JsonValueKind.Undefined) })
        {
            errors.Add("addresses", "addresses is a JSON object or array.");
        }
        else if (addresses is { } given && given.ValueKind is JsonValueKind.Object or JsonValueKind.Array && given.GetRawText().Length > MaxAddressesLength)
        {
            errors.Add("addresses", $"addresses is at most {MaxAddressesLength / 1024} KB.");
        }

        return cleaned;
    }

    private static SupplierGroupResponse GroupResponse(SupplierGroup g, int count) =>
        new(g.Id, g.Name, SupplierEndpoints.AttributesOf(g.AttributesJson), SupplierConfigSupport.SettingsOf(g.SettingsJson), count, g.CreatedAt, g.UpdatedAt);

    private static ReceivingEntityResponse EntityResponse(ReceivingEntity e)
    {
        JsonElement? addresses = null;
        if (!string.IsNullOrWhiteSpace(e.AddressesJson))
        {
            try
            {
                using var document = JsonDocument.Parse(e.AddressesJson);
                addresses = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                addresses = null;
            }
        }

        return new ReceivingEntityResponse(e.Id, e.Code, e.Name, e.VatIds, addresses, SupplierEndpoints.AttributesOf(e.AttributesJson), e.CreatedAt, e.UpdatedAt);
    }

    internal static ProblemHttpResult NotFound(string what, Guid id) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: $"{what} not found", detail: $"{what} {id} does not exist in this tenant.");
}
