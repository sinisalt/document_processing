using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using DocProc.Application.Abstractions;
using DocProc.Infrastructure.Http;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

namespace DocProc.Infrastructure.Mail;

/// <summary>Where and how one mailbox is read: the server, the login and the folder.</summary>
public sealed record ImapEndpoint(string Host, int Port, SecureSocketOptions Security, bool PlatformServer, string Username, string? Password, string Folder);

/// <summary>
/// <see cref="IMailSource"/> over IMAP with MailKit (docs/11 §7.1, docs/09 "the GreenMail mailbox"): the messages of one
/// folder newer than the cursor, oldest first, at most <c>maxMessages</c> per call. The cursor is
/// <c>{UIDVALIDITY}:{last UID}</c>; a folder whose UIDVALIDITY changed is read from its start again. The message id
/// (<see cref="InboundMessage.MessageId"/>, unique per channel in <c>Ingestions.SourceMessageId</c>) does not depend on
/// the UIDVALIDITY: it is a hash of the RFC <c>Message-ID</c> header, the sender and the raw message's digest (the bytes
/// the server stores, which a folder read again after a UIDVALIDITY change sends again), so the messages read again are
/// found there and not ingested twice, while a second mail that reuses a Message-ID with other content is a message of
/// its own (<see cref="ContentMessageId"/>). A message over <see cref="MailOptions.MaxMessageBytes"/>, by the size the
/// server reports or by the bytes it then sends, is yielded without its bytes and with
/// <see cref="InboundMessage.SkippedReason"/>, so the cursor moves past it: the body is fetched as a partial
/// (<c>BODY.PEEK[]&lt;0.limit+1&gt;</c>), so no more than one byte past the limit is ever transferred or held.
/// <para>
/// A tenant's own server is checked like a tenant endpoint (docs/11 §16): the name is resolved, every address must be
/// public (or the host listed in <see cref="MailOptions.AllowedHosts"/>), the socket connects to an address that was
/// checked, and TLS is required. The platform server (<see cref="MailOptions.ImapHost"/>) is the platform's own.
/// </para>
/// </summary>
/// <param name="endpoint">The server, the login and the folder.</param>
/// <param name="options">The limits of every poll (<see cref="MailOptions"/>).</param>
/// <param name="maxMessages">The most messages one call yields.</param>
/// <param name="protocolLogger">Where the IMAP conversation is written (tests read the commands sent); null for none.</param>
public sealed class MailKitImapMailSource(ImapEndpoint endpoint, MailOptions options, int maxMessages, IProtocolLogger? protocolLogger = null) : IMailSource
{
    public string ChannelType => "Mailbox";

    /// <summary>The endpoint of a channel's config, or the reason the channel cannot be read.</summary>
    public static ImapEndpoint? Endpoint(MailboxChannelConfig config, string? password, MailOptions options, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);
        problem = null;
        if (config.UsesPlatformServer)
        {
            if (string.IsNullOrWhiteSpace(options.ImapHost))
            {
                problem = "The channel names no IMAP host and the platform runs none (Mail:ImapHost).";
                return null;
            }

            var security = Enum.TryParse<SecureSocketOptions>(options.ImapSecurity, ignoreCase: true, out var parsed) ? parsed : SecureSocketOptions.Auto;
            return new ImapEndpoint(options.ImapHost, options.ImapPort, security, PlatformServer: true, config.Login, password, config.FolderName);
        }

        var tls = config.Security == "starttls" ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect;
        return new ImapEndpoint(config.Host!.Trim(), config.Port ?? (tls == SecureSocketOptions.StartTls ? 143 : 993), tls, PlatformServer: false, config.Login, password, config.FolderName);
    }

    public async IAsyncEnumerable<InboundMessage> FetchAsync(string? cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = protocolLogger is null ? new ImapClient() : new ImapClient(protocolLogger);
        client.Timeout = (int)options.Timeout.TotalMilliseconds;
        await ConnectAsync(client, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(endpoint.Username))
            {
                await client.AuthenticateAsync(endpoint.Username, endpoint.Password ?? string.Empty, cancellationToken).ConfigureAwait(false);
            }

            var folder = await client.GetFolderAsync(endpoint.Folder, cancellationToken).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            var validity = folder.UidValidity;
            var last = ParseCursor(cursor, validity);

            // n:* always names the newest message even when its UID is below n, so the result is filtered again.
            var uids = (await folder.SearchAsync(SearchQuery.Uids(new UniqueIdRange(new UniqueId(validity, last + 1), UniqueId.MaxValue)), cancellationToken).ConfigureAwait(false))
                .Where(u => u.Id > last)
                .OrderBy(u => u.Id)
                .Take(maxMessages)
                .ToList();
            if (uids.Count == 0)
            {
                yield break;
            }

            var summaries = await folder.FetchAsync(uids, MessageSummaryItems.UniqueId | MessageSummaryItems.Size | MessageSummaryItems.InternalDate | MessageSummaryItems.Envelope, cancellationToken).ConfigureAwait(false);
            foreach (var summary in summaries.OrderBy(s => s.UniqueId.Id))
            {
                var next = Cursor(validity, summary.UniqueId.Id);
                var receivedAt = summary.InternalDate ?? DateTimeOffset.UtcNow;
                var from = summary.Envelope?.From?.Mailboxes.FirstOrDefault()?.Address ?? string.Empty;
                var to = summary.Envelope?.To?.Mailboxes.Select(m => m.Address).ToList() ?? [];
                var skippedId = MessageId(validity, summary.UniqueId.Id);
                if (summary.Size is { } size && size > options.MaxMessageBytes)
                {
                    yield return new InboundMessage(skippedId, next, receivedAt, from, to, summary.Envelope?.Subject, ReadOnlyMemory<byte>.Empty, [])
                    {
                        SkippedReason = $"The message is {size.ToString(CultureInfo.InvariantCulture)} bytes, over the limit of {options.MaxMessageBytes.ToString(CultureInfo.InvariantCulture)}.",
                    };
                    continue;
                }

                // The size the server reported is a claim: only the first limit + 1 bytes are fetched (MailKit buffers a
                // fetched body whole before it returns the stream), and a body that fills them is over the limit.
                byte[]? raw;
                using (var stream = await folder.GetStreamAsync(summary.UniqueId, 0, FetchLength(options.MaxMessageBytes), cancellationToken).ConfigureAwait(false))
                {
                    raw = await ReadBoundedAsync(stream, options.MaxMessageBytes, cancellationToken).ConfigureAwait(false);
                }

                if (raw is null)
                {
                    yield return new InboundMessage(skippedId, next, receivedAt, from, to, summary.Envelope?.Subject, ReadOnlyMemory<byte>.Empty, [])
                    {
                        SkippedReason = $"The message is over the limit of {options.MaxMessageBytes.ToString(CultureInfo.InvariantCulture)} bytes.",
                    };
                    continue;
                }

                var id = ContentMessageId(summary.Envelope?.MessageId, from, raw);
                yield return new InboundMessage(id, next, receivedAt, from, to, summary.Envelope?.Subject, raw, []);
            }
        }
        finally
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(quit: true, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The cursor is stored by the poller with the ingestion; nothing is marked on the server (the folder is opened read-only).</summary>
    public Task AcknowledgeAsync(InboundMessage message, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The id of a message that was skipped (never stored): its place in the folder.</summary>
    public static string MessageId(uint validity, uint uid) => $"imap:{validity.ToString(CultureInfo.InvariantCulture)}:{uid.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The unique id of an ingested message in its channel (<c>Ingestions.SourceMessageId</c>), the same however the
    /// folder numbers it: <c>imap:mid:{hash}</c> of the RFC <c>Message-ID</c>, the sender address and the raw message's
    /// SHA-256, or <c>imap:sha:{hash}</c> of the raw message when it has no <c>Message-ID</c>. The raw digest is part of
    /// both, so a mailer that reuses a Message-ID for another mail does not have that mail taken for one already
    /// ingested; the same stored message read again (after a UIDVALIDITY change, or by a second host) has the same id.
    /// </summary>
    public static string ContentMessageId(string? rfcMessageId, string? from, ReadOnlySpan<byte> raw)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(raw));
        var mid = rfcMessageId?.Trim().Trim('<', '>').Trim();
        if (!string.IsNullOrEmpty(mid))
        {
            var key = $"{mid}\n{(from ?? string.Empty).Trim().ToLowerInvariant()}\n{digest}";
            return "imap:mid:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        }

        return "imap:sha:" + digest;
    }

    /// <summary>How many bytes of a body are fetched: one past the limit, so a body over it is told by its length.</summary>
    public static int FetchLength(long maxMessageBytes) => (int)Math.Clamp(maxMessageBytes + 1, 1, int.MaxValue);

    /// <summary>The whole stream, or null once more than <paramref name="limit"/> bytes were read.</summary>
    public static async Task<byte[]?> ReadBoundedAsync(Stream stream, long limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var chunk = new byte[81920];
        using var buffer = new MemoryStream();
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public static string Cursor(uint validity, uint uid) => $"{validity.ToString(CultureInfo.InvariantCulture)}:{uid.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The last UID of the cursor, or 0 for none or for a cursor of another UIDVALIDITY.</summary>
    public static uint ParseCursor(string? cursor, uint validity)
    {
        var parts = cursor?.Split(':');
        return parts is [var v, var u]
               && uint.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var storedValidity)
               && storedValidity == validity
               && uint.TryParse(u, NumberStyles.None, CultureInfo.InvariantCulture, out var uid)
            ? uid
            : 0;
    }

    private async Task ConnectAsync(ImapClient client, CancellationToken cancellationToken)
    {
        if (endpoint.PlatformServer || options.AllowedHosts.Contains(endpoint.Host, StringComparer.OrdinalIgnoreCase))
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, endpoint.Security, cancellationToken).ConfigureAwait(false);
            return;
        }

        // A tenant's host: resolve it, refuse it when any address is private or reserved, and connect to an address
        // that was checked, so a name that resolves elsewhere later cannot get around the check.
        var addresses = IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(TenantEndpointGuard.IsBlocked))
        {
            throw new BlockedEndpointException();
        }

        var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);
            await socket.ConnectAsync(addresses[0], endpoint.Port, timeout.Token).ConfigureAwait(false);
            await client.ConnectAsync(socket, endpoint.Host, endpoint.Port, endpoint.Security, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
