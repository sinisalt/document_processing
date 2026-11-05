using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Configuration;
using DocProc.Application.Documents;
using DocProc.Application.Suppliers;
using DocProc.Definitions.Pipelines;
using DocProc.Domain.Fields;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Layouts;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Pipeline.Stages;

/// <summary>
/// The <c>defaults</c> step of a definition-driven type (docs/11 §5.8, §10.1): after the extraction chain, each entry of
/// <c>params.values</c> writes a value into a field the document left empty (<c>mode: ifMissing</c>, the default) or
/// over what was read (<c>mode: override</c>), never over a value a reviewer corrected. A value comes from, in this
/// order of the entry's keys: <c>value</c> (a constant), <c>from</c> (the first of the named fields, or
/// <c>$source.receivedDate</c>, the day the file or mail was received in the tenant's time zone, that has a value) and
/// <c>fromSupplierSetting</c> (<c>defaults.currency</c> or <c>locale</c> of the matched supplier, its group's when it has
/// none). Written values are <c>Inferred</c> with the source method of the step that ran (<see cref="SourceMethod.Profile"/>,
/// no page), so a reviewer sees where they did not come from the page. Entries apply in their order, so a later one can
/// read what an earlier one wrote (<c>validUntil</c> from <c>validFrom</c>).
/// </summary>
internal static class ExtractDefaults
{
    /// <summary>The confidence of a default: what the type says to assume, not what the page says.</summary>
    public const double Confidence = 0.95;

    public const string ReceivedDate = "$source.receivedDate";

    /// <summary>What the step wrote, by step id (<c>appliedConfig.defaults</c>), or null when no step ran.</summary>
    public static async Task<JsonObject?> RunAsync(DocProcDbContext db, Document document, EffectiveType type, ResolvedPipeline pipeline, JsonObject tree, string tenantCurrency, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(tree);
        var steps = pipeline.Phase(PipelinePhases.Extract).Where(s => s.Enabled && s.Kind == StepKindCatalogue.Defaults).ToList();
        if (steps.Count == 0 || type.Compiled is not { } compiled)
        {
            return null;
        }

        var context = new Lazy<Task<Context>>(() => LoadAsync(db, document, cancellationToken));
        var applied = new JsonObject();
        foreach (var step in steps)
        {
            if (step.Params["values"] is not JsonObject values)
            {
                continue;
            }

            var written = new JsonArray();
            foreach (var (path, spec) in values)
            {
                if (spec is not JsonObject entry || compiled.Descriptors.Find(path) is not { IsEnvelope: true })
                {
                    continue;
                }

                var overrideExisting = entry["mode"] is JsonValue m && m.TryGetValue<string>(out var mode) && mode == "override";
                var value = await ValueOfAsync(entry, tree, context).ConfigureAwait(false);
                if (value is null)
                {
                    continue;
                }

                var currency = DefinitionTree.ValueOf(tree["currency"]) is JsonValue code && code.TryGetValue<string>(out var c) ? c : tenantCurrency;
                if (LayoutTreeWriter.Assign(tree, compiled.Descriptors, path, value, new FieldSource(SourceMethod.Profile), Confidence, overrideExisting, currency, typedRecord: false))
                {
                    written.Add(path);
                }
            }

            applied[step.Id] = new JsonObject { ["written"] = written };
        }

        return applied;
    }

    private static async Task<JsonNode?> ValueOfAsync(JsonObject entry, JsonObject tree, Lazy<Task<Context>> context)
    {
        if (entry.TryGetPropertyValue("value", out var constant) && constant is not null)
        {
            return constant.DeepClone();
        }

        if (entry["from"] is { } from)
        {
            var sources = from is JsonArray list ? list.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null) : [from is JsonValue one && one.TryGetValue<string>(out var single) ? single : null];
            foreach (var source in sources.OfType<string>())
            {
                if (source == ReceivedDate)
                {
                    if ((await context.Value.ConfigureAwait(false)).ReceivedDate is { } day)
                    {
                        return JsonValue.Create(day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                    }

                    continue;
                }

                if (Read(tree, source) is { } read)
                {
                    return read;
                }
            }
        }

        if (entry["fromSupplierSetting"] is JsonValue setting && setting.TryGetValue<string>(out var name))
        {
            var settings = (await context.Value.ConfigureAwait(false)).Settings;
            return name switch
            {
                "defaults.currency" when settings?.Defaults?.Currency is { } currency => JsonValue.Create(currency),
                "locale" when settings?.Locale?.Culture is { } culture => JsonValue.Create(culture),
                _ => null,
            };
        }

        return null;
    }

    /// <summary>The value of a top-level or group field of the tree (not a row's), as the values view gives it, or null.</summary>
    private static JsonNode? Read(JsonObject tree, string path)
    {
        JsonNode? node = tree;
        foreach (var segment in path.Split('.'))
        {
            node = node is JsonObject obj ? obj[segment] : null;
        }

        return DefinitionTree.ValueOf(node) is { } value && value.GetValueKind() != JsonValueKind.Null ? value.DeepClone() : null;
    }

    private sealed record Context(DateOnly? ReceivedDate, SupplierSettings? Settings);

    private static async Task<Context> LoadAsync(DocProcDbContext db, Document document, CancellationToken cancellationToken)
    {
        var received = await db.IngestionItems.AsNoTracking().Where(i => i.Id == document.IngestionItemId)
            .Select(i => (DateTimeOffset?)i.Ingestion!.ReceivedAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var zone = await db.Tenants.AsNoTracking().Where(t => t.Id == document.TenantId).Select(t => t.TimeZone).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        DateOnly? day = received is { } at ? DocProc.Infrastructure.Outputs.TenantClock.Today(zone ?? "UTC", at) : null;
        SupplierSettings? settings = null;
        if (document.SupplierId is { } supplierId)
        {
            var row = await db.Suppliers.AsNoTracking().Where(s => s.Id == supplierId)
                .Select(s => new { s.SettingsJson, GroupSettings = s.Group != null ? s.Group.SettingsJson : null })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            settings = row is null ? null : SupplierSettings.Parse(row.SettingsJson).Over(SupplierSettings.Parse(row.GroupSettings));
        }

        return new Context(day, settings);
    }
}
