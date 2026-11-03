using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
/// <item><description><c>http</c>: <c>{method (POST, PUT, PATCH), urlTemplate (absolute http(s), <c>{{...}}</c> placeholders), headers {name: value}, timeoutSeconds (1-120), successCodes [200-299...], auth {type, headerName?, username?, tokenUrl?, clientId?, scope?}, responseMapping? (JSONata)}</c>;</description></item>
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

    /// <summary>Header names a config may not carry: credentials go to the destination's secret and the auth block.</summary>
    private static readonly HashSet<string> CredentialHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "X-Api-Key", "Api-Key", "X-Auth-Token",
        "Idempotency-Key", "X-DocProc-Document-Id", "X-DocProc-Revision", "Host", "Content-Length", "Transfer-Encoding",
    };

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

    /// <summary>The auth type of an http config (<c>none</c> when it has none).</summary>
    public static string AuthTypeOf(JsonObject config) => config["auth"]?["type"] is JsonValue value && value.TryGetValue<string>(out var type) ? type : HttpAuthTypes.None;

    private static void Http(JsonObject config, List<(string, string)> problems)
    {
        Known(config, ["method", "urlTemplate", "headers", "timeoutSeconds", "successCodes", "auth", "responseMapping"], problems);
        if (Text(config["method"]) is { } method && !HttpMethods.Contains(method))
        {
            problems.Add(("config.method", "method is POST, PUT or PATCH."));
        }

        if (Text(config["urlTemplate"]) is not { Length: > 0 } url || url.Length > MaxTemplateLength)
        {
            problems.Add(("config.urlTemplate", $"urlTemplate is required: an absolute http or https URL of at most {MaxTemplateLength} characters ({{{{...}}}} placeholders allowed)."));
        }
        else if (!Uri.TryCreate(Placeholder().Replace(url, "x"), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo))
        {
            problems.Add(("config.urlTemplate", "urlTemplate is an absolute http or https URL without user name or password (credentials go to the secret)."));
        }

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
                    else if (CredentialHeaders.Contains(name))
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
