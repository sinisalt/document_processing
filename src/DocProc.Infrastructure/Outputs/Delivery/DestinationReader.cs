using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Abstractions;
using DocProc.Infrastructure.Http;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Infrastructure.Outputs.Delivery;

/// <summary>The answer of a GET against a destination: the body, or why there is none.</summary>
public sealed record DestinationAnswer(ReadOnlyMemory<byte>? Body, int? Status, string? Problem)
{
    public bool Succeeded => Body is not null;
}

/// <summary>
/// A GET against a URL with an http destination's own headers and authentication (its secret from the secret store, an
/// OAuth token from the cache), through the delivery client: public addresses re-checked at connect, https, no
/// redirects, the development hosts only in Development (docs/11 §13.3). One attempt, bounded in time and size. Used by
/// the status poll (docs/11 §13.8) and the master-data sync (§14); a failure is an answer, never an exception.
/// </summary>
public sealed class DestinationReader(DeliveryHttpClient http, ISecretStore secrets, OAuthTokenCache tokens, TimeProvider clock)
{
    public async Task<DestinationAnswer> GetAsync(OutputDestination destination, Uri uri, TimeSpan timeout, int maxBytes, IReadOnlyDictionary<string, string>? extraHeaders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(uri);
        if (http.Refusal(uri) is { } refusal)
        {
            return new DestinationAnswer(null, null, $"The URL is not allowed: {refusal}");
        }

        JsonObject config;
        try
        {
            config = JsonNode.Parse(destination.ConfigJson) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            config = [];
        }

        var auth = config["auth"] as JsonObject;
        var authType = destination.Transport == OutputTransports.Http ? DestinationConfig.AuthTypeOf(config) : HttpAuthTypes.None;
        string? secret = null;
        if (HttpAuthTypes.NeedsSecret(authType))
        {
            secret = destination.SecretRef is { } reference ? await secrets.GetAsync(reference, cancellationToken).ConfigureAwait(false) : null;
            if (string.IsNullOrEmpty(secret))
            {
                return new DestinationAnswer(null, null, $"The destination's auth '{authType}' needs its secret, and none is set.");
            }
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        string? token = null;
        if (authType == HttpAuthTypes.OAuth2ClientCredentials)
        {
            var obtained = await tokens.GetAsync(destination, auth!, secret!, timer.Token, cancellationToken).ConfigureAwait(false);
            if (obtained.Failure is { } failure)
            {
                return new DestinationAnswer(null, null, $"The token endpoint failed ({failure.ErrorClass}).");
            }

            token = obtained.Token;
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        try
        {
            if (config["headers"] is JsonObject headers)
            {
                foreach (var (name, value) in headers)
                {
                    if (value is JsonValue v && v.TryGetValue<string>(out var headerValue))
                    {
                        message.Headers.TryAddWithoutValidation(name, headerValue);
                    }
                }
            }

            foreach (var (name, value) in extraHeaders ?? new Dictionary<string, string>())
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }

            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            HttpDeliveryTransport.Authorize(message, authType, auth, secret, token, [], clock);
        }
        catch (FormatException exception)
        {
            return new DestinationAnswer(null, null, $"The request cannot be built from the destination's settings: {exception.Message}");
        }

        try
        {
            using var response = await http.Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timer.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var body = await HttpDeliveryTransport.ReadBoundedAsync(response.Content, timer.Token, maxBytes + 1).ConfigureAwait(false);
            if (status == 401 && token is not null)
            {
                tokens.Forget(destination);
            }

            if (status is < 200 or > 299)
            {
                return new DestinationAnswer(null, status, $"The URL answered {status}.");
            }

            return body.Length > maxBytes
                ? new DestinationAnswer(null, status, $"The answer is larger than {maxBytes / 1024} KB.")
                : new DestinationAnswer(body, status, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DestinationAnswer(null, null, $"The URL did not answer within {timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException exception) when (TenantEndpointGuard.FindBlocked(exception) is not null)
        {
            return new DestinationAnswer(null, null, "The URL is not allowed: https to a public host only.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return new DestinationAnswer(null, null, $"The URL could not be reached ({exception.GetType().Name}).");
        }
    }
}
