using DocProc.Api.Auth;
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
    internal static SupplierResponse From(Supplier s) =>
        new(s.Id, s.Name, s.NormalizedName, s.VatId, s.TaxId, s.Ibans, s.EmailDomains, s.Aliases, s.DefaultCurrency, s.AlwaysReview, s.GlobalSupplierId, s.CreatedAt, s.UpdatedAt);
}

public sealed record CreateSupplierRequest(
    string Name,
    string? VatId,
    string? TaxId,
    IReadOnlyList<string>? Ibans,
    IReadOnlyList<string>? EmailDomains,
    IReadOnlyList<string>? Aliases,
    string? DefaultCurrency,
    bool? AlwaysReview);

/// <summary>Every field optional; absent fields are left as they are. A string set to empty clears it; a list replaces the stored list.</summary>
public sealed record PatchSupplierRequest(
    string? Name,
    string? VatId,
    string? TaxId,
    IReadOnlyList<string>? Ibans,
    IReadOnlyList<string>? EmailDomains,
    IReadOnlyList<string>? Aliases,
    string? DefaultCurrency,
    bool? AlwaysReview);

/// <summary>One recent document of a supplier, for the supplier activity view (docs/05 "Suppliers: ... activity").</summary>
public sealed record SupplierActivityItem(Guid Id, string Status, string Type, string? DocumentNumber, DateOnly? IssueDate, decimal? TotalGross, string? Currency, DateTimeOffset CreatedAt);

/// <summary>
/// Suppliers of the current tenant (docs/02 §1 Supplier). <c>NormalizedName</c> is always derived with
/// <see cref="Identifiers.NormalizeName"/>, the same function stage 5 matches with, so what the portal creates and
/// what the pipeline looks up agree.
/// </summary>
public static class SupplierEndpoints
{
    private const int ActivitySize = 20;

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

        suppliers.MapPost("/", async Task<Results<Created<SupplierResponse>, ValidationProblem, ProblemHttpResult>> (CreateSupplierRequest request, [FromServices] DocProcDbContext db, ITenantContext tenant, TimeProvider clock, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Suppliers") is { } problem)
            {
                return problem;
            }

            var errors = new Errors().Require("name", request.Name, 300).Optional("vatId", request.VatId, 32).Optional("taxId", request.TaxId, 32);
            if (request.DefaultCurrency is not null && !IsCurrency(request.DefaultCurrency))
            {
                errors.Add("defaultCurrency", "Currency must be a three-letter upper-case ISO 4217 code.");
            }

            var normalizedName = string.IsNullOrWhiteSpace(request.Name) ? string.Empty : Identifiers.NormalizeName(request.Name);
            if (!string.IsNullOrWhiteSpace(request.Name) && normalizedName.Length == 0)
            {
                errors.Add("name", "name must contain at least one letter or digit.");
            }

            if (errors.ToProblem() is { } invalid)
            {
                return invalid;
            }

            if (await CurrentTenantAsync(db, ct) is null)
            {
                return UnknownTenant();
            }

            if (await db.Suppliers.AnyAsync(s => s.NormalizedName == normalizedName, ct))
            {
                return Conflict("Supplier exists", $"A supplier whose name folds to '{normalizedName}' already exists in this tenant; add an alias to it instead.");
            }

            var now = clock.GetUtcNow();
            var supplier = new Supplier
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.TenantId!.Value,
                Name = request.Name.Trim(),
                NormalizedName = normalizedName,
                VatId = Identifiers.NormalizeCompact(request.VatId),
                TaxId = TrimToNull(request.TaxId),
                Ibans = CleanList(request.Ibans).Select(i => Identifiers.NormalizeCompact(i)!).ToList(),
                EmailDomains = CleanList(request.EmailDomains).Select(d => d.ToLowerInvariant()).ToList(),
                Aliases = CleanList(request.Aliases),
                DefaultCurrency = request.DefaultCurrency,
                AlwaysReview = request.AlwaysReview ?? false,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Suppliers.Add(supplier);
            await db.SaveChangesAsync(ct);
            return TypedResults.Created($"/api/v1/suppliers/{supplier.Id}", SupplierResponse.From(supplier));
        })
        .RequireAuthorization(Policies.TenantAdmin)
        .WithName("CreateSupplier")
        .WithSummary("Creates a supplier; the folded name used for matching is derived server-side.");

        suppliers.MapGet("/{id:guid}", async Task<Results<Ok<SupplierResponse>, NotFound, ProblemHttpResult>> (Guid id, [FromServices] DocProcDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Suppliers") is { } problem)
            {
                return problem;
            }

            var supplier = await db.Suppliers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct);
            return supplier is null ? TypedResults.NotFound() : TypedResults.Ok(SupplierResponse.From(supplier));
        })
        .RequireAuthorization(Policies.Read)
        .WithName("GetSupplier")
        .WithSummary("One supplier of the current tenant.");

        suppliers.MapPatch("/{id:guid}", async Task<Results<Ok<SupplierResponse>, NotFound, ValidationProblem, ProblemHttpResult>> (Guid id, PatchSupplierRequest request, [FromServices] DocProcDbContext db, ITenantContext tenant, TimeProvider clock, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Suppliers") is { } problem)
            {
                return problem;
            }

            var errors = new Errors().Optional("name", request.Name, 300).Optional("vatId", request.VatId, 32).Optional("taxId", request.TaxId, 32);
            string? normalizedName = null;
            if (request.Name is not null)
            {
                normalizedName = Identifiers.NormalizeName(request.Name);
                if (normalizedName.Length == 0)
                {
                    errors.Add("name", "name must contain at least one letter or digit.");
                }
            }

            if (!string.IsNullOrEmpty(request.DefaultCurrency) && !IsCurrency(request.DefaultCurrency))
            {
                errors.Add("defaultCurrency", "Currency must be a three-letter upper-case ISO 4217 code.");
            }

            if (errors.ToProblem() is { } invalid)
            {
                return invalid;
            }

            var supplier = await db.Suppliers.SingleOrDefaultAsync(s => s.Id == id, ct);
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

            if (request.Ibans is not null)
            {
                supplier.Ibans = CleanList(request.Ibans).Select(i => Identifiers.NormalizeCompact(i)!).ToList();
            }

            if (request.EmailDomains is not null)
            {
                supplier.EmailDomains = CleanList(request.EmailDomains).Select(d => d.ToLowerInvariant()).ToList();
            }

            if (request.Aliases is not null)
            {
                supplier.Aliases = CleanList(request.Aliases);
            }

            if (request.DefaultCurrency is not null)
            {
                supplier.DefaultCurrency = TrimToNull(request.DefaultCurrency);
            }

            if (request.AlwaysReview is { } alwaysReview)
            {
                supplier.AlwaysReview = alwaysReview;
            }

            supplier.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(SupplierResponse.From(supplier));
        })
        .RequireAuthorization(Policies.TenantAdmin)
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
        .WithSummary("Deletes a supplier no document references; otherwise 409.");

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
}
