using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocProc.Infrastructure.Outputs.Delivery;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Infrastructure.Outputs;

/// <summary>The authentication of an http destination (docs/11 §13.3); the secret itself is the destination's, never in the config.</summary>
public static class HttpAuthTypes
{
    public const string None = "none";
    public const string ApiKeyHeader = "apiKeyHeader";
    public const string Basic = "basic";
    public const string Bearer = "bearer";
    public const string OAuth2ClientCredentials = "oauth2ClientCredentials";
    public const string HmacSha256 = "hmacSha256";

    public static readonly IReadOnlyList<string> All = [None, ApiKeyHeader, Basic, Bearer, OAuth2ClientCredentials, HmacSha256];

    /// <summary>The types that need the destination's secret.</summary>
    public static bool NeedsSecret(string type) => type != None;
}

/// <summary>
/// The configuration of a destination per transport (docs/11 §13.3), checked when it is saved: what each transport
/// takes, with clear errors, and nothing that looks like a credential (the secret is stored apart, write-only).
/// <list type="bullet">
/// <item><description><c>http</c>: <c>{method (POST, PUT, PATCH), urlTemplate (absolute http(s), <c>{{...}}</c> placeholders), updateMethod?, updateUrlTemplate? (what a re-approval under <c>OnReapproval: Update</c> sends with; default PUT and urlTemplate), headers {name: value}, timeoutSeconds (1-120), successCodes [200-299...], auth {type, headerName?, username?, tokenUrl?, clientId?, scope?}, responseMapping? (JSONata), multipart? {parts}}</c>;</description></item>
/// <item><description><c>blob</c>: <c>{path (relative, no <c>..</c>), fileNameTemplate}</c>;</description></item>
/// <item><description><c>pull</c>: <c>{retentionDays?}</c>.</description></item>
/// </list>
/// The outbound checks of <c>TenantEndpointGuard</c> (https, public addresses) run when a delivery is sent (P3 part B):
/// a saved URL is not resolved.
/// </summary>
public static partial class DestinationConfig
{
    public const int MaxConfigLength = 32 * 1024;
    public const int MaxHeaders = 20;
    public const int MaxTemplateLength = 2000;

    /// <summary>Header names a config's <c>headers</c> may not carry because they are credentials: those go to the destination's secret and the auth block.</summary>
    private static readonly HashSet<string> CredentialHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "X-Api-Key", "Api-Key", "X-Auth-Token",
    };

    /// <summary>
    /// Header names the transport sets or that frame the request, which neither <c>headers</c> nor the auth's
    /// <c>headerName</c> may carry: the delivery's own headers (<c>Idempotency-Key</c>, every <c>X-DocProc-*</c>), the
    /// host (another virtual host behind the same address), the body's framing and the hop-by-hop headers.
    /// </summary>
    private static readonly HashSet<string> TransportHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Content-Type", "Content-Encoding", "Transfer-Encoding", "Connection", "Keep-Alive", "TE", "Trailer", "Upgrade",
        "Expect", "Proxy-Connection", "Proxy-Authorization", "Idempotency-Key",
    };

    /// <summary>Whether the transport reserves <paramref name="name"/> (<see cref="TransportHeaders"/> and the <c>X-DocProc-</c> prefix).</summary>
    public static bool IsReservedHeader(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return TransportHeaders.Contains(name) || name.StartsWith("X-DocProc-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The auth types whose secret travels as a header value as it is (<c>apiKeyHeader</c>, <c>bearer</c>).</summary>
    public static bool SendsSecretInHeader(string authType) => authType is HttpAuthTypes.ApiKeyHeader or HttpAuthTypes.Bearer;

    /// <summary>
    /// What is wrong with <paramref name="secret"/> for <paramref name="authType"/>, or null. No secret may hold a line
    /// break or NUL (a header value with CR or LF would add header lines of its own); one that travels in a header as it
    /// is (<see cref="SendsSecretInHeader"/>) is printable ASCII (space and tab allowed). <c>basic</c> sends it base64,
    /// <c>oauth2ClientCredentials</c> form-encoded and <c>hmacSha256</c> never, so any other text is fine there.
    /// </summary>
    public static string? SecretProblem(string authType, string secret)
    {
        ArgumentNullException.ThrowIfNull(authType);
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
        {
            return "The secret has a line break or a NUL character.";
        }

        return SendsSecretInHeader(authType) && secret.Any(c => c != '\t' && (c < ' ' || c > '~'))
            ? $"The secret of auth '{authType}' is sent in a header as it is, so it is printable ASCII."
            : null;
    }

    private static readonly HashSet<string> HttpMethods = new(StringComparer.Ordinal) { "POST", "PUT", "PATCH" };

    /// <summary>The problems of <paramref name="config"/> for <paramref name="transport"/>, as (field, message); empty when it is fine.</summary>
    public static IReadOnlyList<(string Field, string Message)> Validate(string transport, JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var problems = new List<(string, string)>();
        if (config.ToJsonString().Length > MaxConfigLength)
        {
            problems.Add(("config", $"The config has more than {MaxConfigLength} characters."));
            return problems;
        }

        switch (transport)
        {
            case OutputTransports.Http:
                Http(config, problems);
                break;
            case OutputTransports.Blob:
                Blob(config, problems);
                break;
            case OutputTransports.Pull:
                Known(config, ["retentionDays"], problems);
                if (config["retentionDays"] is { } days && !(Int(days) is >= 1 and <= 365))
                {
                    problems.Add(("config.retentionDays", "retentionDays is a whole number from 1 to 365."));
                }

                break;
            case OutputTransports.Sftp or OutputTransports.Email:
                problems.Add(("transport", $"The '{transport}' transport comes in a later phase (P6); this build has {string.Join(", ", OutputTransports.Available)}."));
                break;
            default:
                problems.Add(("transport", $"Unknown transport '{transport}'; this build has {string.Join(", ", OutputTransports.Available)}."));
                break;
        }

        return problems;
    }

    /// <summary>
    /// What in a change of an http config from <paramref name="before"/> to <paramref name="after"/> would send the
    /// stored secret somewhere it was not set up for: another origin (scheme, host and port) of <c>urlTemplate</c>, of
    /// the URL a re-approval sends to (<c>updateUrlTemplate</c>, else <c>urlTemplate</c>) or of the OAuth
    /// <c>tokenUrl</c>, another <c>auth.type</c>, or another <c>headerName</c>. Empty when the new auth sends no secret
    /// (<c>none</c>, and <c>hmacSha256</c>, whose key never leaves) or nothing of that changed. Such a change needs the
    /// secret again (docs/05, <c>PATCH /destinations/{id}</c>): otherwise anyone who may edit the destination could
    /// point it at a host of their own and read the credential set up for the real receiver.
    /// </summary>
    public static IReadOnlyList<string> SecretRedirects(JsonObject before, JsonObject after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var type = AuthTypeOf(after);
        if (type is HttpAuthTypes.None or HttpAuthTypes.HmacSha256)
        {
            return [];
        }

        var changes = new List<string>();
        if (AuthTypeOf(before) != type)
        {
            changes.Add("auth.type");
        }

        if (!SameOrigin(Text(before["urlTemplate"]), Text(after["urlTemplate"])))
        {
            changes.Add("the host of urlTemplate");
        }

        if (!SameOrigin(Text(before["updateUrlTemplate"]) ?? Text(before["urlTemplate"]), Text(after["updateUrlTemplate"]) ?? Text(after["urlTemplate"])))
        {
            changes.Add("the host of updateUrlTemplate");
        }

        if (type == HttpAuthTypes.OAuth2ClientCredentials && !SameOrigin(Text(before["auth"]?["tokenUrl"]), Text(after["auth"]?["tokenUrl"])))
        {
            changes.Add("the host of auth.tokenUrl");
        }

        if (type == HttpAuthTypes.ApiKeyHeader && !string.Equals(Text(before["auth"]?["headerName"]), Text(after["auth"]?["headerName"]), StringComparison.OrdinalIgnoreCase))
        {
            changes.Add("auth.headerName");
        }

        return changes;
    }

    /// <summary>Whether two URL templates name the same origin: the text before the path (<c>scheme://host:port</c>), case-insensitive, with the scheme's default port written or not.</summary>
    private static bool SameOrigin(string? before, string? after) => string.Equals(Origin(before), Origin(after), StringComparison.Ordinal);

    private static string? Origin(string? template)
    {
        if (template is null)
        {
            return null;
        }

        var start = template.IndexOf("://", StringComparison.Ordinal);
        if (start < 0)
        {
            return template.Trim().ToLowerInvariant();
        }

        var end = template.IndexOfAny(['/', '?', '#'], start + 3);
        var origin = (end < 0 ? template : template[..end]).Trim().ToLowerInvariant();
        return origin.EndsWith(":443", StringComparison.Ordinal) && origin.StartsWith("https://", StringComparison.Ordinal) ? origin[..^4]
            : origin.EndsWith(":80", StringComparison.Ordinal) && origin.StartsWith("http://", StringComparison.Ordinal) ? origin[..^3]
            : origin;
    }

    /// <summary>The auth type of an http config (<c>none</c> when it has none).</summary>
    public static string AuthTypeOf(JsonObject config) => config["auth"]?["type"] is JsonValue value && value.TryGetValue<string>(out var type) ? type : HttpAuthTypes.None;

    private static void Http(JsonObject config, List<(string, string)> problems)
    {
        Known(config, ["method", "urlTemplate", "updateMethod", "updateUrlTemplate", "headers", "timeoutSeconds", "successCodes", "auth", "responseMapping", "multipart"], problems);
        if (Text(config["method"]) is { } method && !HttpMethods.Contains(method))
        {
            problems.Add(("config.method", "method is POST, PUT or PATCH."));
        }

        if (config["updateMethod"] is { } updateMethod && (Text(updateMethod) is not { } update || !HttpMethods.Contains(update)))
        {
            problems.Add(("config.updateMethod", "updateMethod (what a re-approval under OnReapproval Update sends with) is POST, PUT or PATCH."));
        }

        Url(config, "urlTemplate", required: true, problems);
        Url(config, "updateUrlTemplate", required: false, problems);
        Multipart(config, problems);

        if (config["headers"] is { } headers)
        {
            if (headers is not JsonObject list || list.Count > MaxHeaders)
            {
                problems.Add(("config.headers", $"headers is an object of at most {MaxHeaders} names and string values."));
            }
            else
            {
                foreach (var (name, value) in list)
                {
                    if (!HeaderName().IsMatch(name) || Text(value) is not { } text || text.Length > MaxTemplateLength || text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal))
                    {
                        problems.Add(($"config.headers.{name}", "A header is a token name with a single-line string value."));
                    }
                    else if (CredentialHeaders.Contains(name) || IsReservedHeader(name))
                    {
                        problems.Add(($"config.headers.{name}", $"'{name}' is set by the transport or is a credential; credentials go to the destination's secret and auth."));
                    }
                }
            }
        }

        if (config["timeoutSeconds"] is { } timeout && !(Int(timeout) is >= 1 and <= 120))
        {
            problems.Add(("config.timeoutSeconds", "timeoutSeconds is a whole number from 1 to 120."));
        }

        if (config["successCodes"] is { } codes && (codes is not JsonArray array || array.Count is 0 or > 20 || array.Any(c => !(Int(c) is >= 200 and <= 299))))
        {
            problems.Add(("config.successCodes", "successCodes is a list of 1 to 20 status codes from 200 to 299."));
        }

        if (config["auth"] is { } auth)
        {
            if (auth is not JsonObject block)
            {
                problems.Add(("config.auth", "auth is an object {type, ...}."));
            }
            else
            {
                Known(block, ["type", "headerName", "username", "tokenUrl", "clientId", "scope"], problems, "config.auth");
                var type = Text(block["type"]);
                if (type is null || !HttpAuthTypes.All.Contains(type))
                {
                    problems.Add(("config.auth.type", $"auth.type is one of {string.Join(", ", HttpAuthTypes.All)}."));
                }
                else if (type == HttpAuthTypes.ApiKeyHeader && (Text(block["headerName"]) is not { } header || !HeaderName().IsMatch(header)))
                {
                    problems.Add(("config.auth.headerName", "apiKeyHeader needs headerName, the header the key is sent in."));
                }
                else if (type == HttpAuthTypes.ApiKeyHeader && IsReservedHeader(Text(block["headerName"])!))
                {
                    problems.Add(("config.auth.headerName", $"'{Text(block["headerName"])}' is set by the transport (Host, Idempotency-Key, X-DocProc-*, the body's framing, hop-by-hop headers); the key goes in a header of its own."));
                }
                else if (type == HttpAuthTypes.Basic && Text(block["username"]) is not { Length: > 0 and <= 200 })
                {
                    problems.Add(("config.auth.username", "basic needs the username (the password is the secret)."));
                }
                else if (type == HttpAuthTypes.OAuth2ClientCredentials
                    && (Text(block["tokenUrl"]) is not { } tokenUrl || !Uri.TryCreate(tokenUrl, UriKind.Absolute, out var token) || token.Scheme is not ("https" or "http") || Text(block["clientId"]) is not { Length: > 0 and <= 200 }))
                {
                    problems.Add(("config.auth", "oauth2ClientCredentials needs tokenUrl (absolute) and clientId (the client secret is the secret)."));
                }
            }
        }

        if (config["responseMapping"] is { } mapping && (Text(mapping) is not { Length: > 0 } expression || expression.Length > 8000))
        {
            problems.Add(("config.responseMapping", "responseMapping is a JSONata expression of at most 8000 characters."));
        }
    }

    private static void Url(JsonObject config, string name, bool required, List<(string, string)> problems)
    {
        if (config[name] is null && !required)
        {
            return;
        }

        if (Text(config[name]) is not { Length: > 0 } url || url.Length > MaxTemplateLength)
        {
            problems.Add(($"config.{name}", $"{name} is {(required ? "required: " : string.Empty)}an absolute http or https URL of at most {MaxTemplateLength} characters ({{{{...}}}} placeholders allowed)."));
        }
        else if (!Uri.TryCreate(Placeholder().Replace(url, "x"), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo))
        {
            problems.Add(($"config.{name}", $"{name} is an absolute http or https URL without user name or password (credentials go to the secret)."));
        }
        else if (Placeholder().Matches(url).Any(m => !DeliveryTemplates.IsKnownPlaceholder(m.Value)))
        {
            problems.Add(($"config.{name}", $"{name} has a placeholder this build does not know; placeholders are {DeliveryTemplates.Describe}."));
        }
    }

    /// <summary>
    /// <c>multipart: {parts: [{name, mapping: "self"} | {name, content: "original"}]}</c> (docs/11 §13.3): the body
    /// as multipart/form-data, with the rendered payload and the document's original file as parts.
    /// </summary>
    private static void Multipart(JsonObject config, List<(string, string)> problems)
    {
        if (config["multipart"] is not { } multipart)
        {
            return;
        }

        if (multipart is not JsonObject block || block["parts"] is not JsonArray parts || parts.Count is 0 or > 10 || block.Count != 1)
        {
            problems.Add(("config.multipart", "multipart is {parts: [...]} with 1 to 10 parts."));
            return;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < parts.Count; i++)
        {
            var where = $"config.multipart.parts[{i}]";
            if (parts[i] is not JsonObject part || Text(part["name"]) is not { } name || !HeaderName().IsMatch(name) || !names.Add(name))
            {
                problems.Add((where, "A part is an object with a unique token name."));
                continue;
            }

            var mapping = Text(part["mapping"]);
            var content = Text(part["content"]);
            var known = part.All(p => p.Key is "name" or "mapping" or "content");
            if (!known || (mapping, content) is not (("self", null) or (null, "original")))
            {
                problems.Add((where, "A part is {name, mapping: \"self\"} (the rendered payload) or {name, content: \"original\"} (the document's original file)."));
            }
        }

        if (!parts.OfType<JsonObject>().Any(p => Text(p["mapping"]) == "self"))
        {
            problems.Add(("config.multipart.parts", "One part carries the rendered payload ({mapping: \"self\"})."));
        }
    }

    private static void Blob(JsonObject config, List<(string, string)> problems)
    {
        Known(config, ["path", "fileNameTemplate"], problems);
        if (Text(config["path"]) is not { } path || path.Length > 400 || path.StartsWith('/') || path.Contains('\\', StringComparison.Ordinal) || path.Split('/').Any(s => s is ".." or "."))
        {
            problems.Add(("config.path", "path is a relative folder of at most 400 characters, with '/' between its parts and no '.' or '..'."));
        }

        if (Text(config["fileNameTemplate"]) is not { Length: > 0 and <= 400 } name || name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal))
        {
            problems.Add(("config.fileNameTemplate", "fileNameTemplate is a file name of at most 400 characters without '/', '\\' or '..' ({{...}} placeholders allowed)."));
        }
        else if (Placeholder().Matches(name).Any(m => !DeliveryTemplates.IsKnownPlaceholder(m.Value)))
        {
            problems.Add(("config.fileNameTemplate", $"fileNameTemplate has a placeholder this build does not know; placeholders are {DeliveryTemplates.Describe}."));
        }
    }

    private static void Known(JsonObject config, string[] names, List<(string, string)> problems, string where = "config")
    {
        foreach (var (name, _) in config)
        {
            if (!names.Contains(name, StringComparer.Ordinal))
            {
                problems.Add(($"{where}.{name}", $"Unknown setting '{name}'; this transport takes {string.Join(", ", names)}."));
            }
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static int? Int(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<int>(out var number) ? number : null;

    [GeneratedRegex(@"\{\{[^{}]*\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,100}$")]
    private static partial Regex HeaderName();
}
