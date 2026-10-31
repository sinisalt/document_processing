using System.Net;
using System.Net.Sockets;

namespace DocProc.Llm.Providers;

/// <summary>
/// Where a tenant-scope provider configuration may send requests (docs/08 "Endpoint restrictions"). A tenant chooses
/// the endpoint and the platform's worker sends the tenant's key and document content to it, so the endpoint must be a
/// public <c>https</c> host: loopback, private (RFC 1918, CGNAT, ULA), link-local (cloud metadata at 169.254.169.254
/// included), unspecified, multicast and other reserved addresses are refused, IPv4 and IPv6 alike, also when spelled
/// as an IPv4-mapped IPv6 address or in decimal, octal or hexadecimal. <see cref="Validate(Uri)"/> checks the URL when the
/// configuration is created; <see cref="HttpClient"/> checks every connection again after DNS resolution and connects
/// to the vetted address itself, so a name that resolves elsewhere later (DNS rebinding) cannot get around it.
/// Platform configurations, which only a SuperAdmin creates, are not restricted (self-hosted models).
/// </summary>
public static class TenantEndpointGuard
{
    /// <summary>
    /// What a blocked call reports. Deliberately the same for every refused address and free of anything the target
    /// said: nothing is sent to a blocked host, and the tenant learns neither its address nor whether it answers.
    /// </summary>
    public const string BlockedMessage =
        "The provider endpoint is not allowed: tenant provider configurations must use https to a public host "
        + "(loopback, private, link-local and other reserved addresses are refused).";

    private static readonly string[] ReservedSuffixes = [".localhost", ".local", ".internal", ".localdomain", ".home.arpa"];

    private static readonly Lazy<HttpClient> SharedClient = new(() => CreateClient());

    /// <summary>
    /// The client tenant-scope providers use: https only, no proxy, no redirects, and a connect step that resolves the
    /// host, refuses it when any of its addresses is blocked and connects to the addresses it checked.
    /// </summary>
    public static HttpClient HttpClient => SharedClient.Value;

    /// <summary>Why <paramref name="endpoint"/> may not serve a tenant configuration, or null when it may.</summary>
    public static string? Validate(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? Validate(uri) : "endpoint must be an absolute https URL.";

    /// <summary>Why <paramref name="endpoint"/> may not serve a tenant configuration, or null when it may.</summary>
    public static string? Validate(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return "endpoint must use https for a tenant provider configuration.";
        }

        if (endpoint.UserInfo.Length > 0)
        {
            return "endpoint must not carry credentials in the URL.";
        }

        var host = Unbracket(endpoint.IdnHost).TrimEnd('.').ToLowerInvariant();
        if (endpoint.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 || IPAddress.TryParse(host, out _))
        {
            return IPAddress.TryParse(host, out var address) && !IsBlocked(address)
                ? null
                : "endpoint must not be a loopback, private, link-local or otherwise reserved address.";
        }

        if (endpoint.HostNameType != UriHostNameType.Dns || host.Length == 0)
        {
            return "endpoint must name a public host.";
        }

        var lastLabel = host[(host.LastIndexOf('.') + 1)..];
        if (!host.Contains('.', StringComparison.Ordinal) || host == "localhost" || ReservedSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal)))
        {
            return "endpoint must name a public host, not a local or internal name.";
        }

        // No top-level domain is numeric: such a name is an address spelling the resolver would turn into an IP.
        if (lastLabel.All(char.IsAsciiDigit) || lastLabel.StartsWith("0x", StringComparison.Ordinal))
        {
            return "endpoint must not be a loopback, private, link-local or otherwise reserved address.";
        }

        return null;
    }

    /// <summary>Whether a tenant configuration may not connect to <paramref name="address"/>.</summary>
    public static bool IsBlocked(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var b = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsBlockedV4(b),
            AddressFamily.InterNetworkV6 => IsBlockedV6(b),
            _ => true,
        };
    }

    /// <summary>The <see cref="BlockedEndpointException"/> in <paramref name="exception"/>'s chain, or null when it was not a refusal.</summary>
    public static BlockedEndpointException? FindBlocked(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is BlockedEndpointException blocked)
            {
                return blocked;
            }
        }

        return null;
    }

    /// <summary>The refusal as the error every adapter reports: the configuration is unusable until the tenant fixes it.</summary>
    public static LlmException ToLlmException(BlockedEndpointException blocked) =>
        new(LlmErrorKind.Unavailable, BlockedMessage, inner: blocked);

    /// <summary>
    /// Throws when <paramref name="config"/> is a tenant configuration whose endpoint <see cref="Validate(Uri)"/> refuses
    /// (a row created before the check existed); the connect step would refuse it anyway.
    /// </summary>
    public static void EnsureAllowed(LlmProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Scope == ConfigScope.Tenant && config.Endpoint is { } endpoint && Validate(endpoint) is not null)
        {
            throw ToLlmException(new BlockedEndpointException());
        }
    }

    /// <summary>
    /// A client like <see cref="HttpClient"/>; <paramref name="resolve"/> replaces DNS (tests that need a name to
    /// resolve to a given address), null uses <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>.
    /// </summary>
    public static HttpClient CreateClient(Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        resolve ??= Dns.GetHostAddressesAsync;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = (context, cancellationToken) => ConnectAsync(context, resolve, cancellationToken),
            UseProxy = false, // a proxy would connect on our behalf, after the check
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        // The adapters time every request themselves; HttpClient's own 100 s default would cut long calls short.
        return new HttpClient(new HttpsOnlyHandler(handler)) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken cancellationToken)
    {
        var host = Unbracket(context.DnsEndPoint.Host);
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await resolve(host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(IsBlocked))
        {
            throw new BlockedEndpointException();
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            // The addresses just checked, not the name: a second resolution could answer differently.
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static string Unbracket(string host) =>
        host.Length > 1 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;

    private static bool IsBlockedV4(byte[] b) => b[0] switch
    {
        0 or 10 or 127 => true, // "this network", private, loopback
        100 => b[1] is >= 64 and <= 127, // shared address space (CGNAT)
        169 => b[1] == 254, // link-local, cloud metadata
        172 => b[1] is >= 16 and <= 31, // private
        192 => (b[1] == 0 && b[2] is 0 or 2) || (b[1] == 88 && b[2] == 99) || b[1] == 168, // IETF, TEST-NET-1, 6to4 relay, private
        198 => b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100), // benchmarking, TEST-NET-2
        203 => b[1] == 0 && b[2] == 113, // TEST-NET-3
        >= 224 => true, // multicast, reserved, broadcast
        _ => false,
    };

    /// <summary>Only global unicast (2000::/3) is allowed, without the ranges in it that embed or reach other addresses.</summary>
    private static bool IsBlockedV6(byte[] b)
    {
        if ((b[0] & 0xE0) != 0x20)
        {
            return true; // ::, ::1, IPv4-compatible, NAT64 (64:ff9b::/96), discard, ULA, link-local, site-local, multicast
        }

        var first = (b[0] << 8) | b[1];
        var second = (b[2] << 8) | b[3];
        return first switch
        {
            0x2001 => second == 0x0db8 || second < 0x0200, // documentation; IETF protocol assignments, Teredo among them
            0x2002 => IsBlockedV4([b[2], b[3], b[4], b[5]]), // 6to4 embeds the IPv4 address it reaches
            0x3fff => true, // documentation (RFC 9637)
            _ => false,
        };
    }

    /// <summary>Refuses any request that is not https before a connection is made.</summary>
    private sealed class HttpsOnlyHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri is { } uri && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? base.SendAsync(request, cancellationToken)
                : throw new BlockedEndpointException();

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri is { } uri && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? base.Send(request, cancellationToken)
                : throw new BlockedEndpointException();
    }
}

/// <summary>A tenant provider call refused by <see cref="TenantEndpointGuard"/>; nothing was sent to the host.</summary>
public sealed class BlockedEndpointException : HttpRequestException
{
    public BlockedEndpointException()
        : base(HttpRequestError.ConnectionError, TenantEndpointGuard.BlockedMessage)
    {
    }

    public BlockedEndpointException(string message)
        : base(message)
    {
    }

    public BlockedEndpointException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
