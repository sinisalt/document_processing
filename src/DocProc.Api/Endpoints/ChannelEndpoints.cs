using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

/// <summary><see cref="HasSecret"/> is all a client learns about the credential; the reference itself stays server-side.</summary>
public sealed record ChannelResponse(
    Guid Id,
    string Name,
    string Type,
    string Status,
    JsonElement Config,
    bool HasSecret,
    DateTimeOffset? LastPolledAt,
    string? LastError,
    DateTimeOffset CreatedAt)
{
    internal static ChannelResponse From(IngestionChannel c) =>
        new(c.Id, c.Name, c.Type.ToString(), c.Status.ToString(), ParseConfig(c.ConfigJson), !string.IsNullOrEmpty(c.SecretRef), c.LastPolledAt, c.LastError, c.CreatedAt);

    private static JsonElement ParseConfig(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return document.RootElement.Clone();
    }
}

/// <summary><see cref="Config"/> is the type-specific settings object (mailbox address, folder path); <see cref="SecretRef"/> names the credential in the secret store.</summary>
public sealed record CreateChannelRequest(string Name, string Type, JsonElement? Config, string? SecretRef);

public sealed record PatchChannelRequest(string? Name, string? Status, JsonElement? Config);

/// <summary>Ingestion channels of the current tenant (docs/02 §1 IngestionChannel; docs/05: TenantAdmin manages channels).</summary>
public static class ChannelEndpoints
{
    public static RouteGroupBuilder MapChannels(this RouteGroupBuilder group)
    {
        var channels = group.MapGroup("/channels").WithTags("Channels");

        channels.MapGet("/", async Task<Results<Ok<IReadOnlyList<ChannelResponse>>, ProblemHttpResult>> ([FromServices] DocProcDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Channels") is { } problem)
            {
                return problem;
            }

            var rows = await db.IngestionChannels.AsNoTracking().ToListAsync(ct);
            return TypedResults.Ok<IReadOnlyList<ChannelResponse>>(rows.OrderBy(c => c.CreatedAt).ThenBy(c => c.Name, StringComparer.Ordinal).Select(ChannelResponse.From).ToList());
        })
        .RequireAuthorization(Policies.Read)
        .WithName("ListChannels")
        .WithSummary("Ingestion channels of the current tenant; credentials are never returned.");

        channels.MapPost("/", async Task<Results<Created<ChannelResponse>, ValidationProblem, ProblemHttpResult>> (CreateChannelRequest request, [FromServices] DocProcDbContext db, ITenantContext tenant, TimeProvider clock, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Channels") is { } problem)
            {
                return problem;
            }

            var errors = new Errors().Require("name", request.Name, 100).Optional("secretRef", request.SecretRef, 200);
            if (!TryParseEnum<ChannelType>(request.Type, out var type))
            {
                errors.Add("type", $"Unknown channel type. Allowed: {Names<ChannelType>()}.");
            }

            if (!IsObjectOrAbsent(request.Config))
            {
                errors.Add("config", "config must be a JSON object.");
            }

            if (errors.ToProblem() is { } invalid)
            {
                return invalid;
            }

            if (await CurrentTenantAsync(db, ct) is null)
            {
                return UnknownTenant();
            }

            var channel = new IngestionChannel
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.TenantId!.Value,
                Name = request.Name.Trim(),
                Type = type,
                ConfigJson = request.Config is { ValueKind: JsonValueKind.Object } config ? config.GetRawText() : "{}",
                SecretRef = TrimToNull(request.SecretRef),
                Status = ChannelStatus.Active,
                CreatedAt = clock.GetUtcNow(),
            };
            db.IngestionChannels.Add(channel);
            await db.SaveChangesAsync(ct);
            return TypedResults.Created($"/api/v1/channels/{channel.Id}", ChannelResponse.From(channel));
        })
        .RequireAuthorization(Policies.TenantAdmin)
        .WithName("CreateChannel")
        .WithSummary("Adds a mailbox, watched-folder, API-upload or portal-upload channel to the current tenant.");

        channels.MapPatch("/{id:guid}", async Task<Results<Ok<ChannelResponse>, NotFound, ValidationProblem, ProblemHttpResult>> (Guid id, PatchChannelRequest request, [FromServices] DocProcDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Channels") is { } problem)
            {
                return problem;
            }

            var errors = new Errors().Optional("name", request.Name, 100);
            if (request.Name is not null && string.IsNullOrWhiteSpace(request.Name))
            {
                errors.Add("name", "name cannot be blank.");
            }

            ChannelStatus? status = null;
            if (request.Status is not null)
            {
                if (!TryParseEnum<ChannelStatus>(request.Status, out var parsed))
                {
                    errors.Add("status", $"Unknown status. Allowed: {Names<ChannelStatus>()}.");
                }

                status = parsed;
            }

            if (!IsObjectOrAbsent(request.Config))
            {
                errors.Add("config", "config must be a JSON object.");
            }

            if (errors.ToProblem() is { } invalid)
            {
                return invalid;
            }

            var channel = await db.IngestionChannels.SingleOrDefaultAsync(c => c.Id == id, ct);
            if (channel is null)
            {
                return TypedResults.NotFound();
            }

            if (request.Name is not null)
            {
                channel.Name = request.Name.Trim();
            }

            if (status is { } newStatus)
            {
                channel.Status = newStatus;
                if (newStatus != ChannelStatus.Error)
                {
                    channel.LastError = null;
                }
            }

            if (request.Config is { ValueKind: JsonValueKind.Object } config)
            {
                channel.ConfigJson = config.GetRawText();
            }

            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(ChannelResponse.From(channel));
        })
        .RequireAuthorization(Policies.TenantAdmin)
        .WithName("UpdateChannel")
        .WithSummary("Renames, pauses/resumes or reconfigures a channel.");

        channels.MapDelete("/{id:guid}", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (Guid id, [FromServices] DocProcDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Channels") is { } problem)
            {
                return problem;
            }

            var channel = await db.IngestionChannels.SingleOrDefaultAsync(c => c.Id == id, ct);
            if (channel is null)
            {
                return TypedResults.NotFound();
            }

            if (await db.Ingestions.AnyAsync(i => i.ChannelId == id, ct))
            {
                return Conflict("Channel in use", "Ingestions reference this channel; pause it instead of deleting it.");
            }

            db.IngestionChannels.Remove(channel);
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        })
        .RequireAuthorization(Policies.TenantAdmin)
        .WithName("DeleteChannel")
        .WithSummary("Deletes a channel that has never received anything; otherwise 409.");

        return group;
    }

    private static bool IsObjectOrAbsent(JsonElement? config) =>
        config is null || config.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null;
}
