using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Mail;
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

    /// <summary>The supplier of a document no source rule claims (docs/11 §7.2 step 4).</summary>
    public Guid? DefaultSupplierId { get; init; }

    /// <summary>The type of a document no source rule pins (<c>TypeSource=Supplier</c>).</summary>
    public string? DefaultDocumentTypeKey { get; init; }

    /// <summary>The receiving entity of a document no source rule names.</summary>
    public Guid? DefaultReceivingEntityId { get; init; }

    /// <summary>What a polling channel reads, normalised (<c>imap:platform/invoices@tenant-a.local/inbox</c>); null for upload channels and invalid configs.</summary>
    public string? SourceKey { get; init; }

    internal static ChannelResponse From(IngestionChannel c, ILogger logger) =>
        new(c.Id, c.Name, c.Type.ToString(), c.Status.ToString(), ParseConfig(c, logger), !string.IsNullOrEmpty(c.SecretRef), c.LastPolledAt, c.LastError, c.CreatedAt)
        {
            DefaultSupplierId = c.DefaultSupplierId,
            DefaultDocumentTypeKey = c.DefaultDocumentTypeKey,
            DefaultReceivingEntityId = c.DefaultReceivingEntityId,
            SourceKey = c.SourceKey,
        };

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
public sealed record CreateChannelRequest(string Name, string Type, JsonElement? Config, string? Secret)
{
    /// <summary>The channel's default supplier (docs/11 §7.2; needs <c>sourceRouting</c>).</summary>
    public Guid? DefaultSupplierId { get; init; }

    /// <summary>The channel's default type (needs <c>sourceRouting</c>).</summary>
    public string? DefaultDocumentTypeKey { get; init; }

    /// <summary>The channel's default receiving entity (needs <c>sourceRouting</c>).</summary>
    public Guid? DefaultReceivingEntityId { get; init; }
}

/// <summary>
/// Every field optional; absent fields are left as they are. <see cref="Secret"/> set to a value rotates the credential,
/// set to an empty string removes it. The defaults (needs <c>sourceRouting</c>) are cleared with the empty id or an empty
/// type key.
/// </summary>
public sealed record PatchChannelRequest(string? Name, string? Status, JsonElement? Config, string? Secret)
{
    public Guid? DefaultSupplierId { get; init; }

    public string? DefaultDocumentTypeKey { get; init; }

    public Guid? DefaultReceivingEntityId { get; init; }
}

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

        channels.MapPost("/", async Task<Results<Created<ChannelResponse>, ValidationProblem, ProblemHttpResult>> (CreateChannelRequest request, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, ITenantContext tenant, ISecretStore secrets, Microsoft.Extensions.Options.IOptions<MailOptions> mail, ILogger<ChannelResponse> logger, TimeProvider clock, CancellationToken ct) =>
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

            var current = await CurrentTenantAsync(db, ct);
            if (current is null)
            {
                return UnknownTenant();
            }

            var routing = await features.IsEnabledAsync(tenant.TenantId!.Value, TenantFeature.SourceRouting, ct);
            if (!routing && (request.DefaultSupplierId is not null || request.DefaultDocumentTypeKey is not null || request.DefaultReceivingEntityId is not null))
            {
                return SupplierConfigSupport.FeatureOff();
            }

            var configJson = request.Config is { ValueKind: JsonValueKind.Object } given ? given.GetRawText() : "{}";

            // The source key is computed whatever the switch. With sourceRouting on, the config is checked and a source
            // another channel reads is a 409. Without it, the channel is saved as before P4 (a free config, no 409):
            // the key is kept when it is free and left empty when it is not, and the poller claims an empty key under
            // the same unique index before the channel is ever read (ChannelPollerHostedService), so two channels never
            // poll one mailbox or folder.
            var sourceKey = ConnectorConfig(routing ? errors : new Errors(), type, configJson, current.Slug, mail.Value);
            if (routing)
            {
                await ValidateDefaultsAsync(db, errors, request.DefaultSupplierId, request.DefaultDocumentTypeKey, request.DefaultReceivingEntityId, ct);
                if (errors.ToProblem() is { } connectorInvalid)
                {
                    return connectorInvalid;
                }
            }

            var tenantId = tenant.TenantId!.Value;
            var channel = new IngestionChannel
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Name = request.Name.Trim(),
                Type = type,
                ConfigJson = configJson,
                Status = ChannelStatus.Active,
                CreatedAt = clock.GetUtcNow(),
                SourceKey = sourceKey,
                DefaultSupplierId = request.DefaultSupplierId,
                DefaultDocumentTypeKey = request.DefaultDocumentTypeKey is null ? null : DocumentTypeKey.Parse(request.DefaultDocumentTypeKey).Value,
                DefaultReceivingEntityId = request.DefaultReceivingEntityId,
            };

            // The secret first, the row second (as /llm-providers does): a failed save leaves an unreferenced secret,
            // never a channel whose reference points at nothing.
            if (!string.IsNullOrEmpty(request.Secret))
            {
                channel.SecretRef = SecretReference(tenantId, channel.Id);
                await secrets.SetAsync(channel.SecretRef, request.Secret, ct);
            }

            db.IngestionChannels.Add(channel);
            if (await SaveSourceAsync(db, channel, strict: routing, ct) is { } taken)
            {
                return taken;
            }

            return TypedResults.Created($"/api/v1/channels/{channel.Id}", ChannelResponse.From(channel, logger));
        })
        .RequireAuthorization(Policies.TenantAdmin)
        .WithName("CreateChannel")
        .WithSummary("Adds a mailbox, watched-folder, API-upload or portal-upload channel to the current tenant; the credential is write-only.");

        channels.MapPatch("/{id:guid}", async Task<Results<Ok<ChannelResponse>, NotFound, ValidationProblem, ProblemHttpResult>> (Guid id, PatchChannelRequest request, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, ITenantContext tenant, ISecretStore secrets, Microsoft.Extensions.Options.IOptions<MailOptions> mail, ILogger<ChannelResponse> logger, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Channels") is { } problem)
            {
                return problem;
            }

            var errors = new Errors().Optional("name", request.Name, 100);
            if (request.Name is not null && string.IsNullOrWhiteSpace(request.Name))
            {
                errors.Add("name", "name cannot be blank.", ValidationCodes.NotBlank);
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

            var routing = await features.IsEnabledAsync(channel.TenantId, TenantFeature.SourceRouting, ct);
            if (!routing && (request.DefaultSupplierId is not null || request.DefaultDocumentTypeKey is not null || request.DefaultReceivingEntityId is not null))
            {
                return SupplierConfigSupport.FeatureOff();
            }

            // The key follows the config on every save, whatever the switch (see the create); with sourceRouting on, a
            // config sent is checked, and the stored one (perhaps written before the switch) only gives the key.
            var slug = await db.Tenants.AsNoTracking().Where(t => t.Id == channel.TenantId).Select(t => t.Slug).SingleAsync(ct);
            channel.SourceKey = request.Config is { ValueKind: JsonValueKind.Object } c
                ? ConnectorConfig(routing ? errors : new Errors(), channel.Type, c.GetRawText(), slug, mail.Value)
                : ConnectorConfig(new Errors(), channel.Type, channel.ConfigJson, slug, mail.Value);
            if (routing)
            {
                await ValidateDefaultsAsync(
                    db,
                    errors,
                    request.DefaultSupplierId is { } s && s != Guid.Empty ? s : null,
                    string.IsNullOrEmpty(request.DefaultDocumentTypeKey) ? null : request.DefaultDocumentTypeKey,
                    request.DefaultReceivingEntityId is { } e && e != Guid.Empty ? e : null,
                    ct);
                if (errors.ToProblem() is { } connectorInvalid)
                {
                    return connectorInvalid;
                }

                if (request.DefaultSupplierId is { } supplierId)
                {
                    channel.DefaultSupplierId = supplierId == Guid.Empty ? null : supplierId;
                }

                if (request.DefaultDocumentTypeKey is { } typeKey)
                {
                    channel.DefaultDocumentTypeKey = typeKey.Length == 0 ? null : DocumentTypeKey.Parse(typeKey).Value;
                }

                if (request.DefaultReceivingEntityId is { } entityId)
                {
                    channel.DefaultReceivingEntityId = entityId == Guid.Empty ? null : entityId;
                }
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

            // Rotate: write the new value under the channel's own reference before the row. Clear: the row first, the
            // secret second, so a failed save never leaves a reference to a deleted secret. The reference is always
            // computed, never read from the row (see OwnsSecret).
            var reference = SecretReference(channel.TenantId, channel.Id);
            string? clearedReference = null;
            if (request.Secret is { Length: > 0 } secret)
            {
                _ = OwnsSecret(channel, reference, logger);
                channel.SecretRef = reference;
                await secrets.SetAsync(reference, secret, ct);
            }
            else if (request.Secret is not null)
            {
                clearedReference = OwnsSecret(channel, reference, logger) ? reference : null;
                channel.SecretRef = null;
            }

            if (await SaveSourceAsync(db, channel, strict: routing, ct) is { } taken)
            {
                return taken;
            }

            if (clearedReference is not null)
            {
                await secrets.DeleteAsync(clearedReference, ct);
            }

            return TypedResults.Ok(ChannelResponse.From(channel, logger));
        })
        .RequireAuthorization(Policies.TenantAdmin)
        .WithName("UpdateChannel")
        .WithSummary("Renames, pauses/resumes or reconfigures a channel; secret rotates (value) or clears (empty string) the credential.");

        channels.MapDelete("/{id:guid}", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (Guid id, [FromServices] DocProcDbContext db, ITenantContext tenant, ISecretStore secrets, ILogger<ChannelResponse> logger, CancellationToken ct) =>
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

            // The database refuses it as well (NO ACTION). Clearing the scope instead would widen the binding to every
            // document of the tenant.
            if (await db.OutputBindings.AnyAsync(b => b.ChannelId == id, ct))
            {
                return Conflict("Channel in use", "Output bindings are scoped to this channel; change or delete them first.");
            }

            if (await db.SupplierSourceRules.AnyAsync(r => r.ChannelId == id, ct))
            {
                return Conflict("Channel in use", "Supplier source rules name this channel; change or delete them first.");
            }

            var reference = SecretReference(channel.TenantId, channel.Id);
            var ownsSecret = OwnsSecret(channel, reference, logger);
            db.IngestionChannels.Remove(channel);
            await db.SaveChangesAsync(ct);
            if (ownsSecret)
            {
                await secrets.DeleteAsync(reference, ct);
            }

            return TypedResults.NoContent();
        })
        .RequireAuthorization(Policies.TenantAdmin)
        .WithName("DeleteChannel")
        .WithSummary("Deletes a channel that has never received anything and no output binding is scoped to, and its credential; otherwise 409.");

        return group;
    }

    /// <summary>The server-generated secret reference of a channel; never taken from a request.</summary>
    internal static string SecretReference(Guid tenantId, Guid channelId) => ChannelSecrets.Reference(tenantId, channelId);

    /// <summary>
    /// A mailbox's or watched folder's config read as its connector's shape (docs/11 §6.3) gives the channel's source
    /// key; the problems go to <paramref name="errors"/> (with <c>sourceRouting</c> on they refuse the save). Upload
    /// channels keep a free config and have none.
    /// </summary>
    private static string? ConnectorConfig(Errors errors, ChannelType type, string configJson, string tenantSlug, MailOptions mail)
    {
        switch (type)
        {
            case ChannelType.Mailbox:
                var mailbox = MailboxChannelConfig.TryParse(configJson, mail.AllowedHosts, out var mailErrors);
                foreach (var error in mailErrors)
                {
                    errors.Add("config", error);
                }

                return mailbox?.SourceKey;
            case ChannelType.WatchedFolder:
                var folder = WatchedFolderChannelConfig.TryParse(configJson, out var folderErrors);
                foreach (var error in folderErrors)
                {
                    errors.Add("config", error);
                }

                return folder?.SourceKey(tenantSlug);
            default:
                return null;
        }
    }

    private static async Task ValidateDefaultsAsync(DocProcDbContext db, Errors errors, Guid? supplierId, string? typeKey, Guid? entityId, CancellationToken ct)
    {
        if (supplierId is { } s && !await db.Suppliers.AnyAsync(x => x.Id == s, ct))
        {
            errors.Add("defaultSupplierId", $"No supplier {s} in this tenant.");
        }

        if (typeKey is not null && !DocumentTypeKey.TryParse(typeKey, out _))
        {
            errors.Add("defaultDocumentTypeKey", $"'{typeKey}' is not a type key.");
        }

        if (entityId is { } e && !await db.ReceivingEntities.AnyAsync(x => x.Id == e, ct))
        {
            errors.Add("defaultReceivingEntityId", $"No receiving entity {e} in this tenant.");
        }
    }

    /// <summary>
    /// Saves the channel; when another channel (of any tenant) already reads its mailbox or folder, 409 if
    /// <paramref name="strict"/> (sourceRouting on), else the channel is saved without a source key (the poller claims
    /// one before it reads, and puts the channel in Error when the source is taken). The source key is unique across
    /// tenants in the database (docs/11 §6.3), which a tenant session cannot query past its own rows.
    /// </summary>
    private static async Task<ProblemHttpResult?> SaveSourceAsync(DocProcDbContext db, IngestionChannel channel, bool strict, CancellationToken ct)
    {
        if (channel.SourceKey is { } key && await db.IngestionChannels.AnyAsync(c => c.Id != channel.Id && c.Type == channel.Type && c.SourceKey == key, ct))
        {
            if (strict)
            {
                return Conflict("Source in use", "Another channel of this tenant already reads this mailbox or folder.");
            }

            channel.SourceKey = null;
        }

        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException) when (channel.SourceKey is not null)
        {
            if (strict)
            {
                return Conflict("Source in use", "Another channel already reads this mailbox or folder.");
            }

            channel.SourceKey = null;
            await db.SaveChangesAsync(ct);
            return null;
        }
    }

    /// <summary>
    /// Whether the stored reference is the channel's own, the only one this API ever writes or deletes through. A row
    /// written before references were server-generated may carry a client-chosen one such as <c>platform/{configId}</c>
    /// or another tenant's key reference: that is legacy data. It is logged, dropped from the row on the next rotate or
    /// clear, and never touched in the store, so a tenant admin can neither overwrite nor delete a secret that is not
    /// the channel's.
    /// </summary>
    private static bool OwnsSecret(IngestionChannel channel, string reference, ILogger logger)
    {
        if (channel.SecretRef is null)
        {
            return false;
        }

        if (string.Equals(channel.SecretRef, reference, StringComparison.Ordinal))
        {
            return true;
        }

        logger.LogWarning("Channel {ChannelId} carries a secret reference that is not its own; the row is detached from it and the store is left untouched.", channel.Id);
        return false;
    }

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
