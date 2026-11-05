using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocProc.Application.Routing;

/// <summary>
/// What the receiving mail server said about a message's sender (docs/11 §6.3 <c>SenderAuthJson</c>): the SPF, DKIM
/// and DMARC results of the trusted <c>Authentication-Results</c> header (<c>pass</c>, <c>fail</c>, <c>none</c>, ...),
/// the domains that signed with DKIM, and the From domain they are checked against. A sender is
/// <see cref="Verified"/> on DMARC pass, or on SPF pass together with a DKIM pass from a domain aligned with the From
/// domain (docs/11 §7.2 step 2).
/// <para>
/// A forward unwrapped from a trusted forwarder (<see cref="Via"/> <c>trustedForwarder</c>, docs/11 §7.1) carries the
/// forwarder's own results in <see cref="Forwarder"/>; the original sender then counts as verified when the forwarder
/// does: the original's signatures do not survive a forward, and trusting the forwarder's domain is exactly what
/// <c>trustedForwarderDomains</c> says.
/// </para>
/// </summary>
public sealed record SenderAuthentication
{
    public const string Pass = "pass";
    public const string ViaTrustedForwarder = "trustedForwarder";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string? Spf { get; init; }

    public string? Dkim { get; init; }

    public string? Dmarc { get; init; }

    /// <summary>The <c>header.d</c> domains of the DKIM results that passed, lower-cased.</summary>
    public IReadOnlyList<string>? DkimDomains { get; init; }

    /// <summary>The domain of the From address the results are about, lower-cased.</summary>
    public string? FromDomain { get; init; }

    /// <summary>The <c>authserv-id</c> of the header the results were read from.</summary>
    public string? AuthServId { get; init; }

    /// <summary><c>trustedForwarder</c> when the sender is the original of a forward, else null.</summary>
    public string? Via { get; init; }

    /// <summary>The forwarder's own results, for an unwrapped forward.</summary>
    public SenderAuthentication? Forwarder { get; init; }

    /// <summary>No header could be trusted, or none was present: nothing is known, so nothing is verified.</summary>
    public static SenderAuthentication None(string? fromDomain) => new() { FromDomain = fromDomain };

    /// <summary>Whether the sender is authenticated (see the class summary).</summary>
    [JsonIgnore]
    public bool Verified => Via == ViaTrustedForwarder
        ? Forwarder?.Verified == true
        : Is(Dmarc, Pass) || (Is(Spf, Pass) && Is(Dkim, Pass) && FromDomain is { } from && (DkimDomains ?? []).Any(d => Aligned(d, from)));

    /// <summary>Relaxed alignment: the signing domain is the From domain, or one is a subdomain of the other.</summary>
    public static bool Aligned(string signingDomain, string fromDomain)
    {
        ArgumentNullException.ThrowIfNull(signingDomain);
        ArgumentNullException.ThrowIfNull(fromDomain);
        var d = signingDomain.Trim().TrimEnd('.').ToLowerInvariant();
        var f = fromDomain.Trim().TrimEnd('.').ToLowerInvariant();
        return d.Length > 0 && (d == f || f.EndsWith("." + d, StringComparison.Ordinal) || d.EndsWith("." + f, StringComparison.Ordinal))
               && d.Contains('.', StringComparison.Ordinal);
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static SenderAuthentication? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SenderAuthentication>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
