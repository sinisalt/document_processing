using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Abstractions;
using DocProc.Application.Outputs;
using DocProc.Infrastructure.Http;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Infrastructure.Outputs.Delivery;

/// <summary>
/// The <c>http</c> transport of docs/11 §13.3: one request per delivery through <see cref="DeliveryHttpClient"/>.
/// <list type="bullet">
/// <item><description>Method and URL from the config (<c>method</c>, <c>urlTemplate</c>), or for a re-approval under
/// <c>OnReapproval: Update</c> (<c>meta.event = document.updated</c>) from <c>updateMethod</c> (default PUT) and
/// <c>updateUrlTemplate</c> (default the URL).</description></item>
/// <item><description>Headers: the config's, then <c>Idempotency-Key</c> (the delivery's key), <c>X-DocProc-Document-Id</c>
/// and <c>X-DocProc-Revision</c> (the document version rendered), then the auth.</description></item>
/// <item><description>Auth: <c>apiKeyHeader</c>, <c>basic</c>, <c>bearer</c>, <c>oauth2ClientCredentials</c> (the token is
/// cached until a minute before it expires, and dropped when the receiver answers 401) and <c>hmacSha256</c>
/// (<c>X-DocProc-Timestamp</c>, and <c>X-DocProc-Signature: sha256=hex(HMAC-SHA256(secret, timestamp + "." + body))</c>
/// over the exact bytes sent).</description></item>
/// <item><description>The body: the payload, or multipart/form-data with the payload and the original file
/// (<c>multipart.parts</c>).</description></item>
/// <item><description><c>responseMapping</c>: a JSONata expression over the response body, in the renderer, whose
/// string or number becomes the document's external id at this destination.</description></item>
/// </list>
/// The response excerpt keeps 4 KB with the destination's secret and token masked.
/// </summary>
public sealed class HttpDeliveryTransport(
    DeliveryHttpClient http,
    ISecretStore secrets,
    OAuthTokenCache tokens,
    IOutputRenderer renderer,
    TimeProvider clock) : IDeliveryTransport
{
    /// <summary>The most of a response read (for the excerpt and the response mapping).</summary>
    public const int MaxResponseBytes = 256 * 1024;

    public const int DefaultTimeoutSeconds = 30;

    public string Transport => OutputTransports.Http;

    public async Task<DeliveryOutcome> SendAsync(DeliveryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var watch = Stopwatch.StartNew();
        var outcome = await SendCoreAsync(context, cancellationToken).ConfigureAwait(false);
        return outcome with { DurationMs = (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds) };
    }

    private async Task<DeliveryOutcome> SendCoreAsync(DeliveryContext context, CancellationToken cancellationToken)
    {
        var config = context.Config;
        var update = context.Delivery.Event == OutputEvents.Updated;
        var method = update ? Text(config, "updateMethod") ?? "PUT" : Text(config, "method") ?? "POST";
        var template = (update ? Text(config, "updateUrlTemplate") : null) ?? Text(config, "urlTemplate") ?? string.Empty;
        Uri uri;
        try
        {
            if (!Uri.TryCreate(DeliveryTemplates.ExpandUrl(template, context.Values), UriKind.Absolute, out var parsed))
            {
                return DeliveryOutcome.Permanent("url", "The destination's URL template does not give an absolute URL.");
            }

            uri = parsed;
        }
        catch (DeliveryTemplateException exception)
        {
            return DeliveryOutcome.Permanent("template", exception.Message);
        }

        if (http.Refusal(uri) is { } refusal)
        {
            return DeliveryOutcome.Permanent("blocked", refusal);
        }

        var auth = config["auth"] as JsonObject;
        var authType = DestinationConfig.AuthTypeOf(config);
        string? secret = null;
        if (HttpAuthTypes.NeedsSecret(authType))
        {
            secret = context.Destination.SecretRef is { } reference ? await secrets.GetAsync(reference, cancellationToken).ConfigureAwait(false) : null;
            if (string.IsNullOrEmpty(secret))
            {
                return DeliveryOutcome.Permanent("secret", $"The destination's auth '{authType}' needs its secret, and none is set (PUT /destinations/{{id}}/secret).");
            }
        }

        byte[] body;
        string contentType;
        try
        {
            (body, contentType) = await BodyAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (DeliveryTemplateException exception)
        {
            return DeliveryOutcome.Permanent("original", exception.Message);
        }

        string? token = null;
        if (authType == HttpAuthTypes.OAuth2ClientCredentials)
        {
            var obtained = await tokens.GetAsync(context.Destination, auth!, secret!, cancellationToken).ConfigureAwait(false);
            if (obtained.Failure is { } failure)
            {
                return failure;
            }

            token = obtained.Token;
        }

        var timeout = TimeSpan.FromSeconds(Int(config, "timeoutSeconds") ?? DefaultTimeoutSeconds);
        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        request.Content = content;
        if (config["headers"] is JsonObject headers)
        {
            foreach (var (name, value) in headers)
            {
                if (value is JsonValue text && text.TryGetValue<string>(out var headerValue) && !request.Headers.TryAddWithoutValidation(name, headerValue))
                {
                    request.Content.Headers.TryAddWithoutValidation(name, headerValue);
                }
            }
        }

        request.Headers.TryAddWithoutValidation("Idempotency-Key", context.Delivery.IdempotencyKey);
        request.Headers.TryAddWithoutValidation("X-DocProc-Document-Id", context.Delivery.DocumentId.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-DocProc-Revision", context.Delivery.DocumentVersion.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-DocProc-Event", context.Delivery.Event);
        Authorize(request, authType, auth, secret, token, body);

        HttpResponseMessage response;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        try
        {
            response = await http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DeliveryOutcome.Retryable("timeout", $"The receiver did not answer within {timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException exception) when (TenantEndpointGuard.FindBlocked(exception) is not null)
        {
            return DeliveryOutcome.Permanent("blocked", "The destination URL is not allowed: deliveries go over https to a public host (loopback, private, link-local and other reserved addresses are refused).");
        }
        catch (HttpRequestException exception)
        {
            return DeliveryOutcome.Retryable("network", $"{exception.HttpRequestError}: {exception.Message}");
        }

        using (response)
        {
            byte[] responseBytes;
            try
            {
                responseBytes = await ReadBoundedAsync(response.Content, timer.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                responseBytes = [];
            }

            var status = (int)response.StatusCode;
            var excerpt = Excerpt(responseBytes, secret, token);
            if (status == 401 && token is not null)
            {
                tokens.Forget(context.Destination);
            }

            var successCodes = config["successCodes"] is JsonArray codes ? codes.Select(c => c?.GetValue<int>() ?? 0).ToList() : null;
            var kind = DeliveryClassifier.Classify(status, successCodes);
            if (kind == DeliveryOutcomeKind.Delivered)
            {
                var delivered = DeliveryOutcome.Delivered(status, excerpt);
                if (Text(config, "responseMapping") is { } mapping)
                {
                    var (externalId, note) = await ExternalIdAsync(mapping, responseBytes, cancellationToken).ConfigureAwait(false);
                    delivered = delivered with { ExternalId = externalId, Note = note };
                }

                return delivered;
            }

            if (kind == DeliveryOutcomeKind.Retryable)
            {
                // A 401 after a token was used: the cached token is gone, and the next attempt asks for a new one.
                return DeliveryOutcome.Retryable("status", $"The receiver answered {status}.", status, excerpt) with { RetryAfter = RetryAfter(response) };
            }

            return status == 401 && token is not null
                ? DeliveryOutcome.Retryable("token", "The receiver refused the cached token (401); a new one is asked for.", status, excerpt)
                : DeliveryOutcome.Permanent("status", $"The receiver answered {status}.", status, excerpt);
        }
    }

    private static async Task<(byte[] Body, string ContentType)> BodyAsync(DeliveryContext context, CancellationToken cancellationToken)
    {
        if (context.Config["multipart"]?["parts"] is not JsonArray parts)
        {
            return (context.Payload.ToArray(), context.ContentType);
        }

        using var multipart = new MultipartFormDataContent("docproc-" + context.Delivery.Id.ToString("N"));
        foreach (var part in parts.OfType<JsonObject>())
        {
            var name = part["name"]!.GetValue<string>();
            if (part["mapping"] is not null)
            {
                var payload = new ReadOnlyMemoryContent(context.Payload);
                payload.Headers.ContentType = MediaTypeHeaderValue.Parse(context.ContentType);
                multipart.Add(payload, name, name + Extension(context.ContentType));
                continue;
            }

            var original = await context.Original(cancellationToken).ConfigureAwait(false)
                ?? throw new DeliveryTemplateException("The document's original file is not available for the multipart part '" + name + "'.");
            var file = new ReadOnlyMemoryContent(original.Bytes);
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(original.ContentType);
            multipart.Add(file, name, original.FileName);
        }

        var bytes = await multipart.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return (bytes, multipart.Headers.ContentType!.ToString());
    }

    private void Authorize(HttpRequestMessage request, string authType, JsonObject? auth, string? secret, string? token, byte[] body)
    {
        switch (authType)
        {
            case HttpAuthTypes.ApiKeyHeader:
                request.Headers.TryAddWithoutValidation(auth!["headerName"]!.GetValue<string>(), secret);
                break;
            case HttpAuthTypes.Basic:
                var pair = Encoding.UTF8.GetBytes($"{auth!["username"]!.GetValue<string>()}:{secret}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(pair));
                break;
            case HttpAuthTypes.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                break;
            case HttpAuthTypes.OAuth2ClientCredentials:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                break;
            case HttpAuthTypes.HmacSha256:
                var timestamp = clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                request.Headers.TryAddWithoutValidation("X-DocProc-Timestamp", timestamp);
                request.Headers.TryAddWithoutValidation("X-DocProc-Signature", "sha256=" + Signature(secret!, timestamp, body));
                break;
        }
    }

    /// <summary>The hex HMAC-SHA256 of <c>timestamp + "." + body</c> with the secret's UTF-8 bytes as the key.</summary>
    public static string Signature(string secret, string timestamp, byte[] body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentNullException.ThrowIfNull(body);
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message, prefix.Length);
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message));
    }

    private async Task<(string? ExternalId, string? Note)> ExternalIdAsync(string expression, byte[] responseBytes, CancellationToken cancellationToken)
    {
        JsonNode? body;
        try
        {
            body = responseBytes.Length == 0 ? null : JsonNode.Parse(responseBytes);
        }
        catch (JsonException)
        {
            return (null, "responseMapping: the response body is not JSON.");
        }

        var result = await renderer.RenderAsync(new RenderRequest(expression, body, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)), cancellationToken).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            return (null, $"responseMapping failed ({error.Kind}): {error.Message}");
        }

        var id = result.Output switch
        {
            JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
            JsonValue value when value.GetValueKind() == JsonValueKind.Number => value.ToJsonString(),
            _ => null,
        };
        return id is { Length: > 0 and <= 400 } ? (id, null) : (null, "responseMapping gave no string or number of at most 400 characters.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while (buffer.Length < MaxResponseBytes && (read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, MaxResponseBytes - buffer.Length)), cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>The first 4 KB of a response as text, with the secret and token masked.</summary>
    public static string? Excerpt(byte[] bytes, params string?[] masked)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, DeliveryAttempt.MaxExcerptLength * 2));
        foreach (var value in masked)
        {
            if (value is { Length: >= 4 })
            {
                text = text.Replace(value, "***", StringComparison.Ordinal);
            }
        }

        return text.Length > DeliveryAttempt.MaxExcerptLength ? text[..DeliveryAttempt.MaxExcerptLength] : text;
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } retryAfter)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta;
        }

        return retryAfter.Date is { } date ? date - DateTimeOffset.UtcNow : null;
    }

    private static string Extension(string contentType) => contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ? ".json" : string.Empty;

    private static string? Text(JsonObject config, string name) =>
        config[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static int? Int(JsonObject config, string name) =>
        config[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
}

/// <summary>
/// The OAuth 2.0 client-credentials tokens of http destinations (docs/11 §13.3, "token cached until expiry"): one per
/// destination and secret, asked for through <see cref="DeliveryHttpClient"/> at the config's <c>tokenUrl</c> and kept
/// until a minute before <c>expires_in</c> ends (five minutes when the answer has none).
/// </summary>
public sealed class OAuthTokenCache(DeliveryHttpClient http, TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid, (string SecretRef, string Token, DateTimeOffset ExpiresAt)> _tokens = new();

    public void Forget(OutputDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        _tokens.TryRemove(destination.Id, out _);
    }

    public async Task<(string? Token, DeliveryOutcome? Failure)> GetAsync(OutputDestination destination, JsonObject auth, string secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(auth);
        var now = clock.GetUtcNow();
        if (_tokens.TryGetValue(destination.Id, out var cached) && cached.SecretRef == destination.SecretRef && cached.ExpiresAt > now)
        {
            return (cached.Token, null);
        }

        var tokenUrl = auth["tokenUrl"]!.GetValue<string>();
        if (!Uri.TryCreate(tokenUrl, UriKind.Absolute, out var uri) || http.Refusal(uri) is not null)
        {
            return (null, DeliveryOutcome.Permanent("blocked", "The token URL is not allowed: https to a public host only."));
        }

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("client_id", auth["clientId"]!.GetValue<string>()),
            new("client_secret", secret),
        };
        if (auth["scope"] is JsonValue scope && scope.TryGetValue<string>(out var scopeText) && scopeText.Length > 0)
        {
            form.Add(new("scope", scopeText));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) };
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await http.Client.SendAsync(request, timer.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(timer.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var excerpt = HttpDeliveryTransport.Excerpt(Encoding.UTF8.GetBytes(text), secret);
                return (null, DeliveryClassifier.Classify(status, null) == DeliveryOutcomeKind.Retryable
                    ? DeliveryOutcome.Retryable("token", $"The token endpoint answered {status}.", status, excerpt)
                    : DeliveryOutcome.Permanent("token", $"The token endpoint answered {status} (check clientId and the secret).", status, excerpt));
            }

            var json = JsonNode.Parse(text) as JsonObject;
            if (json?["access_token"] is not JsonValue accessToken || !accessToken.TryGetValue<string>(out var token) || token.Length == 0)
            {
                return (null, DeliveryOutcome.Permanent("token", "The token endpoint's answer has no access_token."));
            }

            var lifetime = json["expires_in"] is JsonValue expires && expires.TryGetValue<int>(out var seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromMinutes(5);
            var margin = lifetime > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(1) : TimeSpan.Zero;
            _tokens[destination.Id] = (destination.SecretRef ?? string.Empty, token, now + lifetime - margin);
            return (token, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, DeliveryOutcome.Retryable("token", "The token endpoint did not answer within 30 s."));
        }
        catch (HttpRequestException exception) when (TenantEndpointGuard.FindBlocked(exception) is not null)
        {
            return (null, DeliveryOutcome.Permanent("blocked", "The token URL is not allowed: https to a public host only."));
        }
        catch (HttpRequestException exception)
        {
            return (null, DeliveryOutcome.Retryable("token", $"The token endpoint could not be reached: {exception.Message}"));
        }
        catch (JsonException)
        {
            return (null, DeliveryOutcome.Permanent("token", "The token endpoint's answer is not JSON."));
        }
    }
}
