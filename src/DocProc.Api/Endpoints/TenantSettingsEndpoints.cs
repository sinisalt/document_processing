using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Application.Routing;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

/// <summary>The read-only identity of the current tenant; platform staff change it through <c>/tenants/{id}</c>.</summary>
public sealed record TenantIdentity(Guid Id, string Name, string Slug, string LegalName, string? VatId, string DefaultCurrency, string CountryCode, string TimeZone)
{
    internal static TenantIdentity From(Tenant t) => new(t.Id, t.Name, t.Slug, t.LegalName, t.VatId, t.DefaultCurrency, t.CountryCode, t.TimeZone);
}

/// <summary>
/// The per-tenant switches of docs/11 §3 (principle 6), all off by default. <c>configurableTypes</c> opens the type
/// catalogue (adopt, copy, edit, publish; P1) and lets the tenant's copies resolve; <c>customTypes</c> also allows types
/// of the tenant's own; the others belong to later phases and change nothing yet.
/// </summary>
public sealed record TenantFeaturesDto(bool ConfigurableTypes, bool Outputs, bool SourceRouting, bool SupplierLayouts, bool CustomTypes, bool StructuredSources)
{
    internal static TenantFeaturesDto From(TenantFeatureFlags? f) => f is null
        ? new(false, false, false, false, false, false)
        : new(f.ConfigurableTypes, f.Outputs, f.SourceRouting, f.SupplierLayouts, f.CustomTypes, f.StructuredSources);
}

/// <summary>
/// Review policy, retention and the feature switches (docs/02 §1 "settings", docs/11 §18 "Settings"); the body of PUT
/// and the <c>settings</c> part of GET. <see cref="Features"/> is optional on PUT: left out, the switches stay as they are.
/// The supplier configuration of P4 (docs/11 §6.4, §7.1) is optional on PUT as well, and changing it needs
/// <c>sourceRouting</c>: <see cref="SupplierAttributeSchema"/> (a JSON Schema object of the typed subset; <c>{}</c>
/// removes it), <see cref="LayoutsRequiredByDefault"/> (the default of a supplier's <c>layouts.required: inherit</c>)
/// and <see cref="TrustedForwarderDomains"/> (forwarders unwrapped for every mailbox of the tenant).
/// <see cref="LayoutReviewWarmup"/> (docs/11 §8.3 <c>layoutActivation</c>, decision 2 of §24) is how many documents
/// under a newly activated layout version go to review, 5 by default; changing it needs <c>supplierLayouts</c>.
/// </summary>
public sealed record TenantSettingsDto(
    double AutoApproveConfidenceThreshold,
    IReadOnlyList<Guid> AlwaysReviewSupplierIds,
    int RetentionYears,
    bool ShareExtractorProfiles,
    TenantFeaturesDto? Features = null,
    JsonElement? SupplierAttributeSchema = null,
    bool? LayoutsRequiredByDefault = null,
    IReadOnlyList<string>? TrustedForwarderDomains = null,
    int? LayoutReviewWarmup = null)
{
    internal static TenantSettingsDto From(TenantSettings s) => new(
        s.AutoApproveConfidenceThreshold,
        s.AlwaysReviewSupplierIds,
        s.RetentionYears,
        s.ShareExtractorProfiles,
        TenantFeaturesDto.From(s.Features),
        SchemaOf(s.SupplierAttributeSchema),
        s.LayoutsRequiredByDefault,
        s.TrustedForwarderDomains ?? [],
        s.EffectiveLayoutReviewWarmup());

    private static JsonElement? SchemaOf(string? json)
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
}

public sealed record TenantSettingsResponse(TenantIdentity Tenant, TenantSettingsDto Settings);

/// <summary>Review policy, retention and profile sharing of the current tenant (docs/05 "settings: review policy, thresholds, retention").</summary>
public static class TenantSettingsEndpoints
{
    public const int MinRetentionYears = 1;
    public const int MaxRetentionYears = 30;

    /// <summary>Whether two stored schemas say the same (whitespace and member order aside).</summary>
    private static bool SameSchema(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        try
        {
            using var left = JsonDocument.Parse(a);
            using var right = JsonDocument.Parse(b);
            return JsonElement.DeepEquals(left.RootElement, right.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static RouteGroupBuilder MapTenantSettings(this RouteGroupBuilder group)
    {
        var settings = group.MapGroup("/settings").WithTags("Settings");

        settings.MapGet("/", async Task<Results<Ok<TenantSettingsResponse>, ProblemHttpResult>> ([FromServices] DocProcDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Settings") is { } problem)
            {
                return problem;
            }

            var current = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(ct);
            return current is null ? UnknownTenant() : TypedResults.Ok(new TenantSettingsResponse(TenantIdentity.From(current), TenantSettingsDto.From(current.Settings)));
        })
        .RequireAuthorization(Policies.Read)
        .WithName("GetTenantSettings")
        .WithSummary("Review policy, retention and feature switches of the current tenant, with its read-only identity.");

        settings.MapPut("/", async Task<Results<Ok<TenantSettingsResponse>, ValidationProblem, ProblemHttpResult>> (TenantSettingsDto request, [FromServices] DocProcDbContext db, ITenantContext tenant, TimeProvider clock, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Settings") is { } problem)
            {
                return problem;
            }

            var errors = new Errors();
            if (double.IsNaN(request.AutoApproveConfidenceThreshold) || request.AutoApproveConfidenceThreshold is < 0 or > 1)
            {
                errors.Add("autoApproveConfidenceThreshold", "The threshold must be between 0 and 1.", ValidationCodes.Range, ValidationCodes.Params(("min", 0), ("max", 1)));
            }

            if (request.RetentionYears is < MinRetentionYears or > MaxRetentionYears)
            {
                errors.Add("retentionYears", $"Retention must be between {MinRetentionYears} and {MaxRetentionYears} years.", ValidationCodes.Range, ValidationCodes.Params(("min", MinRetentionYears), ("max", MaxRetentionYears)));
            }

            var supplierIds = (request.AlwaysReviewSupplierIds ?? []).Distinct().ToList();
            if (supplierIds.Count > 0)
            {
                var known = await db.Suppliers.Where(s => supplierIds.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct);
                var unknown = supplierIds.Except(known).ToList();
                if (unknown.Count > 0)
                {
                    errors.Add("alwaysReviewSupplierIds", $"Unknown supplier id(s): {string.Join(", ", unknown)}.");
                }
            }

            string? schema = null;
            if (request.SupplierAttributeSchema is { } schemaElement && schemaElement.ValueKind != JsonValueKind.Null)
            {
                var raw = schemaElement.ValueKind == JsonValueKind.Object && !schemaElement.EnumerateObject().Any() ? null : schemaElement.GetRawText();
                if (raw is not null && Application.Suppliers.SupplierAttributeSchema.TryParse(raw, out var schemaErrors) is null)
                {
                    foreach (var error in schemaErrors.Take(20))
                    {
                        errors.Add("supplierAttributeSchema", error);
                    }
                }

                schema = raw;
            }

            List<string>? forwarders = null;
            if (request.TrustedForwarderDomains is { } domains)
            {
                forwarders = [.. domains.Select(SourceConditions.Domain).Where(d => d is not null).Select(d => d!).Distinct(StringComparer.Ordinal)];
                if (forwarders.Count > 20)
                {
                    errors.Add("trustedForwarderDomains", "trustedForwarderDomains holds at most 20 domains.");
                }
                else if (forwarders.FirstOrDefault(d => !d.Contains('.', StringComparison.Ordinal) || d.Length > 253 || d.Contains('@', StringComparison.Ordinal)) is { } bad)
                {
                    errors.Add("trustedForwarderDomains", $"'{bad}' is not a domain name.");
                }
            }

            if (request.LayoutReviewWarmup is { } warmup && warmup is < 0 or > TenantSettings.MaxLayoutReviewWarmup)
            {
                errors.Add("layoutReviewWarmup", $"layoutReviewWarmup is 0 to {TenantSettings.MaxLayoutReviewWarmup} documents.");
            }

            if (errors.ToProblem() is { } invalid)
            {
                return invalid;
            }

            var current = await CurrentTenantAsync(db, ct);
            if (current is null)
            {
                return UnknownTenant();
            }

            // The supplier configuration of P4 changes only with sourceRouting on; a client that sends back what GET
            // gave (the portal's settings page) changes nothing and needs no switch.
            var schemaChanged = request.SupplierAttributeSchema is { ValueKind: not JsonValueKind.Null } && !SameSchema(schema, current.Settings.SupplierAttributeSchema);
            var layoutsChanged = request.LayoutsRequiredByDefault is { } required && required != current.Settings.LayoutsRequiredByDefault;
            var forwardersChanged = forwarders is not null && !forwarders.SequenceEqual(current.Settings.TrustedForwarderDomains ?? [], StringComparer.Ordinal);
            var routingOn = request.Features?.SourceRouting ?? current.Settings.Features?.SourceRouting ?? false;
            if ((schemaChanged || layoutsChanged || forwardersChanged) && !routingOn)
            {
                return SupplierConfigSupport.FeatureOff();
            }

            // The layout warm-up of decision 2 (docs/11 §24): changing it needs supplierLayouts; sending back what GET gave does not.
            var warmupChanged = request.LayoutReviewWarmup is { } newWarmup && newWarmup != current.Settings.EffectiveLayoutReviewWarmup();
            if (warmupChanged && !(request.Features?.SupplierLayouts ?? current.Settings.Features?.SupplierLayouts ?? false))
            {
                return LayoutEndpoints.FeatureOff();
            }

            if (warmupChanged)
            {
                current.Settings.LayoutReviewWarmup = request.LayoutReviewWarmup;
            }

            if (schemaChanged)
            {
                current.Settings.SupplierAttributeSchema = schema;
            }

            if (layoutsChanged)
            {
                current.Settings.LayoutsRequiredByDefault = request.LayoutsRequiredByDefault!.Value;
            }

            if (forwardersChanged)
            {
                current.Settings.TrustedForwarderDomains = forwarders;
            }

            current.Settings.AutoApproveConfidenceThreshold = request.AutoApproveConfidenceThreshold;
            current.Settings.AlwaysReviewSupplierIds = supplierIds;
            current.Settings.RetentionYears = request.RetentionYears;
            current.Settings.ShareExtractorProfiles = request.ShareExtractorProfiles;
            if (request.Features is { } features)
            {
                // Set in place: EF updates the owned JSON object only where it exists (TenantSettings.Features); a row
                // stored without it (before P0's back-fill) gets a new one.
                var flags = current.Settings.Features ??= new TenantFeatureFlags();
                flags.ConfigurableTypes = features.ConfigurableTypes;
                flags.Outputs = features.Outputs;
                flags.SourceRouting = features.SourceRouting;
                flags.SupplierLayouts = features.SupplierLayouts;
                flags.CustomTypes = features.CustomTypes;
                flags.StructuredSources = features.StructuredSources;
            }

            current.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(new TenantSettingsResponse(TenantIdentity.From(current), TenantSettingsDto.From(current.Settings)));
        })
        .RequireAuthorization(Policies.TenantAdmin)
        .WithName("PutTenantSettings")
        .WithSummary("Replaces the review policy and retention of the current tenant, and its feature switches when features is given.");

        return group;
    }
}
