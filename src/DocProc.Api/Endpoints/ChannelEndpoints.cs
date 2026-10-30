using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Application.Abstractions;
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
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    internal static ChannelResponse From(IngestionChannel c, ILogger logger) =>
        new(c.Id, c.Name, c.Type.ToString(), c.Status.ToString(), ParseConfig(c, logger), !string.IsNullOrEmpty(c.SecretRef), c.LastPolledAt, c.LastError, c.CreatedAt);

    /// <summary>
    /// The stored config as a JSON element. A row whose ConfigJson is not valid JSON (written outside this API) must
    /// not take the whole list down with a 400, so it reads as an empty object and the channel is named in the log.
    /// </summary>
    private static JsonElement ParseConfig(IngestionChannel channel, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(channel.ConfigJson))
        {
            return EmptyObject;
        }

        try
        {
            using var document = JsonDocument.Parse(channel.ConfigJson);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Channel {ChannelId} has a malformed stored config; returning an empty object.", channel.Id);
            return EmptyObject;
        }
    }
}

/// <summary>
/// <see cref="Config"/> is the type-specific settings object (mailbox address, folder path), at most 16 KB.
/// <see cref="Secret"/> is write-only: the mailbox password or token goes to the secret store under a reference the
/// server generates; the response only ever says whether one exists.
/// </summary>
public sealed record CreateChannelRequest(string Name, string Type, JsonElement? Config, string? Secret);

/// <summary>Every field optional; absent fields are left as they are. <see cref="Secret"/> set to a value rotates the credential, set to an empty string removes it.</summary>
public sealed record PatchChannelRequest(string? Name, string? Status, JsonElement? Config, string? Secret);

/// <summary>
/// Ingestion channels of the current tenant (docs/02 §1 IngestionChannel; docs/05: TenantAdmin manages channels).
/// A channel's credential lives in <see cref="ISecretStore"/> under <c>tenant/{tenantId}/channel/{channelId}</c>, a
/// reference the client never chooses: a client-supplied reference could name another tenant's or the platform's
/// LLM key and have the mail poller hand it to the client's own IMAP host.
/// </summary>
public static class ChannelEndpoints
{
    /// <summary>Upper bound of the serialised <c>config</c> object (docs/05).</summary>
    public const int MaxConfigLength = 16 * 1024;

    public const int MaxSecretLength = 4096;

    public static RouteGroupBuilder MapChannels(this RouteGroupBuilder group)
    {
        var channels = group.MapGroup("/channels").WithTags("Channels");

        channels.MapGet("/", async Task<Results<Ok<IReadOnlyList<ChannelResponse>>, ProblemHttpResult>> ([FromServices] DocProcDbContext db, ITenantContext tenant, ILogger<ChannelResponse> logger, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Channels") is { } problem)
            {
                return problem;
            }

            var rows = await db.IngestionChannels.AsNoTracking().ToListAsync(ct);
            return TypedResults.Ok<IReadOnlyList<ChannelResponse>>(rows.OrderBy(c => c.CreatedAt).ThenBy(c => c.Name, StringComparer.Ordinal).Select(c => ChannelResponse.From(c, logger)).ToList());
        })
        .RequireAuthorization(Policies.Read)
        .WithName("ListChannels")
        .WithSummary("Ingestion channels of the current tenant; credentials are never returned.");

        channels.MapPost("/", async Task<Results<Created<ChannelResponse>, ValidationProblem, ProblemHttpResult>> (CreateChannelRequest request, [FromServices] DocProcDbContext db, ITenantContext tenant, ISecretStore secrets, ILogger<ChannelResponse> logger, TimeProvider clock, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Channels") is { } problem)
            {
                return problem;
            }

            var errors = new Errors().Require("name", request.Name, 100);
            if (!TryParseEnum<ChannelType>(request.Type, out var type))
            {
                errors.Add("type", $"Unknown channel type. Allowed: {Names<ChannelType>()}.");
            }

            ValidateConfig(errors, request.Config);
            ValidateSecret(errors, request.Secret);
            if (errors.ToProblem() is { } invalid)
            {
                return invalid;
            }

            if (await CurrentTenantAsync(db, ct) is null)
            {
                return UnknownTenant();
            }

            var tenantId = tenant.TenantId!.Value;
            var channel = new IngestionChannel
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Name = request.Name.Trim(),
                Type = type,
                ConfigJson = request.Config is { ValueKind: JsonValueKind.Object } config ? config.GetRawText() : "{}",
                Status = ChannelStatus.Active,
                CreatedAt = clock.GetUtcNow(),
            };

            // The secret first, the row second (as /llm-providers does): a failed save leaves an unreferenced secret,
            // never a channel whose reference points at nothing.
            if (!string.IsNullOrEmpty(request.Secret))
            {
                channel.SecretRef = SecretReference(tenantId, channel.Id);
                await secrets.SetAsync(channel.SecretRef, request.Secret, ct);
            }

            db.IngestionChannels.Add(channel);
            await db.SaveChangesAsync(ct);
            return TypedResults.Created($"/api/v1/channels/{channel.Id}", ChannelResponse.From(channel, logger));
        })
        .RequireAuthorization(Policies.TenantAdmin)
        .WithName("CreateChannel")
        .WithSummary("Adds a mailbox, watched-folder, API-upload or portal-upload channel to the current tenant; the credential is write-only.");

        channels.MapPatch("/{id:guid}", async Task<Results<Ok<ChannelResponse>, NotFound, ValidationProblem, ProblemHttpResult>> (Guid id, PatchChannelRequest request, [FromServices] DocProcDbContext db, ITenantContext tenant, ISecretStore secrets, ILogger<ChannelResponse> logger, CancellationToken ct) =>
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

            ValidateConfig(errors, request.Config);
            ValidateSecret(errors, request.Secret);
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

            // Rotate: write the new value (under the existing reference, or a fresh one) before the row. Clear: the
            // row first, the secret second, so a failed save never leaves a reference to a deleted secret.
            string? clearedReference = null;
            if (request.Secret is { Length: > 0 } secret)
            {
                channel.SecretRef ??= SecretReference(channel.TenantId, channel.Id);
                await secrets.SetAsync(channel.SecretRef, secret, ct);
            }
            else if (request.Secret is not null)
            {
                clearedReference = channel.SecretRef;
                channel.SecretRef = null;
            }

            await db.SaveChangesAsync(ct);
            if (clearedReference is not null)
            {
                await secrets.DeleteAsync(clearedReference, ct);
            }

            return TypedResults.Ok(ChannelResponse.From(channel, logger));
        })
        .RequireAuthorization(Policies.TenantAdmin)
        .WithName("UpdateChannel")
        .WithSummary("Renames, pauses/resumes or reconfigures a channel; secret rotates (value) or clears (empty string) the credential.");

        channels.MapDelete("/{id:guid}", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (Guid id, [FromServices] DocProcDbContext db, ITenantContext tenant, ISecretStore secrets, CancellationToken ct) =>
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
            if (channel.SecretRef is { } reference)
            {
                await secrets.DeleteAsync(reference, ct);
            }

            return TypedResults.NoContent();
        })
        .RequireAuthorization(Policies.TenantAdmin)
        .WithName("DeleteChannel")
        .WithSummary("Deletes a channel that has never received anything, and its credential; otherwise 409.");

        return group;
    }

    /// <summary>The server-generated secret reference of a channel; never taken from a request.</summary>
    internal static string SecretReference(Guid tenantId, Guid channelId) => $"tenant/{tenantId}/channel/{channelId}";

    private static void ValidateConfig(Errors errors, JsonElement? config)
    {
        if (config is null || config.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return;
        }

        if (config.Value.ValueKind != JsonValueKind.Object)
        {
            errors.Add("config", "config must be a JSON object.");
        }
        else if (config.Value.GetRawText().Length > MaxConfigLength)
        {
            errors.Add("config", $"config is at most {MaxConfigLength / 1024} KB.");
        }
    }

    private static void ValidateSecret(Errors errors, string? secret)
    {
        if (secret is not null && secret.Length > MaxSecretLength)
        {
            errors.Add("secret", $"secret is at most {MaxSecretLength} characters.");
        }
    }
}
