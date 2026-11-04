using System.Globalization;
using System.Text.Json;
using DocProc.Application.Configuration;
using DocProc.Application.Suppliers;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

/// <summary>
/// The checks shared by the supplier configuration of P4 (docs/11 §6.4, §6.5): the <c>sourceRouting</c> switch that
/// guards source rules, routing tests, supplier groups, receiving entities, supplier settings and the typed attribute
/// schema; the reading of <c>settings</c>; and the attribute checks against the tenant's schema.
/// </summary>
internal static class SupplierConfigSupport
{
    public const int MaxSettingsLength = 16 * 1024;

    /// <summary>403 unless the tenant has <c>sourceRouting</c> on; 400 without a tenant.</summary>
    public static async Task<ProblemHttpResult?> GuardAsync(ITenantContext tenant, ITenantFeatures features, string what, CancellationToken ct)
    {
        if (RequireTenant(tenant, what) is { } problem)
        {
            return problem;
        }

        return await features.IsEnabledAsync(tenant.TenantId!.Value, TenantFeature.SourceRouting, ct) ? null : FeatureOff();
    }

    public static ProblemHttpResult FeatureOff() => TypedResults.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Feature not enabled",
        detail: "The 'sourceRouting' feature is off for this tenant; a tenant admin switches it on in the settings (features.sourceRouting). Source rules, routing tests, supplier groups, receiving entities, supplier settings and the attribute schema need it.");

    /// <summary>
    /// Reads a <c>settings</c> object strictly into <see cref="SupplierSettings"/> and checks its values; null with an
    /// error when it does not fit. An empty object is valid (every setting inherited).
    /// </summary>
    public static SupplierSettings? ReadSettings(Errors errors, JsonElement settings, string field = "settings")
    {
        if (settings.ValueKind != JsonValueKind.Object)
        {
            errors.Add(field, "settings is a JSON object.");
            return null;
        }

        var raw = settings.GetRawText();
        if (raw.Length > MaxSettingsLength)
        {
            errors.Add(field, $"settings is at most {MaxSettingsLength / 1024} KB.");
            return null;
        }

        SupplierSettings parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SupplierSettings>(raw, SupplierSettings.JsonOptions) ?? SupplierSettings.Empty;
        }
        catch (JsonException e)
        {
            errors.Add(field, $"settings does not read as supplier settings (defaultDocumentTypeKey, allowedTypeKeys, fixDocumentType, locale, review, defaults, layouts, requireSenderAuth): {e.Message}");
            return null;
        }

        var before = errors.Count;
        if (parsed.DefaultDocumentTypeKey is { } defaultType && !DocumentTypeKey.TryParse(defaultType, out _))
        {
            errors.Add($"{field}.defaultDocumentTypeKey", $"'{defaultType}' is not a type key.");
        }

        if (parsed.AllowedTypeKeys is { } allowed)
        {
            if (allowed.Count > 20)
            {
                errors.Add($"{field}.allowedTypeKeys", "allowedTypeKeys holds at most 20 type keys.");
            }
            else if (allowed.FirstOrDefault(k => !DocumentTypeKey.TryParse(k, out _)) is { } bad)
            {
                errors.Add($"{field}.allowedTypeKeys", $"'{bad}' is not a type key.");
            }
        }

        if (parsed.Locale?.Culture is { } culture && !IsCulture(culture))
        {
            errors.Add($"{field}.locale.culture", $"'{culture}' is not a culture name such as de-DE.");
        }

        if (parsed.Locale?.DateOrder is { } order && order is not ("DMY" or "MDY" or "YMD"))
        {
            errors.Add($"{field}.locale.dateOrder", "dateOrder is DMY, MDY or YMD.");
        }

        if (parsed.Review?.AutoApproveMinConfidence is { } confidence && (double.IsNaN(confidence) || confidence is < 0 or > 1))
        {
            errors.Add($"{field}.review.autoApproveMinConfidence", "autoApproveMinConfidence is between 0 and 1.");
        }

        if (parsed.Defaults?.Currency is { } currency && !IsCurrency(currency))
        {
            errors.Add($"{field}.defaults.currency", "Currency must be a three-letter upper-case ISO 4217 code.");
        }

        foreach (var problem in DocProc.Definitions.Pipelines.PipelineResolver.Check(parsed.PipelineOverrides, $"{field}.pipelineOverrides"))
        {
            errors.Add(problem.Path, problem.Message);
        }

        return errors.Count == before ? parsed : null;
    }

    /// <summary>
    /// The checks of <c>settings.pipelineOverrides</c> that need the tenant (docs/11 §10.1, P5): the
    /// <c>configurableSteps</c> switch (a client that sends back the overrides as they are stored needs none), each
    /// <c>http-lookup</c> step's destination (an http destination of the tenant) and its JSONata expressions (parsed by
    /// the renderer, not run). <paramref name="renderer"/> null refuses overrides altogether (a reviewer's
    /// <c>:create-supplier</c>, which may not set them).
    /// </summary>
    public static async Task CheckPipelineAsync(
        DocProcDbContext db,
        ITenantFeatures features,
        DocProc.Application.Outputs.IOutputRenderer? renderer,
        Guid tenantId,
        SupplierSettings? settings,
        string? storedSettingsJson,
        Errors errors,
        CancellationToken ct,
        string field = "settings")
    {
        if (settings?.PipelineOverrides is not { Count: > 0 } overrides)
        {
            return;
        }

        var at = $"{field}.pipelineOverrides";
        var stored = SupplierSettings.Parse(storedSettingsJson).PipelineOverrides;
        if (stored is not null && JsonSerializer.Serialize(stored, DocProc.Definitions.Pipelines.PipelineOverridesJson.Options) == JsonSerializer.Serialize(overrides, DocProc.Definitions.Pipelines.PipelineOverridesJson.Options))
        {
            return; // Sent back as stored: no change, no switch needed.
        }

        if (renderer is null)
        {
            errors.Add(at, "pipelineOverrides are set by a config admin in the supplier's settings, not from review.");
            return;
        }

        if (!await features.IsEnabledAsync(tenantId, TenantFeature.ConfigurableSteps, ct))
        {
            errors.Add(at, "pipelineOverrides need the 'configurableSteps' feature; a tenant admin switches it on in the settings (features.configurableSteps).");
            return;
        }

        await PipelineChecks.CheckLookupsAsync(db, renderer, overrides, at, errors, ct);
    }

    /// <summary>The settings as the API shows them (an empty object for none).</summary>
    public static JsonElement SettingsOf(string? json)
    {
        using var document = JsonDocument.Parse(SupplierSettings.Parse(json).ToJson() ?? "{}");
        return document.RootElement.Clone();
    }

    /// <summary>The tenant's attribute schema, or null when it has none (or a stored one that no longer reads).</summary>
    public static async Task<SupplierAttributeSchema?> SchemaAsync(DocProcDbContext db, CancellationToken ct)
    {
        var stored = await db.Tenants.AsNoTracking().Select(t => t.Settings.SupplierAttributeSchema).FirstOrDefaultAsync(ct);
        return SupplierAttributeSchema.TryParse(stored, out _);
    }

    /// <summary>Checks attributes against the tenant's schema (when it has one); <paramref name="checkRequired"/> for a supplier's own attributes.</summary>
    public static async Task ValidateAgainstSchemaAsync(DocProcDbContext db, Errors errors, IReadOnlyDictionary<string, JsonElement>? attributes, bool checkRequired, CancellationToken ct, string field = "attributes")
    {
        if (attributes is null)
        {
            return;
        }

        if (await SchemaAsync(db, ct) is not { } schema)
        {
            return;
        }

        foreach (var (key, message) in schema.Validate(attributes, checkRequired))
        {
            errors.Add($"{field}.{key}", message);
        }
    }

    private static bool IsCulture(string name)
    {
        try
        {
            return name.Length <= 20 && !string.IsNullOrWhiteSpace(name) && !CultureInfo.GetCultureInfo(name, predefinedOnly: true).Equals(CultureInfo.InvariantCulture);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}
