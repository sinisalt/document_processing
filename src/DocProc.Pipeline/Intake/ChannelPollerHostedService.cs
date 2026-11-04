using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Pipeline;
using DocProc.Application.Routing;
using DocProc.Infrastructure.Configuration;
using DocProc.Infrastructure.Http;
using DocProc.Infrastructure.Mail;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DocProc.Pipeline.Intake;

/// <summary>
/// The channel pollers of docs/11 §7.1: every <see cref="IntakeOptions.PollInterval"/>, each active <c>Mailbox</c> and
/// <c>WatchedFolder</c> channel of a tenant with <c>sourceRouting</c> on is read, and what arrived becomes an ingestion
/// with its source facts (sender, recipients, mailbox, subject, sender authentication and forwarder for mail; the
/// folder path relative to the channel root for files), queued for stage 1 like an upload. A tenant without the switch
/// is never polled, as before P4.
/// <list type="bullet">
/// <item><b>Mailbox</b> (<see cref="MailKitImapMailSource"/>): the messages after the channel's cursor
/// (<c>{UIDVALIDITY}:{UID}</c>); the .eml is the raw artefact, its attachments the items, and forwards from a trusted
/// forwarder domain (the channel's and the tenant's) are unwrapped (<see cref="MailMessageReader"/>). The credential is
/// the channel's write-only secret.</item>
/// <item><b>WatchedFolder</b>: the files below <c>{Intake:FolderRoot}/{tenant slug}/{path}</c> whose size and time stayed
/// unchanged for <c>stableSeconds</c> (seen unchanged by two scans that far apart), outside <c>processed/</c>,
/// <c>failed/</c> and the folders of the tenant's other watched-folder channels; links and hidden files are skipped. A
/// file taken is moved to <c>processed/</c> (one that cannot be read or is too large to <c>failed/</c>), keeping its
/// relative path.</item>
/// </list>
/// Each message or file is ingested once per channel: its id (<c>imap:{validity}:{uid}</c>, or a hash of the file's
/// relative path, size and time) is unique per channel in the database, so a second host polling the same channel, or a
/// retry after a crash before the cursor moved, finds it there and moves on.
/// </summary>
public sealed class ChannelPollerHostedService(
    IServiceProvider services,
    IPipelineQueue queue,
    TimeProvider clock,
    IHostEnvironment environment,
    IOptions<IntakeOptions> options,
    IOptions<MailOptions> mailOptions,
    ILogger<ChannelPollerHostedService> logger) : BackgroundService
{
    private const int MaxErrorLength = 2000;
    private readonly IntakeOptions _options = options.Value;
    private readonly MailOptions _mail = mailOptions.Value;

    // Per channel, per file: the size and time a scan saw and when it first saw them unchanged.
    private readonly ConcurrentDictionary<Guid, Dictionary<string, (long Size, DateTime WrittenAt, DateTimeOffset SeenAt)>> _observed = new();

    private ISystemDbContextFactory? Contexts => services.GetService<ISystemDbContextFactory>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.PollersEnabled || _options.PollInterval <= TimeSpan.Zero)
        {
            logger.LogInformation("Channel pollers are off on this host (Intake:PollersEnabled, Intake:PollInterval).");
            return;
        }

        if (Contexts is null)
        {
            logger.LogWarning("Channel pollers are off: this host has no database.");
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_options.PollInterval, clock, stoppingToken).ConfigureAwait(false);
                try
                {
                    await PollOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Channel poll failed; retrying in {Interval}.", _options.PollInterval);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    /// <summary>One round over every polling channel; returns how many ingestions it created.</summary>
    public async Task<int> PollOnceAsync(CancellationToken cancellationToken)
    {
        var contexts = Contexts ?? throw new InvalidOperationException("The channel pollers need persistence (ISystemDbContextFactory).");
        List<(Guid Id, Guid TenantId)> channels;
        await using (var db = contexts.CreatePlatformScoped())
        {
            var candidates = await db.IngestionChannels.AsNoTracking()
                .Where(c => c.Status == ChannelStatus.Active && (c.Type == ChannelType.Mailbox || c.Type == ChannelType.WatchedFolder))
                .OrderBy(c => c.TenantId).ThenBy(c => c.Id)
                .Select(c => new { c.Id, c.TenantId })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var tenantIds = candidates.Select(c => c.TenantId).Distinct().ToList();
            var tenants = await db.Tenants.AsNoTracking().Where(t => tenantIds.Contains(t.Id) && t.Status == TenantStatus.Active).ToListAsync(cancellationToken).ConfigureAwait(false);
            var routed = tenants.Where(t => EfTenantFeatures.From(t.Settings.Features).IsEnabled(TenantFeature.SourceRouting)).Select(t => t.Id).ToHashSet();
            channels = [.. candidates.Where(c => routed.Contains(c.TenantId)).Select(c => (c.Id, c.TenantId))];
        }

        var created = 0;
        foreach (var (channelId, tenantId) in channels)
        {
            try
            {
                created += await PollChannelAsync(tenantId, channelId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Channel {ChannelId} of tenant {TenantId} could not be polled.", channelId, tenantId);
                await RecordErrorAsync(tenantId, channelId, Describe(exception), cancellationToken).ConfigureAwait(false);
            }
        }

        return created;
    }

    /// <summary>Polls one channel in its tenant's scope; returns how many ingestions it created.</summary>
    public async Task<int> PollChannelAsync(Guid tenantId, Guid channelId, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<DocProcDbContext>();
        var channel = await db.IngestionChannels.SingleOrDefaultAsync(c => c.Id == channelId, cancellationToken).ConfigureAwait(false);
        if (channel is null || channel.Status != ChannelStatus.Active)
        {
            return 0;
        }

        return channel.Type switch
        {
            ChannelType.Mailbox => await PollMailboxAsync(scope.ServiceProvider, db, channel, cancellationToken).ConfigureAwait(false),
            ChannelType.WatchedFolder => await PollFolderAsync(scope.ServiceProvider, db, channel, cancellationToken).ConfigureAwait(false),
            _ => 0,
        };
    }

    private async Task<int> PollMailboxAsync(IServiceProvider scoped, DocProcDbContext db, IngestionChannel channel, CancellationToken cancellationToken)
    {
        var config = MailboxChannelConfig.TryParse(channel.ConfigJson, _mail.AllowedHosts, out var errors);
        if (config is null)
        {
            return await FailAsync(db, channel, "The mailbox config is not valid: " + string.Join(" ", errors), cancellationToken).ConfigureAwait(false);
        }

        var reference = ChannelSecrets.Reference(channel.TenantId, channel.Id);
        var password = string.Equals(channel.SecretRef, reference, StringComparison.Ordinal)
            ? await scoped.GetRequiredService<ISecretStore>().GetAsync(reference, cancellationToken).ConfigureAwait(false)
            : null;
        var endpoint = MailKitImapMailSource.Endpoint(config, password, _mail, out var problem);
        if (endpoint is null)
        {
            return await FailAsync(db, channel, problem!, cancellationToken).ConfigureAwait(false);
        }

        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == channel.TenantId, cancellationToken).ConfigureAwait(false);
        var forwarders = config.ForwarderDomains
            .Concat((tenant.Settings.TrustedForwarderDomains ?? []).Select(SourceConditions.Domain).Where(d => d is not null).Select(d => d!))
            .ToHashSet(StringComparer.Ordinal);
        var authServIds = _mail.TrustedAuthServIds.Append(config.AuthServId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var read = new MailReadOptions(config.UnwrapForwards ?? true, forwarders, authServIds);
        var blobs = scoped.GetRequiredService<IBlobStore>();
        var source = new MailKitImapMailSource(endpoint, _mail, Math.Max(1, _options.MaxMessagesPerPoll));

        var created = 0;
        string? skipped = null;
        await foreach (var message in source.FetchAsync(channel.Cursor, cancellationToken).ConfigureAwait(false))
        {
            var now = clock.GetUtcNow();
            if (message.SkippedReason is { } reason)
            {
                skipped = $"Message {message.MessageId} was not ingested: {reason}";
                logger.LogWarning("Channel {ChannelId}: {Reason}", channel.Id, skipped);
                channel.Cursor = message.Cursor;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            MimeMessage mime;
            using (var stream = new MemoryStream(message.RawEml.ToArray(), writable: false))
            {
                mime = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            var parsed = MailMessageReader.Read(mime, read);
            var files = parsed.Attachments
                .Select(a => new UploadFile(UploadIntake.FitFileName(a.FileName), a.ContentType, ItemKinds.Detect(a.ContentType, a.FileName), () => new MemoryStream(a.Content.ToArray(), writable: false)))
                .ToList();
            var stored = await UploadIntake.StoreAsync(blobs, channel.TenantId, files, cancellationToken).ConfigureAwait(false);
            var raw = (await UploadIntake.StoreAsync(blobs, channel.TenantId, [new UploadFile("message.eml", "message/rfc822", ItemKind.Other, () => new MemoryStream(message.RawEml.ToArray(), writable: false))], cancellationToken).ConfigureAwait(false))[0];
            var metadata = new SourceMetadata(Sender: parsed.Sender, Subject: parsed.Subject)
            {
                Recipients = parsed.Recipients,
                Mailbox = config.MailboxAddress,
                SenderAuth = parsed.SenderAuth,
                ForwardedBy = parsed.ForwardedBy,
                ReceivedAt = message.ReceivedAt,
                MessageId = message.MessageId,
            };
            var ingestion = UploadIntake.CreateIngestion(channel.TenantId, channel.Id, metadata, stored, now, raw);
            channel.Cursor = message.Cursor;
            channel.LastPolledAt = now;
            channel.LastError = null;
            if (await SaveOnceAsync(db, channel, ingestion, cancellationToken).ConfigureAwait(false))
            {
                created++;
                await queue.EnqueueAsync(new ProcessIngestion(channel.TenantId, ingestion.Id), cancellationToken).ConfigureAwait(false);
            }
        }

        channel.LastPolledAt = clock.GetUtcNow();
        channel.LastError = skipped is null ? null : Truncate(skipped);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    private async Task<int> PollFolderAsync(IServiceProvider scoped, DocProcDbContext db, IngestionChannel channel, CancellationToken cancellationToken)
    {
        var config = WatchedFolderChannelConfig.TryParse(channel.ConfigJson, out var errors);
        if (config is null)
        {
            return await FailAsync(db, channel, "The watched-folder config is not valid: " + string.Join(" ", errors), cancellationToken).ConfigureAwait(false);
        }

        var slug = await db.Tenants.AsNoTracking().Where(t => t.Id == channel.TenantId).Select(t => t.Slug).SingleAsync(cancellationToken).ConfigureAwait(false);
        var tenantRoot = TenantRoot(slug);
        var channelRoot = Path.GetFullPath(Path.Combine(tenantRoot, config.RelativePath));
        if (!Inside(channelRoot, tenantRoot))
        {
            return await FailAsync(db, channel, "The folder is outside the tenant's drop zone.", cancellationToken).ConfigureAwait(false);
        }

        if (!Directory.Exists(channelRoot))
        {
            return await FailAsync(db, channel, $"The folder {config.RelativePath} does not exist in the tenant's drop zone.", cancellationToken).ConfigureAwait(false);
        }

        // The folders of the tenant's other watched-folder channels below this one are theirs.
        var others = await db.IngestionChannels.AsNoTracking()
            .Where(c => c.Id != channel.Id && c.Type == ChannelType.WatchedFolder && c.Status == ChannelStatus.Active)
            .Select(c => c.ConfigJson)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var excluded = others
            .Select(json => WatchedFolderChannelConfig.TryParse(json, out _))
            .Where(c => c is not null)
            .Select(c => Path.GetFullPath(Path.Combine(tenantRoot, c!.RelativePath)))
            .Where(other => !string.Equals(other, channelRoot, StringComparison.Ordinal) && Inside(other, channelRoot))
            .Append(Path.Combine(channelRoot, "processed"))
            .Append(Path.Combine(channelRoot, "failed"))
            .ToList();

        var stableFor = config.StableSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : _options.FolderStableFor;
        var observed = _observed.GetOrAdd(channel.Id, _ => new Dictionary<string, (long, DateTime, DateTimeOffset)>(StringComparer.Ordinal));
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            MatchType = MatchType.Simple,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        var now = clock.GetUtcNow();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ready = new List<FileInfo>();
        var truncated = false;
        foreach (var path in Directory.EnumerateFiles(channelRoot, config.FilePattern, enumeration).Order(StringComparer.Ordinal))
        {
            if (excluded.Any(e => Inside(path, e)) || IsTemporary(Path.GetFileName(path)))
            {
                continue;
            }

            var file = new FileInfo(path);
            if (file.LinkTarget is not null)
            {
                continue;
            }

            seen.Add(path);
            var current = (file.Length, file.LastWriteTimeUtc);
            if (!observed.TryGetValue(path, out var before) || before.Size != current.Length || before.WrittenAt != current.LastWriteTimeUtc)
            {
                observed[path] = (current.Length, current.LastWriteTimeUtc, now);
                if (stableFor > TimeSpan.Zero)
                {
                    continue;
                }
            }
            else if (now - before.SeenAt < stableFor)
            {
                continue;
            }

            ready.Add(file);
            if (ready.Count >= Math.Max(1, _options.MaxFilesPerScan))
            {
                truncated = true;
                break;
            }
        }

        // A scan cut short at the file limit did not look at the rest, which keeps what earlier scans saw of them.
        foreach (var gone in truncated ? [] : observed.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            observed.Remove(gone);
        }

        var blobs = scoped.GetRequiredService<IBlobStore>();
        var created = 0;
        foreach (var file in ready)
        {
            var relative = Path.GetRelativePath(channelRoot, file.FullName).Replace('\\', '/');
            observed.Remove(file.FullName);
            try
            {
                if (file.Length > _options.MaxFileBytes)
                {
                    logger.LogWarning("Channel {ChannelId}: {File} is over the limit of {Limit} bytes; moved to failed/.", channel.Id, relative, _options.MaxFileBytes);
                    Move(file.FullName, Path.Combine(channelRoot, "failed", relative));
                    continue;
                }

                var bytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken).ConfigureAwait(false);
                var name = file.Name;
                var contentType = ItemKinds.MimeTypeFor(name);
                var stored = await UploadIntake.StoreAsync(blobs, channel.TenantId, [new UploadFile(UploadIntake.FitFileName(name), contentType, ItemKinds.Detect(contentType, name), () => new MemoryStream(bytes, writable: false))], cancellationToken).ConfigureAwait(false);
                var folder = SourceConditions.Folder(Path.GetDirectoryName(relative) ?? string.Empty) ?? string.Empty;
                var metadata = new SourceMetadata
                {
                    FolderPath = folder,
                    ReceivedAt = now,
                    MessageId = FileMessageId(relative, file.Length, file.LastWriteTimeUtc),
                };
                var ingestion = UploadIntake.CreateIngestion(channel.TenantId, channel.Id, metadata, stored, now);
                channel.LastPolledAt = now;
                channel.LastError = null;
                if (await SaveOnceAsync(db, channel, ingestion, cancellationToken).ConfigureAwait(false))
                {
                    created++;
                    await queue.EnqueueAsync(new ProcessIngestion(channel.TenantId, ingestion.Id), cancellationToken).ConfigureAwait(false);
                }

                Move(file.FullName, Path.Combine(channelRoot, "processed", relative));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Channel {ChannelId}: {File} could not be taken; moved to failed/.", channel.Id, relative);
                Move(file.FullName, Path.Combine(channelRoot, "failed", relative));
            }
        }

        channel.LastPolledAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    /// <summary>The tenant's own root of the drop zone, <c>{Intake:FolderRoot}/{slug}</c>, as a full path.</summary>
    public string TenantRoot(string tenantSlug)
    {
        var root = Path.IsPathRooted(_options.FolderRoot) ? _options.FolderRoot : Path.Combine(environment.ContentRootPath, _options.FolderRoot);
        return Path.GetFullPath(Path.Combine(root, tenantSlug));
    }

    /// <summary>The unique id of a file in its channel: its relative path, size and time, hashed (a file replaced by another of the same name is new).</summary>
    public static string FileMessageId(string relativePath, long size, DateTime writtenAtUtc) =>
        "file:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{relativePath}\n{size}\n{writtenAtUtc.Ticks}")));

    /// <summary>
    /// Saves the ingestion with the channel's cursor; false when the channel already has an ingestion of this message
    /// (another host took it first): the ingestion is dropped from the unit of work and the cursor still moves.
    /// </summary>
    private static async Task<bool> SaveOnceAsync(DocProcDbContext db, IngestionChannel channel, Ingestion ingestion, CancellationToken cancellationToken)
    {
        db.Ingestions.Add(ingestion);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            var messageId = ingestion.SourceMessageId;
            if (!await db.Ingestions.AsNoTracking().AnyAsync(i => i.ChannelId == channel.Id && i.SourceMessageId == messageId && i.Id != ingestion.Id, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            db.Entry(ingestion).State = EntityState.Detached;
            foreach (var item in ingestion.Items)
            {
                db.Entry(item).State = EntityState.Detached;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    private async Task<int> FailAsync(DocProcDbContext db, IngestionChannel channel, string error, CancellationToken cancellationToken)
    {
        logger.LogWarning("Channel {ChannelId} of tenant {TenantId}: {Error}", channel.Id, channel.TenantId, error);
        channel.LastError = Truncate(error);
        channel.LastPolledAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private async Task RecordErrorAsync(Guid tenantId, Guid channelId, string error, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = services.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
            var db = scope.ServiceProvider.GetRequiredService<DocProcDbContext>();
            var channel = await db.IngestionChannels.SingleOrDefaultAsync(c => c.Id == channelId, cancellationToken).ConfigureAwait(false);
            if (channel is not null)
            {
                channel.LastError = Truncate(error);
                channel.LastPolledAt = clock.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The error of channel {ChannelId} could not be recorded.", channelId);
        }
    }

    /// <summary>What a tenant admin sees of a failed poll: the kind of failure, never a stack or the server's full answer.</summary>
    private static string Describe(Exception exception) => exception switch
    {
        BlockedEndpointException => TenantEndpointGuard.BlockedMessage,
        MailKit.Security.AuthenticationException => "The mailbox refused the login (check the username and the password).",
        MailKit.FolderNotFoundException => "The mailbox has no such folder.",
        System.Net.Sockets.SocketException or MailKit.ServiceNotConnectedException or MailKit.Net.Imap.ImapProtocolException or IOException => "The IMAP server could not be reached or closed the connection.",
        MailKit.Security.SslHandshakeException => "The TLS handshake with the IMAP server failed.",
        TimeoutException or OperationCanceledException => "The IMAP server did not answer in time.",
        _ => "The channel could not be polled: " + exception.GetType().Name + ".",
    };

    private static bool Inside(string path, string root)
    {
        var normal = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(normal, StringComparison.Ordinal) || string.Equals(path, root, StringComparison.Ordinal);
    }

    private static bool IsTemporary(string name) =>
        name.StartsWith('.') || name.StartsWith("~$", StringComparison.Ordinal)
        || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase);

    /// <summary>Moves a file (a name already taken gets a time stamp); a file another host moved first is left alone.</summary>
    private void Move(string from, string to)
    {
        try
        {
            if (!File.Exists(from))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (File.Exists(to))
            {
                to = Path.Combine(Path.GetDirectoryName(to)!, $"{Path.GetFileNameWithoutExtension(to)}.{clock.GetUtcNow():yyyyMMddHHmmssfff}{Path.GetExtension(to)}");
            }

            File.Move(from, to);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "{File} could not be moved to {Target}.", from, to);
        }
    }

    private static string Truncate(string value) => value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];
}
