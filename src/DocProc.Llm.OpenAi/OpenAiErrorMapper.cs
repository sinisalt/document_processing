using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Net;

namespace DocProc.Llm.OpenAi;

/// <summary>
/// Normalises SDK failures to <see cref="LlmException"/> so retries and tenant flags never depend on the vendor
/// (docs/08 "Errors"). The status mapping is the same as the Anthropic adapter's; <c>Retry-After</c> is honoured.
/// </summary>
public static class OpenAiErrorMapper
{
    public const string RetryAfterHeader = "Retry-After";
    public const string RetryAfterMillisecondsHeader = "retry-after-ms";

    public static readonly TimeSpan DefaultRateLimitDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultTransientDelay = TimeSpan.FromSeconds(5);

    public static LlmException Map(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            ClientResultException api => FromStatus((HttpStatusCode)api.Status, api, RetryAfter(api.GetRawResponse())),
            HttpRequestException http => new LlmException(LlmErrorKind.Transient, http.Message, inner: http),
            TaskCanceledException timeout => new LlmException(LlmErrorKind.Transient, "The provider did not answer within the timeout.", inner: timeout),
            _ => new LlmException(LlmErrorKind.Transient, exception.Message, inner: exception),
        };
    }

    public static LlmException FromStatus(HttpStatusCode status, Exception? inner = null, TimeSpan? retryAfter = null)
    {
        var code = (int)status;
        var message = inner?.Message ?? $"Provider returned HTTP {code}.";
        return code switch
        {
            401 or 403 => new LlmException(LlmErrorKind.Auth, message, inner: inner),
            402 => new LlmException(LlmErrorKind.Quota, message, inner: inner),
            429 => new LlmException(LlmErrorKind.RateLimited, message, retryAfter ?? DefaultRateLimitDelay, inner),
            400 or 404 or 413 or 422 => new LlmException(LlmErrorKind.InvalidRequest, message, inner: inner),
            408 or 409 or >= 500 => new LlmException(LlmErrorKind.Transient, message, retryAfter ?? DefaultTransientDelay, inner),
            _ => new LlmException(LlmErrorKind.Transient, message, inner: inner),
        };
    }

    /// <summary>
    /// Whether a 4xx rejection names <paramref name="parameter"/> (OpenAI: <c>error.param</c>; Azure and vLLM: the message).
    /// Used to drop <c>reasoning_effort</c> for models that do not take it (docs/08 "Reasoning/effort").
    /// </summary>
    public static bool IsUnsupportedParameter(ClientResultException exception, string parameter)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameter);

        if (exception.Status is not (400 or 422))
        {
            return false;
        }

        if (exception.Message.Contains(parameter, StringComparison.Ordinal))
        {
            return true;
        }

        var body = exception.GetRawResponse()?.Content?.ToString();
        return body is not null && body.Contains(parameter, StringComparison.Ordinal);
    }

    /// <summary><c>Retry-After</c> in seconds or as an HTTP date, or <c>retry-after-ms</c>; null when absent or unparseable.</summary>
    public static TimeSpan? RetryAfter(PipelineResponse? response)
    {
        if (response is null)
        {
            return null;
        }

        if (response.Headers.TryGetValue(RetryAfterMillisecondsHeader, out var ms)
            && double.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) && milliseconds >= 0)
        {
            return TimeSpan.FromMilliseconds(milliseconds);
        }

        if (!response.Headers.TryGetValue(RetryAfterHeader, out var value))
        {
            return null;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            var delay = at - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }
}
