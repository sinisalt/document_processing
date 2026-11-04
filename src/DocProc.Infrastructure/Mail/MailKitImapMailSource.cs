using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
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
/// <c>{UIDVALIDITY}:{last UID}</c>; a folder whose UIDVALIDITY changed is read from its start again (the channel's unique
/// message ids keep what was ingested from coming in twice). A message over <see cref="MailOptions.MaxMessageBytes"/>
/// is yielded without its bytes and with <see cref="InboundMessage.SkippedReason"/>, so the cursor moves past it.
/// <para>
/// A tenant's own server is checked like a tenant endpoint (docs/11 §16): the name is resolved, every address must be
/// public (or the host listed in <see cref="MailOptions.AllowedHosts"/>), the socket connects to an address that was
/// checked, and TLS is required. The platform server (<see cref="MailOptions.ImapHost"/>) is the platform's own.
/// </para>
/// </summary>
public sealed class MailKitImapMailSource(ImapEndpoint endpoint, MailOptions options, int maxMessages) : IMailSource
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
        using var client = new ImapClient { Timeout = (int)options.Timeout.TotalMilliseconds };
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
                var id = MessageId(validity, summary.UniqueId.Id);
                if (summary.Size is { } size && size > options.MaxMessageBytes)
                {
                    yield return new InboundMessage(id, next, receivedAt, from, to, summary.Envelope?.Subject, ReadOnlyMemory<byte>.Empty, [])
                    {
                        SkippedReason = $"The message is {size.ToString(CultureInfo.InvariantCulture)} bytes, over the limit of {options.MaxMessageBytes.ToString(CultureInfo.InvariantCulture)}.",
                    };
                    continue;
                }

                byte[] raw;
                using (var stream = await folder.GetStreamAsync(summary.UniqueId, cancellationToken).ConfigureAwait(false))
                using (var buffer = new MemoryStream())
                {
                    await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                    raw = buffer.ToArray();
                }

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

    /// <summary>The unique id of a message in its channel (<c>Ingestions.SourceMessageId</c>).</summary>
    public static string MessageId(uint validity, uint uid) => $"imap:{validity.ToString(CultureInfo.InvariantCulture)}:{uid.ToString(CultureInfo.InvariantCulture)}";

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
