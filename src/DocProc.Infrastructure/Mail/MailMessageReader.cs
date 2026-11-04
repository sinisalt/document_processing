using System.Text.RegularExpressions;
using DocProc.Application.Routing;
using MimeKit;
using MimeKit.Cryptography;

namespace DocProc.Infrastructure.Mail;

/// <summary>One file of a received mail: what becomes an ingestion item.</summary>
public sealed record MailAttachment(string FileName, string ContentType, ReadOnlyMemory<byte> Content);

/// <summary>
/// A received mail as the mailbox channel ingests it (docs/11 §7.1): the sender the source rules read (the original
/// sender of an unwrapped forward), the forwarder, the recipients (To and Cc), the subject, the sender authentication
/// and the files.
/// </summary>
public sealed record ParsedMail(
    string? MessageId,
    string? Sender,
    string? ForwardedBy,
    IReadOnlyList<string> Recipients,
    string? Subject,
    DateTimeOffset? Date,
    SenderAuthentication SenderAuth,
    IReadOnlyList<MailAttachment> Attachments,
    string? Unwrapped);

/// <summary>How a mailbox reads its mail (the channel's config and the platform's trust settings).</summary>
/// <param name="UnwrapForwards">Unwrap a forward from a trusted forwarder to its original sender.</param>
/// <param name="TrustedForwarderDomains">The forwarder domains (the channel's and the tenant's), lower-cased.</param>
/// <param name="TrustedAuthServIds">
/// The <c>authserv-id</c>s whose <c>Authentication-Results</c> are believed (the receiving MTA's). Empty: the topmost
/// header, which the last hop (the receiving server) adds, is read; a header further down was written by someone else.
/// </param>
public sealed record MailReadOptions(bool UnwrapForwards, IReadOnlyCollection<string> TrustedForwarderDomains, IReadOnlyCollection<string> TrustedAuthServIds)
{
    public const int MaxAttachments = 50;

    public static MailReadOptions Default { get; } = new(true, [], []);
}

/// <summary>
/// Reads a received message with MimeKit (docs/11 §7.1): sender, recipients, subject, attachments (every leaf part
/// with a file name or an attachment disposition; the text and HTML bodies are not documents), and the SPF, DKIM and
/// DMARC results of the trusted <c>Authentication-Results</c> header. With <see cref="MailReadOptions.UnwrapForwards"/>
/// and a sender whose domain is a trusted forwarder, a forward is unwrapped: an attached <c>message/rfc822</c> gives
/// the original sender, subject and attachments (the forwarder's other attachments are kept); an inline forward (a
/// "Forwarded message" or "Weitergeleitete Nachricht" block with a <c>From:</c> or <c>Von:</c> line) gives the
/// original sender, the attachments being the forward's own. The forwarder goes to <see cref="ParsedMail.ForwardedBy"/>.
/// Pure: no I/O beyond the message given.
/// </summary>
public static partial class MailMessageReader
{
    private static readonly string[] ForwardMarkers =
    [
        "---------- forwarded message",
        "-------- forwarded message",
        "begin forwarded message",
        "-------- original message",
        "-----original message-----",
        "-------- weitergeleitete nachricht",
        "---------- weitergeleitete nachricht",
        "ursprüngliche nachricht",
    ];

    [GeneratedRegex(@"^\s*(?:\*\s*)?(?:From|Von|De|Da)\s*:\s*\**\s*(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex FromLine();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex AddressPattern();

    [GeneratedRegex(@"^\s*(?:(?:Fwd?|WG|AW|TR|Fw)\s*:\s*)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ForwardPrefix();

    public static ParsedMail Read(MimeMessage message, MailReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(options);

        var from = SourceConditions.Address(message.From.Mailboxes.FirstOrDefault()?.Address);
        var fromDomain = SourceConditions.DomainOf(from);
        var recipients = message.To.Mailboxes.Concat(message.Cc.Mailboxes)
            .Select(m => SourceConditions.Address(m.Address))
            .Where(a => a is not null)
            .Select(a => a!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var auth = Authentication(message, fromDomain, options.TrustedAuthServIds);
        var (attachments, attached) = Files(message.Body);

        var trusted = options.UnwrapForwards && fromDomain is not null && options.TrustedForwarderDomains.Contains(fromDomain);
        if (trusted && attached.FirstOrDefault(m => m.From.Mailboxes.Any()) is { } original)
        {
            var sender = SourceConditions.Address(original.From.Mailboxes.First().Address);
            var (inner, _) = Files(original.Body);
            var outer = attachments;
            return new ParsedMail(
                MessageId(message),
                sender,
                from,
                recipients,
                original.Subject ?? StripForwardPrefix(message.Subject),
                message.Date == DateTimeOffset.MinValue ? null : message.Date,
                Forwarded(sender, auth),
                [.. inner.Concat(outer).Take(MailReadOptions.MaxAttachments)],
                "attachedMessage");
        }

        if (trusted && InlineForwardSender(message.TextBody) is { } inlineSender)
        {
            return new ParsedMail(
                MessageId(message),
                inlineSender,
                from,
                recipients,
                StripForwardPrefix(message.Subject),
                message.Date == DateTimeOffset.MinValue ? null : message.Date,
                Forwarded(inlineSender, auth),
                [.. attachments.Take(MailReadOptions.MaxAttachments)],
                "inline");
        }

        return new ParsedMail(
            MessageId(message),
            from,
            null,
            recipients,
            message.Subject,
            message.Date == DateTimeOffset.MinValue ? null : message.Date,
            auth,
            [.. attachments.Take(MailReadOptions.MaxAttachments)],
            null);
    }

    /// <summary>The results of the trusted <c>Authentication-Results</c> header for the From domain (see <see cref="MailReadOptions.TrustedAuthServIds"/>).</summary>
    public static SenderAuthentication Authentication(MimeMessage message, string? fromDomain, IReadOnlyCollection<string> trustedAuthServIds)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(trustedAuthServIds);
        AuthenticationResults? chosen = null;
        foreach (var header in message.Headers.Where(h => h.Id == HeaderId.AuthenticationResults))
        {
            if (!AuthenticationResults.TryParse(header.RawValue, out var results))
            {
                continue;
            }

            if (trustedAuthServIds.Count == 0)
            {
                chosen = results; // the topmost header: the receiving server's
                break;
            }

            if (results.AuthenticationServiceIdentifier is { } id && trustedAuthServIds.Contains(id.Trim().ToLowerInvariant()))
            {
                chosen = results;
                break;
            }
        }

        if (chosen is null)
        {
            return SenderAuthentication.None(fromDomain);
        }

        string? Result(string method) => chosen.Results
            .Where(r => string.Equals(r.Method, method, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Result?.ToLowerInvariant())
            .OrderBy(r => r == SenderAuthentication.Pass ? 0 : 1)
            .FirstOrDefault();

        var dkimDomains = chosen.Results
            .Where(r => string.Equals(r.Method, "dkim", StringComparison.OrdinalIgnoreCase) && string.Equals(r.Result, SenderAuthentication.Pass, StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => r.Properties)
            .Where(p => string.Equals(p.PropertyType, "header", StringComparison.OrdinalIgnoreCase) && p.Property is "d" or "i")
            .Select(p => SourceConditions.Domain(p.Property == "i" ? SourceConditions.DomainOf(p.Value) ?? p.Value : p.Value))
            .Where(d => d is not null)
            .Select(d => d!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // DMARC is about the domain of the header From; a result stated for another domain does not count.
        var dmarcFrom = chosen.Results
            .Where(r => string.Equals(r.Method, "dmarc", StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => r.Properties)
            .Where(p => string.Equals(p.PropertyType, "header", StringComparison.OrdinalIgnoreCase) && p.Property == "from")
            .Select(p => SourceConditions.Domain(p.Value))
            .FirstOrDefault();
        var dmarc = Result("dmarc");
        if (dmarc == SenderAuthentication.Pass && dmarcFrom is not null && fromDomain is not null && dmarcFrom != fromDomain)
        {
            dmarc = "mismatch";
        }

        return new SenderAuthentication
        {
            Spf = Result("spf"),
            Dkim = Result("dkim"),
            Dmarc = dmarc,
            DkimDomains = dkimDomains.Count == 0 ? null : dkimDomains,
            FromDomain = fromDomain,
            AuthServId = chosen.AuthenticationServiceIdentifier,
        };
    }

    /// <summary>The sender of an inline forward block, or null when the body has none.</summary>
    public static string? InlineForwardSender(string? textBody)
    {
        if (string.IsNullOrWhiteSpace(textBody))
        {
            return null;
        }

        var text = textBody.Length > 64 * 1024 ? textBody[..(64 * 1024)] : textBody;
        var lower = text.ToLowerInvariant();
        var marker = ForwardMarkers.Select(m => lower.IndexOf(m, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
        if (marker < 0)
        {
            return null;
        }

        try
        {
            var block = text[marker..Math.Min(text.Length, marker + 2000)];
            var line = FromLine().Match(block);
            if (!line.Success)
            {
                return null;
            }

            var address = AddressPattern().Match(line.Groups["value"].Value);
            return address.Success ? SourceConditions.Address(address.Value) : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static SenderAuthentication Forwarded(string? originalSender, SenderAuthentication forwarder) => new()
    {
        FromDomain = SourceConditions.DomainOf(originalSender),
        Via = SenderAuthentication.ViaTrustedForwarder,
        Forwarder = forwarder,
    };

    private static string? MessageId(MimeMessage message) => string.IsNullOrWhiteSpace(message.MessageId) ? null : message.MessageId.Trim();

    private static string? StripForwardPrefix(string? subject)
    {
        if (subject is null)
        {
            return null;
        }

        try
        {
            return ForwardPrefix().Replace(subject, string.Empty);
        }
        catch (RegexMatchTimeoutException)
        {
            return subject;
        }
    }

    /// <summary>The files of a body (leaf parts with a file name or an attachment disposition) and the messages attached to it.</summary>
    private static (List<MailAttachment> Files, List<MimeMessage> Messages) Files(MimeEntity? body)
    {
        var files = new List<MailAttachment>();
        var messages = new List<MimeMessage>();
        Walk(body, files, messages, depth: 0);
        return (files, messages);
    }

    private static void Walk(MimeEntity? entity, List<MailAttachment> files, List<MimeMessage> messages, int depth)
    {
        if (entity is null || depth > 10 || files.Count >= MailReadOptions.MaxAttachments)
        {
            return;
        }

        switch (entity)
        {
            case Multipart multipart:
                foreach (var child in multipart)
                {
                    Walk(child, files, messages, depth + 1);
                }

                break;
            case MessagePart { Message: { } attached }:
                messages.Add(attached);
                break;
            case MimePart part when part.Content is not null && (part.IsAttachment || !string.IsNullOrWhiteSpace(part.FileName)):
                using (var buffer = new MemoryStream())
                {
                    part.Content.DecodeTo(buffer);
                    var name = string.IsNullOrWhiteSpace(part.FileName) ? $"attachment-{files.Count + 1}" : Path.GetFileName(part.FileName.Replace('\\', '/'));
                    files.Add(new MailAttachment(name, part.ContentType.MimeType.ToLowerInvariant(), buffer.ToArray()));
                }

                break;
        }
    }
}
