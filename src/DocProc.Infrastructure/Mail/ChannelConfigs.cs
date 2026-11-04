using System.Text.Json;
using System.Text.Json.Serialization;
using DocProc.Application.Routing;
using DocProc.Infrastructure.Http;

namespace DocProc.Infrastructure.Mail;

/// <summary>
/// The config of a <c>Mailbox</c> channel (docs/11 §6.3): the mailbox address (what a <c>recipient</c> rule and the
/// values view's <c>source.mailbox</c> read), the IMAP server (the platform's <c>Mail:ImapHost</c> when <see cref="Host"/>
/// is left out), the login (the address unless <see cref="Username"/> says otherwise; the password is the channel's
/// write-only secret), the folder, and how forwards are unwrapped. Unknown members are refused.
/// </summary>
public sealed record MailboxChannelConfig
{
    public const string DefaultFolder = "INBOX";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string? Address { get; init; }

    public string? Host { get; init; }

    public int? Port { get; init; }

    /// <summary><c>ssl</c> (TLS on connect) or <c>starttls</c>; a tenant host never runs plain. Ignored for the platform server.</summary>
    public string? Security { get; init; }

    public string? Username { get; init; }

    public string? Folder { get; init; }

    public bool? UnwrapForwards { get; init; }

    public IReadOnlyList<string>? TrustedForwarderDomains { get; init; }

    /// <summary>The <c>authserv-id</c> of this mailbox's receiving server, next to the platform's <c>Mail:TrustedAuthServIds</c>.</summary>
    public string? AuthServId { get; init; }

    public string MailboxAddress => SourceConditions.Address(Address)!;

    public string Login => string.IsNullOrWhiteSpace(Username) ? MailboxAddress : Username.Trim();

    public string FolderName => string.IsNullOrWhiteSpace(Folder) ? DefaultFolder : Folder.Trim();

    public bool UsesPlatformServer => string.IsNullOrWhiteSpace(Host);

    /// <summary>
    /// The channel's <c>SourceKey</c>: server, login and folder, so two channels (of any tenants) cannot read one folder of
    /// one mailbox. The platform server is named <c>platform</c>, whatever host it is.
    /// </summary>
    public string SourceKey => $"imap:{(UsesPlatformServer ? "platform" : $"{Host!.Trim().ToLowerInvariant()}:{Port ?? 993}")}/{Login.ToLowerInvariant()}/{FolderName.ToLowerInvariant()}";

    /// <summary>The config of <paramref name="json"/>, or null with the reasons; <paramref name="allowedHosts"/> pass the public-host check.</summary>
    public static MailboxChannelConfig? TryParse(string? json, IReadOnlyCollection<string> allowedHosts, out IReadOnlyList<string> errors)
    {
        var found = new List<string>();
        errors = found;
        MailboxChannelConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<MailboxChannelConfig>(string.IsNullOrWhiteSpace(json) ? "{}" : json, JsonOptions);
        }
        catch (JsonException e)
        {
            found.Add($"config does not read as a mailbox: {e.Message}");
            return null;
        }

        if (config is null)
        {
            found.Add("config is a JSON object.");
            return null;
        }

        var address = SourceConditions.Address(config.Address);
        if (address is null || SourceConditions.DomainOf(address) is null || address.Length > 320)
        {
            found.Add("address is the mailbox's e-mail address (for example invoices@tenant-a.local).");
        }

        if (!string.IsNullOrWhiteSpace(config.Host))
        {
            var host = config.Host.Trim();
            if (host.Length > 253 || host.Contains('/', StringComparison.Ordinal) || host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('['))
            {
                found.Add("host is a host name or address without a scheme, path or port.");
            }
            else if (!allowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase) && TenantEndpointGuard.Validate($"https://{host}/") is { } refused)
            {
                found.Add("host must be a public IMAP server: " + refused);
            }

            if (config.Security is not null && config.Security is not ("ssl" or "starttls"))
            {
                found.Add("security is ssl or starttls: a tenant's own IMAP server is always reached over TLS.");
            }
        }

        if (config.Port is { } port && port is < 1 or > 65535)
        {
            found.Add("port is between 1 and 65535.");
        }

        if (config.Username is { Length: > 320 })
        {
            found.Add("username is at most 320 characters.");
        }

        if (config.Folder is { Length: > 200 })
        {
            found.Add("folder is at most 200 characters.");
        }

        if (config.AuthServId is { Length: > 253 })
        {
            found.Add("authServId is at most 253 characters.");
        }

        if (config.TrustedForwarderDomains is { } domains)
        {
            if (domains.Count > 20)
            {
                found.Add("trustedForwarderDomains holds at most 20 domains.");
            }
            else if (domains.FirstOrDefault(d => SourceConditions.Domain(d) is not { } n || !n.Contains('.', StringComparison.Ordinal) || n.Length > 253) is { } bad)
            {
                found.Add($"'{bad}' is not a domain name.");
            }
            else if (domains.Select(SourceConditions.Domain).FirstOrDefault(d => d is not null && SourceConditions.FreeMailDomains.Contains(d)) is { } freeMail)
            {
                found.Add(SourceConditions.FreeMailForwarderMessage(freeMail));
            }
        }

        return found.Count == 0 ? config : null;
    }

    /// <summary>The forwarder domains of the channel, normalised.</summary>
    public IReadOnlyList<string> ForwarderDomains => [.. (TrustedForwarderDomains ?? []).Select(SourceConditions.Domain).Where(d => d is not null).Select(d => d!).Distinct(StringComparer.Ordinal)];
}

/// <summary>
/// The config of a <c>WatchedFolder</c> channel (docs/11 §7.1): a folder below the tenant's own root of the platform's
/// drop zone (<c>{Intake:FolderRoot}/{tenant slug}/{path}</c>), the files it takes (<see cref="Pattern"/>, a file-name
/// pattern with <c>*</c> and <c>?</c>), and how long a file's size and time must stay the same before it is taken. A
/// tenant names a path inside its own root only: absolute paths and <c>..</c> are refused.
/// </summary>
public sealed record WatchedFolderChannelConfig
{
    public static readonly JsonSerializerOptions JsonOptions = MailboxChannelConfig.JsonOptions;

    public string? Path { get; init; }

    public string? Pattern { get; init; }

    public int? StableSeconds { get; init; }

    /// <summary>The path relative to the tenant's root, normalised (<c>scans/</c>; the empty string for the root itself).</summary>
    public string RelativePath => SourceConditions.Folder(Path ?? string.Empty) ?? string.Empty;

    public string FilePattern => string.IsNullOrWhiteSpace(Pattern) ? "*" : Pattern.Trim();

    public string SourceKey(string tenantSlug) => $"folder:{tenantSlug}/{RelativePath.ToLowerInvariant()}";

    public static WatchedFolderChannelConfig? TryParse(string? json, out IReadOnlyList<string> errors)
    {
        var found = new List<string>();
        errors = found;
        WatchedFolderChannelConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<WatchedFolderChannelConfig>(string.IsNullOrWhiteSpace(json) ? "{}" : json, JsonOptions);
        }
        catch (JsonException e)
        {
            found.Add($"config does not read as a watched folder: {e.Message}");
            return null;
        }

        if (config is null)
        {
            found.Add("config is a JSON object.");
            return null;
        }

        var path = config.Path ?? string.Empty;
        if (path.Length > 400 || System.IO.Path.IsPathRooted(path) || path.Contains(':', StringComparison.Ordinal) || SourceConditions.Folder(path) is null)
        {
            found.Add("path is a folder relative to the tenant's drop-zone root, without '..' (for example scans/ or lieferanten/).");
        }
        else if (SourceConditions.Folder(path) is { } normal && (normal.StartsWith("processed/", StringComparison.OrdinalIgnoreCase) || normal.StartsWith("failed/", StringComparison.OrdinalIgnoreCase)))
        {
            found.Add("path cannot be a processed/ or failed/ folder, where the watcher moves the files it took.");
        }

        if (config.Pattern is { } pattern && (pattern.Length > 100 || pattern.IndexOfAny(['/', '\\']) >= 0))
        {
            found.Add("pattern is a file-name pattern such as *.pdf (at most 100 characters, no folders).");
        }

        if (config.StableSeconds is { } seconds && seconds is < 0 or > 3600)
        {
            found.Add("stableSeconds is between 0 and 3600.");
        }

        return found.Count == 0 ? config : null;
    }
}
