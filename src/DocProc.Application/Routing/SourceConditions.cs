using System.Security.Cryptography;
using System.Text;

namespace DocProc.Application.Routing;

/// <summary>
/// The normal forms of source facts and rule conditions (docs/11 §6.5): what a rule stores and what the resolver
/// compares, so a rule matches by equality on the indexed columns. Addresses and domains are trimmed and lower-cased;
/// a folder path uses <c>/</c>, has no leading <c>/</c>, no empty, <c>.</c> or <c>..</c> segment and ends with
/// <c>/</c> (the channel root is the empty string).
/// </summary>
public static class SourceConditions
{
    /// <summary>
    /// Free-mail providers (docs/11 §6.5 "platform free-mail deny list"): a sender-domain rule on one of them would
    /// claim every private sender of that provider for one supplier, so it is refused (an exact sender rule is fine).
    /// </summary>
    public static readonly IReadOnlySet<string> FreeMailDomains = new HashSet<string>(
        [
            "gmail.com", "googlemail.com", "outlook.com", "outlook.de", "hotmail.com", "hotmail.de", "live.com", "live.de", "msn.com",
            "yahoo.com", "yahoo.de", "icloud.com", "me.com", "mac.com", "aol.com", "gmx.de", "gmx.net", "gmx.at", "gmx.ch", "web.de",
            "t-online.de", "freenet.de", "posteo.de", "mailbox.org", "proton.me", "protonmail.com", "mail.ru", "yandex.ru", "zoho.com",
        ],
        StringComparer.Ordinal);

    /// <summary>
    /// Why a free-mail domain cannot be a trusted forwarder: every user of the provider could then forward a mail
    /// naming any original sender, and it would count as that sender authenticated (docs/11 §6.5 and §7.1).
    /// </summary>
    public static string FreeMailForwarderMessage(string domain) =>
        $"{domain} is a free-mail provider: trusting it as a forwarder would let every private sender there forward a mail in any supplier's name. Name the forwarding company's own domain.";

    /// <summary>An e-mail address in its compared form, or null for none. The display name and angle brackets are removed.</summary>
    public static string? Address(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var value = address.Trim();
        var open = value.LastIndexOf('<');
        var close = value.LastIndexOf('>');
        if (open >= 0 && close > open)
        {
            value = value[(open + 1)..close];
        }

        value = value.Trim().ToLowerInvariant();
        return value.Length == 0 ? null : value;
    }

    /// <summary>The domain of an address (after the last <c>@</c>), or null.</summary>
    public static string? DomainOf(string? address)
    {
        var normal = Address(address);
        var at = normal?.LastIndexOf('@') ?? -1;
        return at < 0 || at == normal!.Length - 1 ? null : normal[(at + 1)..];
    }

    /// <summary>A domain in its compared form, or null.</summary>
    public static string? Domain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return null;
        }

        var value = domain.Trim().TrimStart('@').TrimEnd('.').ToLowerInvariant();
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// A folder path or prefix in its compared form, or null when it has a <c>..</c> or <c>.</c> segment (a path that
    /// leaves or names its folder in another way is never a routing fact). The root is the empty string.
    /// </summary>
    public static string? Folder(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Any(s => s is "." or ".."))
        {
            return null;
        }

        return segments.Length == 0 ? string.Empty : string.Join('/', segments) + "/";
    }

    /// <summary>Whether <paramref name="folderPath"/> (normalised) lies under <paramref name="prefix"/> (normalised); case-insensitive.</summary>
    public static bool UnderPrefix(string folderPath, string prefix) =>
        folderPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The number of segments of a normalised folder prefix (<c>a/b/</c> has 2), part of a folder rule's specificity.</summary>
    public static int Depth(string prefix) => prefix.Count(c => c == '/');

    /// <summary>
    /// The hex SHA-256 of a rule's normalised conditions (<c>SupplierSourceRules.ConditionHash</c>), unique per tenant:
    /// two rules with the same conditions are the same rule, whichever supplier they name.
    /// </summary>
    public static string Hash(Guid? channelId, string? recipient, string? sender, string? senderDomain, string? folderPrefix, string? subjectRegex, string? fileNameRegex)
    {
        var canonical = string.Join(
            "\u001f",
            channelId?.ToString("N") ?? string.Empty,
            recipient ?? string.Empty,
            sender ?? string.Empty,
            senderDomain ?? string.Empty,
            folderPrefix?.ToLowerInvariant() ?? "\u0000",
            subjectRegex ?? string.Empty,
            fileNameRegex ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
