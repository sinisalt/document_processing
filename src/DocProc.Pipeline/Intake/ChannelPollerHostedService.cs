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
/// channel whose folder, or a folder on the way to it below the tenant's root, is a link is not read at all (it could
/// lead into another tenant's root), and a file is not moved through a link. A file taken is moved to
/// <c>processed/</c> (one that cannot be read or is too large to <c>failed/</c>), keeping its relative path.</item>
/// </list>
/// Each message or file is ingested once per channel: its id (a hash of the RFC <c>Message-ID</c>, sender and raw
/// message, or of the raw message alone, see <see cref="MailKitImapMailSource.ContentMessageId"/>; a hash of the file's
/// relative path, size and time) is unique per channel in the database, so a second host polling the same channel, a
/// retry after a crash before the cursor moved, or a folder read again after its UIDVALIDITY changed finds it there and
/// moves on. A channel is read only under the source key its config gives (docs/11 §6.3), claimed on its first poll
/// when the row has none, so two channels never read one mailbox or folder.
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

        var source = new MailKitImapMailSource(endpoint, _mail, Math.Max(1, _options.MaxMessagesPerPoll));
        try
        {
            // A mailbox's source key is claimed only by a channel that logged in to it with its own credential
            // (docs/11 §6.3): the API never claims one, so a tenant can neither lock nor probe a mailbox it cannot read.
            if (!string.Equals(channel.SourceKey, config.SourceKey, StringComparison.Ordinal))
            {
                await source.VerifyLoginAsync(cancellationToken).ConfigureAwait(false);
                if (!await OwnsSourceAsync(db, channel, config.SourceKey, cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }
            }

            return await ReadMailboxAsync(scoped, db, channel, config, source, cancellationToken).ConfigureAwait(false);
        }
        catch (MailKit.Security.AuthenticationException) when (channel.SourceKey is not null)
        {
            // A key is held only while its channel can log in: one that no longer can (or a key written before the
            // proof was asked for) is given up, so the mailbox's owner can claim it.
            logger.LogWarning("Channel {ChannelId} of tenant {TenantId}: the mailbox refused the login; the channel gives up {SourceKey}.", channel.Id, channel.TenantId, channel.SourceKey);
            channel.SourceKey = null;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<int> ReadMailboxAsync(IServiceProvider scoped, DocProcDbContext db, IngestionChannel channel, MailboxChannelConfig config, MailKitImapMailSource source, CancellationToken cancellationToken)
    {

        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == channel.TenantId, cancellationToken).ConfigureAwait(false);
        var forwarders = config.ForwarderDomains
            .Concat((tenant.Settings.TrustedForwarderDomains ?? []).Select(SourceConditions.Domain).Where(d => d is not null).Select(d => d!))
            .Where(d => !SourceConditions.FreeMailDomains.Contains(d)) // refused on save; never trusted from a row written before
            .ToHashSet(StringComparer.Ordinal);
        var authServIds = _mail.TrustedAuthServIds.Append(config.AuthServId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var read = new MailReadOptions(config.UnwrapForwards ?? true, forwarders, authServIds);
        var blobs = scoped.GetRequiredService<IBlobStore>();

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

            // One copy of the message for parsing and storing (the source's buffer is bounded by Mail:MaxMessageBytes).
            var rawEml = message.RawEml.ToArray();
            ParsedMail parsed;
            try
            {
                MimeMessage mime;
                using (var stream = new MemoryStream(rawEml, writable: false))
                {
                    mime = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
                }

                parsed = MailMessageReader.Read(mime, read);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or NotSupportedException or InvalidOperationException)
            {
                // A message that does not read as a mail (no header block, empty, broken MIME) is skipped like one over
                // the size limit: the cursor moves past it, so it never blocks the messages behind it, and it stays in
                // the mailbox for a person to look at.
                skipped = $"Message {message.MessageId} was not ingested: it does not read as a mail ({exception.GetType().Name}); it stays in the mailbox.";
                logger.LogWarning(exception, "Channel {ChannelId}: {Reason}", channel.Id, skipped);
                channel.Cursor = message.Cursor;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var files = parsed.Attachments
                .Select(a => new UploadFile(UploadIntake.FitFileName(a.FileName), a.ContentType, ItemKinds.Detect(a.ContentType, a.FileName), () => new MemoryStream(a.Content.ToArray(), writable: false)))
                .ToList();
            var stored = await UploadIntake.StoreAsync(blobs, channel.TenantId, files, cancellationToken).ConfigureAwait(false);
            var raw = (await UploadIntake.StoreAsync(blobs, channel.TenantId, [new UploadFile("message.eml", "message/rfc822", ItemKind.Other, () => new MemoryStream(rawEml, writable: false))], cancellationToken).ConfigureAwait(false))[0];
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
            else if (logger.IsEnabled(LogLevel.Information))
            {
                // The same stored message (same Message-ID, sender and bytes; its blobs are content-addressed, so storing
                // them again added nothing): read again after a lost cursor, a UIDVALIDITY change or by another host.
                logger.LogInformation("Channel {ChannelId}: message {MessageId} ({Subject}) is already ingested; the cursor moves past it.", channel.Id, message.MessageId, parsed.Subject);
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
        if (!await OwnsSourceAsync(db, channel, config.SourceKey(slug), cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

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

        // GetFullPath does not resolve links: a folder on the way that is a link could lead out of the tenant's root,
        // so the channel is not read until it is a real folder (and so are its processed/ and failed/ folders).
        if ((LinkOnPath(tenantRoot, channelRoot) ?? LinkOnPath(channelRoot, Path.Combine(channelRoot, "processed")) ?? LinkOnPath(channelRoot, Path.Combine(channelRoot, "failed"))) is { } link)
        {
            return await FailAsync(db, channel, $"The folder {config.RelativePath} is not read: {Path.GetRelativePath(tenantRoot, link).Replace('\\', '/')} is a link, and links are not followed.", cancellationToken).ConfigureAwait(false);
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
                    Move(tenantRoot, channelRoot, file.FullName, Path.Combine(channelRoot, "failed", relative));
                    continue;
                }

                // The scan saw no link, but the file or a folder on the way may have been swapped for one since: the file
                // is opened and what was opened is checked, and only those bytes are read.
                var (bytes, refusal) = await ReadInsideAsync(tenantRoot, file.FullName, _options.MaxFileBytes, cancellationToken).ConfigureAwait(false);
                if (refusal == FolderReadRefusal.Link)
                {
                    logger.LogWarning("Channel {ChannelId}: {File} is, or lies below, a link now; it is not read and not moved.", channel.Id, relative);
                    continue;
                }

                if (bytes is null)
                {
                    logger.LogWarning("Channel {ChannelId}: {File} is over the limit of {Limit} bytes or not a regular file; moved to failed/.", channel.Id, relative, _options.MaxFileBytes);
                    Move(tenantRoot, channelRoot, file.FullName, Path.Combine(channelRoot, "failed", relative));
                    continue;
                }

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
                else if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Channel {ChannelId}: {File} is already ingested (same path, size and time); it is moved to processed/.", channel.Id, relative);
                }

                Move(tenantRoot, channelRoot, file.FullName, Path.Combine(channelRoot, "processed", relative));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Channel {ChannelId}: {File} could not be taken; moved to failed/.", channel.Id, relative);
                Move(tenantRoot, channelRoot, file.FullName, Path.Combine(channelRoot, "failed", relative));
            }
        }

        channel.LastPolledAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    /// <summary>
    /// Whether the channel may read its source (docs/11 §6.3): its stored <c>SourceKey</c> is the key its config gives.
    /// A row without one (a mailbox channel always starts without one; a folder written before P4 or while its source
    /// was taken), or with one its config no longer gives, gets the config's key now (a mailbox channel only after it
    /// logged in with its own secret, see <see cref="PollMailboxAsync"/>); when another channel (of any tenant) already
    /// holds that key, the unique index refuses it and the channel is put in Error, so two channels never read one
    /// mailbox or folder.
    /// </summary>
    private async Task<bool> OwnsSourceAsync(DocProcDbContext db, IngestionChannel channel, string key, CancellationToken cancellationToken)
    {
        if (string.Equals(channel.SourceKey, key, StringComparison.Ordinal))
        {
            return true;
        }

        var stored = channel.SourceKey;
        channel.SourceKey = key;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Channel {ChannelId} of tenant {TenantId} reads {SourceKey} (was {Stored}).", channel.Id, channel.TenantId, key, stored ?? "none");
            }

            return true;
        }
        catch (DbUpdateException)
        {
            channel.SourceKey = stored;
            await StopAsync(db, channel, "Another channel already reads this mailbox or folder; this one is not read.", cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    private async Task StopAsync(DocProcDbContext db, IngestionChannel channel, string error, CancellationToken cancellationToken)
    {
        logger.LogWarning("Channel {ChannelId} of tenant {TenantId} is put in Error: {Error}", channel.Id, channel.TenantId, error);
        channel.Status = ChannelStatus.Error;
        channel.LastError = Truncate(error);
        channel.LastPolledAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Why a file of a watched folder was not read.</summary>
    internal enum FolderReadRefusal
    {
        None,
        Link,
        TooLarge,
    }

    /// <summary>
    /// The bytes of a file of the tenant's drop zone, read from a handle that is the file itself. On Linux (x64, arm64)
    /// the file is opened through <see cref="DropZoneHandles"/>: every folder from the tenant's root down, and the file,
    /// is opened relative to the one above it without following a link, so a file or folder swapped for a link at any
    /// moment is refused by the kernel, and a named pipe opens without waiting for a writer. Elsewhere the way is checked
    /// for links right before opening, and on Linux of another architecture the opened handle's real path
    /// (<c>/proc/self/fd</c>) must be the real path of the tenant's root followed by the file's relative path. A file that
    /// is over <paramref name="maxBytes"/> or cannot be measured (not a regular file: a FIFO, a socket) is
    /// <see cref="FolderReadRefusal.TooLarge"/>.
    /// </summary>
    internal static async Task<(byte[]? Bytes, FolderReadRefusal Refusal)> ReadInsideAsync(string tenantRoot, string path, long maxBytes, CancellationToken cancellationToken)
    {
        FileStream stream;
        if (DropZoneHandles.Supported)
        {
            var relative = Path.GetRelativePath(tenantRoot, path);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                return (null, FolderReadRefusal.Link);
            }

            var (handle, outcome) = DropZoneHandles.OpenFile(tenantRoot, relative);
            if (outcome == DropZoneOutcome.Link)
            {
                return (null, FolderReadRefusal.Link);
            }

            if (handle is null)
            {
                throw new FileNotFoundException("The file is gone.", path);
            }

            stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
        }
        else
        {
            if (LinkOnPath(tenantRoot, path) is not null)
            {
                return (null, FolderReadRefusal.Link);
            }

            stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, Options = FileOptions.Asynchronous | FileOptions.SequentialScan });
        }

        await using (stream.ConfigureAwait(false))
        {
            if (!DropZoneHandles.Supported)
            {
                if (OpenedPath(stream) is { } opened)
                {
                    var expected = Path.Combine(RealPath(tenantRoot), Path.GetRelativePath(tenantRoot, path));
                    if (!string.Equals(opened, expected, StringComparison.Ordinal))
                    {
                        return (null, FolderReadRefusal.Link);
                    }
                }
                else if (LinkOnPath(tenantRoot, path) is not null)
                {
                    return (null, FolderReadRefusal.Link);
                }
            }

            if (!stream.CanSeek || stream.Length > maxBytes)
            {
                return (null, FolderReadRefusal.TooLarge);
            }

            using var buffer = new MemoryStream((int)Math.Min(stream.Length, int.MaxValue));
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return (null, FolderReadRefusal.TooLarge);
                }

                buffer.Write(chunk, 0, read);
            }

            return (buffer.ToArray(), FolderReadRefusal.None);
        }
    }

    /// <summary>The real path of what an open file handle refers to (Linux, <c>/proc/self/fd</c>), or null where that cannot be read.</summary>
    private static string? OpenedPath(FileStream stream)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            var descriptor = stream.SafeFileHandle.DangerousGetHandle().ToInt64();
            return new FileInfo($"/proc/self/fd/{descriptor.ToString(System.Globalization.CultureInfo.InvariantCulture)}").LinkTarget;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A full path with every link on it resolved (as <c>realpath(3)</c>); a part that does not exist is kept as it is.</summary>
    internal static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var parts = new List<string>(full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries));
        var current = root;
        var hops = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            var next = Path.Combine(current, parts[i]);
            string? target;
            try
            {
                target = new FileInfo(next).LinkTarget;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                target = null;
            }

            if (target is null)
            {
                current = next;
                continue;
            }

            if (++hops > 40)
            {
                throw new IOException($"Too many links on the way to {path}.");
            }

            var resolved = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(current, target));
            var resolvedRoot = Path.GetPathRoot(resolved) ?? string.Empty;
            parts = [.. resolved[resolvedRoot.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries), .. parts.Skip(i + 1)];
            current = resolvedRoot;
            i = -1;
        }

        return current;
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

    /// <summary>
    /// The first folder or file from below <paramref name="root"/> down to <paramref name="path"/> that is a link
    /// (symbolic link or junction), or null when there is none; a part that does not exist ends the walk.
    /// </summary>
    public static string? LinkOnPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".")
        {
            return null;
        }

        var current = root;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            try
            {
                if (info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                {
                    return current;
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }

            if (!info.Exists)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Moves a file below the channel's root (a name already taken gets a time stamp); a file another host moved first
    /// is left alone, and so is one whose folder, target folder, or a folder on the way to either, is a link. On Linux
    /// (x64, arm64) the move is bound to handles (<see cref="DropZoneHandles.Move"/>): the folders from the tenant's root
    /// down are opened without following a link and the file is renamed between the two folder handles, so a folder
    /// swapped for a link between the scan and the move is refused, not moved through. Elsewhere the way is checked for
    /// links right before the rename (a narrower window, not none).
    /// </summary>
    private void Move(string tenantRoot, string channelRoot, string from, string to)
    {
        string AlternativeName(string name) => $"{Path.GetFileNameWithoutExtension(name)}.{clock.GetUtcNow():yyyyMMddHHmmssfff}{Path.GetExtension(name)}";
        try
        {
            if (DropZoneHandles.Supported)
            {
                var below = Path.GetRelativePath(tenantRoot, channelRoot);
                var channelParts = below == "." ? 0 : below.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Length;
                var (outcome, movedTo) = DropZoneHandles.Move(tenantRoot, Path.GetRelativePath(tenantRoot, from), Path.GetRelativePath(tenantRoot, to), channelParts, AlternativeName);
                if (outcome == DropZoneOutcome.Link)
                {
                    logger.LogWarning("{File} was not moved to {Target}: a folder on the way, or the file, is a link.", from, to);
                }
                else if (outcome == DropZoneOutcome.Done && logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("{File} was moved to {Target}.", from, movedTo);
                }

                return;
            }

            if (!File.Exists(from))
            {
                return;
            }

            if ((LinkOnPath(channelRoot, from) ?? LinkOnPath(channelRoot, Path.GetDirectoryName(to)!)) is { } link)
            {
                logger.LogWarning("{File} was not moved to {Target}: {Link} is a link.", from, to, link);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (File.Exists(to))
            {
                to = Path.Combine(Path.GetDirectoryName(to)!, AlternativeName(Path.GetFileName(to)));
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
