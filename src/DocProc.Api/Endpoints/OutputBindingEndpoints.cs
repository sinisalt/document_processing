using System.Security.Claims;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Application.Configuration;
using DocProc.Application.Outputs;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

/// <summary>A binding as the API shows it: events, scope, the mapping (by key) and the destination.</summary>
public sealed record OutputBindingResponse(
    Guid Id,
    string Name,
    IReadOnlyList<string> Events,
    string? DocumentTypeKey,
    Guid? SupplierId,
    Guid? ChannelId,
    string? Condition,
    string MappingKey,
    int? PinnedMappingVersion,
    Guid DestinationId,
    string Mode,
    string? ExclusiveGroup,
    int Priority,
    string OnReapproval,
    string? UpdateMappingKey,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// <c>POST /output-bindings</c> (docs/11 §6.7, §13.5). <see cref="Events"/> defaults to <c>document.approved</c>;
/// the scope fields are optional (none: every document of the tenant); <see cref="Condition"/> is a JSONata boolean
/// over the values view. <see cref="MappingKey"/> names the tenant's mapping or a platform template;
/// <see cref="PinnedMappingVersion"/> keeps a published version instead of following the latest. Batch mode and
/// recipes come in P6.
/// </summary>
public sealed record CreateOutputBindingRequest(
    string Name,
    IReadOnlyList<string>? Events,
    string? DocumentTypeKey,
    Guid? SupplierId,
    Guid? ChannelId,
    string? Condition,
    string MappingKey,
    int? PinnedMappingVersion,
    Guid DestinationId,
    string? Mode,
    string? ExclusiveGroup,
    int? Priority,
    string? OnReapproval,
    string? UpdateMappingKey,
    bool? Enabled);

/// <summary>
/// Every field optional; absent fields are left as they are. A string set to empty clears an optional one
/// (<c>documentTypeKey</c>, <c>condition</c>, <c>exclusiveGroup</c>, <c>updateMappingKey</c>); <c>clearSupplier</c>,
/// <c>clearChannel</c> and <c>clearPinnedVersion</c> clear the others.
/// </summary>
public sealed record PatchOutputBindingRequest(
    string? Name,
    IReadOnlyList<string>? Events,
    string? DocumentTypeKey,
    Guid? SupplierId,
    bool? ClearSupplier,
    Guid? ChannelId,
    bool? ClearChannel,
    string? Condition,
    string? MappingKey,
    int? PinnedMappingVersion,
    bool? ClearPinnedVersion,
    Guid? DestinationId,
    string? ExclusiveGroup,
    int? Priority,
    string? OnReapproval,
    string? UpdateMappingKey,
    bool? Enabled);

/// <summary>
/// Output bindings (docs/11 §6.7, §13.5): which documents go where in which shape. Everything needs the
/// <c>outputs</c> switch; reading needs <see cref="Policies.Read"/>, changing <see cref="Policies.IntegrationAdmin"/>.
/// The fan-out that renders and delivers them comes with P3 part B.
/// </summary>
public static class OutputBindingEndpoints
{
    private const string What = "Output bindings";

    public const int MaxConditionLength = 4000;

    public static RouteGroupBuilder MapOutputBindings(this RouteGroupBuilder group)
    {
        var bindings = group.MapGroup("/output-bindings").WithTags("Outputs");
        bindings.MapGet("/", ListAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("ListOutputBindings")
            .WithSummary("The tenant's output bindings.");
        bindings.MapPost("/", CreateAsync)
            .RequireAuthorization(Policies.IntegrationAdmin)
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024))
            .WithName("CreateOutputBinding")
            .WithSummary("Binds events and a scope (type, supplier, channel, condition) to a mapping and a destination.");
        bindings.MapGet("/{id:guid}", GetAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("GetOutputBinding")
            .WithSummary("One output binding.");
        bindings.MapPatch("/{id:guid}", PatchAsync)
            .RequireAuthorization(Policies.IntegrationAdmin)
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024))
            .WithName("UpdateOutputBinding")
            .WithSummary("Changes a binding; absent fields stay.");
        bindings.MapDelete("/{id:guid}", DeleteAsync)
            .RequireAuthorization(Policies.IntegrationAdmin)
            .WithName("DeleteOutputBinding")
            .WithSummary("Removes a binding without deliveries (409 otherwise: disable it instead).");
        return group;
    }

    private static async Task<Results<Ok<IReadOnlyList<OutputBindingResponse>>, ProblemHttpResult>> ListAsync(ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await OutputMappingEndpoints.GuardAsync(tenant, features, ct, What) is { } problem)
        {
            return problem;
        }

        var rows = await db.OutputBindings.AsNoTracking().OrderBy(b => b.Name).ToListAsync(ct);
        var keys = await MappingKeysAsync(db, rows, ct);
        return TypedResults.Ok<IReadOnlyList<OutputBindingResponse>>([.. rows.Select(b => Response(b, keys))]);
    }

    private static async Task<Results<Ok<OutputBindingResponse>, ProblemHttpResult>> GetAsync(Guid id, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await OutputMappingEndpoints.GuardAsync(tenant, features, ct, What) is { } problem)
        {
            return problem;
        }

        var row = await db.OutputBindings.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id, ct);
        return row is null ? NotFound(id) : TypedResults.Ok(Response(row, await MappingKeysAsync(db, [row], ct)));
    }

    private static async Task<Results<Created<OutputBindingResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateOutputBindingRequest request,
        ITenantContext tenant,
        ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IOutputRenderer renderer,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await OutputMappingEndpoints.GuardAsync(tenant, features, ct, What) is { } problem)
        {
            return problem;
        }

        var now = clock.GetUtcNow();
        var binding = new OutputBinding
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.TenantId!.Value,
            Name = request.Name?.Trim() ?? string.Empty,
            Events = [.. (request.Events ?? [OutputEvents.Approved]).Distinct(StringComparer.Ordinal)],
            DocumentTypeKey = TrimToNull(request.DocumentTypeKey),
            SupplierId = request.SupplierId,
            ChannelId = request.ChannelId,
            Condition = TrimToNull(request.Condition),
            PinnedMappingVersion = request.PinnedMappingVersion,
            DestinationId = request.DestinationId,
            ExclusiveGroup = TrimToNull(request.ExclusiveGroup),
            Priority = request.Priority ?? 0,
            Enabled = request.Enabled ?? true,
            CreatedAt = now,
            CreatedBy = DocumentQueries.ActorName(user),
            UpdatedAt = now,
        };
        var errors = new Errors().Require("name", request.Name, 200);
        if (request.Mode is not null && request.Mode != nameof(OutputBindingMode.PerDocument))
        {
            errors.Add("mode", request.Mode == nameof(OutputBindingMode.Batch) ? "Batch bindings come in P6 (docs/11 §13.7); this build delivers PerDocument." : "mode is PerDocument.");
        }

        if (request.OnReapproval is not null)
        {
            if (TryParseEnum<OnReapproval>(request.OnReapproval, out var reapproval))
            {
                binding.OnReapproval = reapproval;
            }
            else
            {
                errors.Add("onReapproval", $"onReapproval is one of {Names<OnReapproval>()}.");
            }
        }

        if (await ResolveAsync(db, renderer, binding, request.MappingKey, TrimToNull(request.UpdateMappingKey), errors, ct) is { } invalid)
        {
            return invalid;
        }

        if (await db.OutputBindings.AnyAsync(b => b.Name == binding.Name, ct))
        {
            return Conflict("Binding exists", $"The tenant already has an output binding named '{binding.Name}'.");
        }

        db.OutputBindings.Add(binding);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/output-bindings/{binding.Id}", Response(binding, await MappingKeysAsync(db, [binding], ct)));
    }

    private static async Task<Results<Ok<OutputBindingResponse>, ValidationProblem, ProblemHttpResult>> PatchAsync(
        Guid id,
        PatchOutputBindingRequest request,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IOutputRenderer renderer,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await OutputMappingEndpoints.GuardAsync(tenant, features, ct, What) is { } problem)
        {
            return problem;
        }

        var binding = await db.OutputBindings.SingleOrDefaultAsync(b => b.Id == id, ct);
        if (binding is null)
        {
            return NotFound(id);
        }

        var keys = await MappingKeysAsync(db, [binding], ct);
        var errors = new Errors().Optional("name", request.Name, 200);
        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                errors.Add("name", "name is not empty.");
            }

            binding.Name = request.Name.Trim();
        }

        binding.Events = request.Events is null ? binding.Events : [.. request.Events.Distinct(StringComparer.Ordinal)];
        binding.DocumentTypeKey = request.DocumentTypeKey is null ? binding.DocumentTypeKey : TrimToNull(request.DocumentTypeKey);
        binding.SupplierId = request.ClearSupplier == true ? null : request.SupplierId ?? binding.SupplierId;
        binding.ChannelId = request.ClearChannel == true ? null : request.ChannelId ?? binding.ChannelId;
        binding.Condition = request.Condition is null ? binding.Condition : TrimToNull(request.Condition);
        binding.PinnedMappingVersion = request.ClearPinnedVersion == true ? null : request.PinnedMappingVersion ?? binding.PinnedMappingVersion;
        binding.DestinationId = request.DestinationId ?? binding.DestinationId;
        binding.ExclusiveGroup = request.ExclusiveGroup is null ? binding.ExclusiveGroup : TrimToNull(request.ExclusiveGroup);
        binding.Priority = request.Priority ?? binding.Priority;
        binding.Enabled = request.Enabled ?? binding.Enabled;
        if (request.OnReapproval is not null)
        {
            if (TryParseEnum<OnReapproval>(request.OnReapproval, out var reapproval))
            {
                binding.OnReapproval = reapproval;
            }
            else
            {
                errors.Add("onReapproval", $"onReapproval is one of {Names<OnReapproval>()}.");
            }
        }

        var mappingKey = request.MappingKey ?? keys[binding.MappingId];
        var updateKey = request.UpdateMappingKey is null ? (binding.UpdateMappingId is { } u ? keys[u] : null) : TrimToNull(request.UpdateMappingKey);
        if (await ResolveAsync(db, renderer, binding, mappingKey, updateKey, errors, ct) is { } invalid)
        {
            return invalid;
        }

        if (await db.OutputBindings.AnyAsync(b => b.Id != id && b.Name == binding.Name, ct))
        {
            return Conflict("Binding exists", $"The tenant already has an output binding named '{binding.Name}'.");
        }

        binding.UpdatedAt = clock.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict("Binding changed", "The binding changed while it was saved; read it again.");
        }

        return TypedResults.Ok(Response(binding, await MappingKeysAsync(db, [binding], ct)));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await OutputMappingEndpoints.GuardAsync(tenant, features, ct, What) is { } problem)
        {
            return problem;
        }

        var binding = await db.OutputBindings.SingleOrDefaultAsync(b => b.Id == id, ct);
        if (binding is null)
        {
            return NotFound(id);
        }

        if (await db.OutboxDeliveries.AnyAsync(d => d.BindingId == id, ct))
        {
            return Conflict("Binding in use", "Deliveries refer to this binding; disable it (enabled: false) to keep its delivery history.");
        }

        db.OutputBindings.Remove(binding);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// The checks every write runs on the binding as it would be stored: events, type key, the supplier, channel and
    /// destination of the tenant, the mapping (the tenant's or a platform template) and its pinned version, the update
    /// mapping, and the condition's syntax.
    /// </summary>
    private static async Task<ValidationProblem?> ResolveAsync(DocProcDbContext db, IOutputRenderer renderer, OutputBinding binding, string? mappingKey, string? updateMappingKey, Errors errors, CancellationToken ct)
    {
        if (binding.Events.Count == 0 || binding.Events.Any(e => !OutputEvents.All.Contains(e, StringComparer.Ordinal)))
        {
            errors.Add("events", $"events is a non-empty list of {string.Join(", ", OutputEvents.All)}.");
        }
        else if (binding.Events.Contains(OutputEvents.NeedsReview, StringComparer.Ordinal))
        {
            errors.Add("events", $"{OutputEvents.NeedsReview} comes in a later phase; this build fans out on {string.Join(" and ", OutputEvents.Fired)}.");
        }
        else if (binding.Events.Contains(OutputEvents.Updated, StringComparer.Ordinal))
        {
            errors.Add("events", $"{OutputEvents.Updated} is not an event of its own: a re-approval of a binding with onReapproval Update renders with meta.event {OutputEvents.Updated}; list {OutputEvents.Approved}.");
        }

        if (binding.DocumentTypeKey is not null && !DocumentTypeKey.TryParse(binding.DocumentTypeKey, out _))
        {
            errors.Add("documentTypeKey", "documentTypeKey is a document type key.");
        }

        if (binding.ExclusiveGroup is { Length: > 64 })
        {
            errors.Add("exclusiveGroup", "exclusiveGroup is at most 64 characters.");
        }

        if (binding.SupplierId is { } supplier && !await db.Suppliers.AnyAsync(s => s.Id == supplier, ct))
        {
            errors.Add("supplierId", "The tenant has no such supplier.");
        }

        if (binding.ChannelId is { } channel && !await db.IngestionChannels.AnyAsync(c => c.Id == channel, ct))
        {
            errors.Add("channelId", "The tenant has no such channel.");
        }

        if (!await db.OutputDestinations.AnyAsync(d => d.Id == binding.DestinationId, ct))
        {
            errors.Add("destinationId", "The tenant has no such destination.");
        }

        var mapping = mappingKey is null ? null : await OutputMappingEndpoints.FindAsync(db, mappingKey, tracked: false, ct);
        if (mapping is null)
        {
            errors.Add("mappingKey", $"There is no output mapping '{mappingKey}'.");
        }
        else
        {
            binding.MappingId = mapping.Id;
            if (binding.PinnedMappingVersion is { } pinned
                && !await db.OutputMappingVersions.AnyAsync(v => v.MappingId == mapping.Id && v.Version == pinned && v.State != OutputMappingVersionState.Draft, ct))
            {
                errors.Add("pinnedMappingVersion", $"Mapping '{mapping.Key}' has no published version {pinned}.");
            }
        }

        if (updateMappingKey is null)
        {
            binding.UpdateMappingId = null;
            if (binding.OnReapproval == OnReapproval.Update)
            {
                errors.Add("updateMappingKey", "onReapproval Update needs updateMappingKey, the mapping the update is rendered with.");
            }
        }
        else if (await OutputMappingEndpoints.FindAsync(db, updateMappingKey, tracked: false, ct) is { } update)
        {
            binding.UpdateMappingId = update.Id;
        }
        else
        {
            errors.Add("updateMappingKey", $"There is no output mapping '{updateMappingKey}'.");
        }

        if (binding.Condition is { } condition)
        {
            if (condition.Length > MaxConditionLength)
            {
                errors.Add("condition", $"condition is at most {MaxConditionLength} characters.");
            }
            else if ((await renderer.CheckAsync(condition, null, ct)).Error is { } error)
            {
                errors.Add("condition", $"condition is not a JSONata expression: {error.Message}");
            }
        }

        return errors.ToProblem();
    }

    private static async Task<Dictionary<Guid, string>> MappingKeysAsync(DocProcDbContext db, IReadOnlyList<OutputBinding> bindings, CancellationToken ct)
    {
        var ids = bindings.SelectMany(b => new[] { b.MappingId, b.UpdateMappingId ?? Guid.Empty }).Where(g => g != Guid.Empty).Distinct().ToList();
        return await db.OutputMappings.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Key, ct);
    }

    private static OutputBindingResponse Response(OutputBinding b, Dictionary<Guid, string> keys) => new(
        b.Id,
        b.Name,
        b.Events,
        b.DocumentTypeKey,
        b.SupplierId,
        b.ChannelId,
        b.Condition,
        keys.TryGetValue(b.MappingId, out var key) ? key : string.Empty,
        b.PinnedMappingVersion,
        b.DestinationId,
        b.Mode.ToString(),
        b.ExclusiveGroup,
        b.Priority,
        b.OnReapproval.ToString(),
        b.UpdateMappingId is { } u && keys.TryGetValue(u, out var updateKey) ? updateKey : null,
        b.Enabled,
        b.CreatedAt,
        b.UpdatedAt);

    private static ProblemHttpResult NotFound(Guid id) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Output binding not found", detail: $"The tenant has no output binding {id}.");
}
