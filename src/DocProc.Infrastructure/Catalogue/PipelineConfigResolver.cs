using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Configuration;
using DocProc.Application.Suppliers;
using DocProc.Definitions.Pipelines;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocProc.Infrastructure.Catalogue;

/// <summary>
/// <see cref="IPipelineConfigResolver"/> over the database (docs/11 §10.1). Without <c>configurableSteps</c> the
/// pipeline is the built-in one (<see cref="PipelineConfig.Builtin"/>) and nothing is read. With it, the layers are the
/// supplier's group's <c>settings.pipelineOverrides</c>, the supplier's, and the identified layout version's mapping
/// <c>pipelineOverrides</c>, applied in that order over the type's own steps (<see cref="PipelineResolver"/>). A refused
/// resolution falls back to the type's own steps and carries the problems. Every pipeline that ran is stored once by its
/// hash (<see cref="PipelineSnapshot"/>), in a context of its own so a stage's unit of work is not touched.
/// </summary>
public sealed class PipelineConfigResolver(
    DocProcDbContext db,
    ITenantFeatures features,
    ISystemDbContextFactory contexts,
    TimeProvider clock,
    ILogger<PipelineConfigResolver> logger) : IPipelineConfigResolver
{
    // The snapshots this process has stored (database, tenant and hash): stored once, read-only after.
    private static readonly ConcurrentDictionary<(string, Guid, string), bool> Stored = new();

    public async Task<PipelineConfig> ResolveAsync(PipelineRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await features.IsEnabledAsync(request.TenantId, TenantFeature.ConfigurableSteps, cancellationToken).ConfigureAwait(false))
        {
            return PipelineConfig.Builtin(request.Type);
        }

        var layers = await LayersAsync(request, cancellationToken).ConfigureAwait(false);
        var resolution = PipelineResolver.Resolve(request.Type?.Definition, layers);
        var config = resolution.Pipeline is { } pipeline
            ? new PipelineConfig(pipeline, [], true)
            : new PipelineConfig(PipelineResolver.Baseline(request.Type?.Definition), resolution.Problems, true);
        if (config.Refused && logger.IsEnabled(LogLevel.Warning))
        {
            logger.LogWarning("Pipeline overrides for tenant {TenantId}, supplier {SupplierId} refused: {Problems}", request.TenantId, request.SupplierId, string.Join("; ", config.Problems.Select(p => $"{p.Path}: {p.Message}")));
        }

        await StoreAsync(request.TenantId, config.Pipeline, cancellationToken).ConfigureAwait(false);
        return config;
    }

    /// <summary>The layers of the request, in the order they apply: the group's, the supplier's, the layout version's.</summary>
    public async Task<IReadOnlyList<PipelineLayer>> LayersAsync(PipelineRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var layers = new List<PipelineLayer>();
        if (request.SupplierId is { } supplierId)
        {
            var supplier = await db.Suppliers.AsNoTracking().Where(s => s.Id == supplierId)
                .Select(s => new { s.SettingsJson, s.GroupId, GroupSettings = s.Group != null ? s.Group.SettingsJson : null })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (supplier is not null)
            {
                if (supplier.GroupId is { } groupId && SupplierSettings.Parse(supplier.GroupSettings).PipelineOverrides is { Count: > 0 } group)
                {
                    layers.Add(new PipelineLayer(PipelineSources.Group, groupId.ToString("D"), group));
                }

                if (SupplierSettings.Parse(supplier.SettingsJson).PipelineOverrides is { Count: > 0 } own)
                {
                    layers.Add(new PipelineLayer(PipelineSources.Supplier, supplierId.ToString("D"), own));
                }
            }
        }

        if (request.LayoutVersionId is { } versionId)
        {
            var version = await db.SupplierLayoutVersions.AsNoTracking().Where(v => v.Id == versionId)
                .Select(v => new { v.MappingJson, v.Version, v.Layout!.LayoutKey })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (version is not null && LayoutOverrides(version.MappingJson) is { Count: > 0 } layout)
            {
                layers.Add(new PipelineLayer(PipelineSources.Layout, $"{version.LayoutKey}@{version.Version}", layout));
            }
        }

        return layers;
    }

    /// <summary>The <c>pipelineOverrides</c> of a layout version's mapping; empty when it has none or they do not read (saves check them).</summary>
    public static IReadOnlyList<PipelineOverride> LayoutOverrides(string? mappingJson)
    {
        if (string.IsNullOrWhiteSpace(mappingJson))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(mappingJson) is JsonObject root
                ? PipelineOverridesJson.TryRead(root["pipelineOverrides"], out _) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task StoreAsync(Guid tenantId, ResolvedPipeline pipeline, CancellationToken cancellationToken)
    {
        var key = (db.Database.GetConnectionString() ?? string.Empty, tenantId, pipeline.Hash);
        if (Stored.ContainsKey(key))
        {
            return;
        }

        await using var store = contexts.CreatePlatformScoped();
        if (!await store.PipelineSnapshots.AsNoTracking().AnyAsync(p => p.TenantId == tenantId && p.Hash == pipeline.Hash, cancellationToken).ConfigureAwait(false))
        {
            store.PipelineSnapshots.Add(new PipelineSnapshot
            {
                TenantId = tenantId,
                Hash = pipeline.Hash,
                StepsJson = pipeline.StepsJson().ToJsonString(),
                CreatedAt = clock.GetUtcNow(),
            });
            try
            {
                await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // Another stage stored the same pipeline first: the row is content-addressed, so it is the same.
            }
        }

        Stored.TryAdd(key, true);
    }
}
