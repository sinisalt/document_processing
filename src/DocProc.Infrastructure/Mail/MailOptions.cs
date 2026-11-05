namespace DocProc.Infrastructure.Mail;

/// <summary>
/// The <c>Mail</c> configuration section (docs/11 §7.1): the platform's IMAP server (GreenMail in compose and under the
/// AppHost), which a mailbox channel without a <c>host</c> of its own reads, and the limits of every mailbox poll.
/// </summary>
public sealed class MailOptions
{
    public const string SectionName = "Mail";

    /// <summary>The platform IMAP host; null when the platform runs none (every mailbox channel then names its host).</summary>
    public string? ImapHost { get; set; }

    /// <summary>The platform IMAP port.</summary>
    public int ImapPort { get; set; } = 143;

    /// <summary>
    /// How the platform server is reached: <c>None</c> (plain, the development mailbox), <c>StartTls</c>,
    /// <c>SslOnConnect</c> or <c>Auto</c> (MailKit's choice by port). A tenant's own host always needs TLS.
    /// </summary>
    public string ImapSecurity { get; set; } = "Auto";

    /// <summary>
    /// Hosts a tenant's mailbox channel may name past the public-host check (a provider reached over a private link).
    /// Every other tenant host must be a public name or address, as for tenant endpoints (docs/11 §16).
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>
    /// The <c>authserv-id</c>s of the platform's receiving server (the MTA in front of <see cref="ImapHost"/>, one for
    /// every tenant's mailbox there) whose <c>Authentication-Results</c> header is believed, for a mailbox on the platform
    /// server only (with that channel's own <c>authServId</c>); a mailbox on a tenant's own host believes only its
    /// channel's <c>authServId</c>. The list is global: every server named here must strip or override a header that
    /// claims any of the ids, whichever tenant's address the mail is for. Empty: no <c>Authentication-Results</c> header
    /// of the platform server is believed and no sender counts as authenticated, so every source rule that needs sender
    /// authentication (the default for mail rules) is skipped with <c>SUPPLIER_SOURCE_UNVERIFIED</c>; a header any
    /// sender could have written is never taken as proof.
    /// </summary>
    public List<string> TrustedAuthServIds { get; set; } = [];

    /// <summary>The largest message ingested; a larger one is skipped and named in the channel's last error.</summary>
    public long MaxMessageBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>The connect, authenticate and read timeout of one IMAP operation.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}
