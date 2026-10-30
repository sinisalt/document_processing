using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocProc.Llm.OpenAi;

/// <summary>What a 4xx rejection says about one request parameter.</summary>
public enum ParameterRejection
{
    None,
    UnsupportedParameter,
    UnsupportedValue,
}

/// <summary>
/// Normalises SDK failures to <see cref="LlmException"/> so retries and tenant flags never depend on the vendor
/// (docs/08 "Errors"). The status mapping is the same as the Anthropic adapter's; <c>Retry-After</c> is honoured.
/// </summary>
public static partial class OpenAiErrorMapper
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
            OperationCanceledException timeout => new LlmException(LlmErrorKind.Transient, "The provider did not answer within the timeout.", inner: timeout),
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
    /// Whether a 4xx rejection says the server does not take <paramref name="parameter"/> at all (OpenAI:
    /// <c>error.code == "unsupported_parameter"</c> with <c>error.param</c>; Azure, vLLM and Ollama: an
    /// "unsupported/unknown parameter", "not supported" or "extra inputs are not permitted" message naming it). Used to
    /// drop <c>reasoning_effort</c> for models that do not take it (docs/08 "Reasoning/effort"). A rejection of the
    /// parameter's <em>value</em> is not an unsupported parameter; see <see cref="IsUnsupportedValue"/>.
    /// </summary>
    public static bool IsUnsupportedParameter(ClientResultException exception, string parameter) =>
        Classify(exception, parameter) == ParameterRejection.UnsupportedParameter;

    /// <summary>
    /// Whether a 4xx rejection says the server takes <paramref name="parameter"/> but not the value sent (OpenAI:
    /// <c>error.code</c> <c>unsupported_value</c>/<c>invalid_value</c>, or an "unsupported value"/"invalid value"/
    /// "supported values are" message naming the parameter). The caller degrades to the nearest supported value.
    /// </summary>
    public static bool IsUnsupportedValue(ClientResultException exception, string parameter) =>
        Classify(exception, parameter) == ParameterRejection.UnsupportedValue;

    /// <summary>How a 4xx rejection relates to <paramref name="parameter"/>; <see cref="ParameterRejection.None"/> for any other error.</summary>
    public static ParameterRejection Classify(ClientResultException exception, string parameter)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameter);

        if (exception.Status is not (400 or 422))
        {
            return ParameterRejection.None;
        }

        var error = ErrorDetails.From(exception);
        if (error.Param is { } param && !string.Equals(param, parameter, StringComparison.Ordinal))
        {
            return ParameterRejection.None; // the server blamed another parameter
        }

        var text = error.Message + "\n" + exception.Message;
        if (error.Param is null && !text.Contains(parameter, StringComparison.Ordinal))
        {
            return ParameterRejection.None;
        }

        if (error.Code is "unsupported_value" or "invalid_value" || ContainsAny(text, "unsupported value", "invalid value", "supported values"))
        {
            return ParameterRejection.UnsupportedValue;
        }

        if (error.Code is "unsupported_parameter" or "unknown_parameter"
            || ContainsAny(text, "unsupported parameter", "unknown parameter", "unrecognized parameter", "not supported", "not permitted", "extra_forbidden"))
        {
            return ParameterRejection.UnsupportedParameter;
        }

        return ParameterRejection.None;
    }

    /// <summary>
    /// The values a rejection lists as accepted ("Supported values are: 'low', 'medium', and 'high'."), lower-cased;
    /// empty when the message names none.
    /// </summary>
    public static IReadOnlyList<string> SupportedValues(ClientResultException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Only the server's own sentence: the SDK's exception message echoes the whole JSON body, whose quoted keys are not values.
        var text = ErrorDetails.From(exception).Message is { Length: > 0 } body ? body : exception.Message;
        var at = text.IndexOf("supported values", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return [];
        }

        var end = text.IndexOfAny(['\r', '\n'], at);
        var sentence = end < 0 ? text[at..] : text[at..end];
        return SupportedValuePattern().Matches(sentence)
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
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

    [GeneratedRegex("['\"`]([a-z_]+)['\"`]", RegexOptions.IgnoreCase)]
    private static partial Regex SupportedValuePattern();

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

    /// <summary>The <c>error</c> object of an OpenAI-style body, or the top-level fields vLLM and Ollama use; blanks when the body is not JSON.</summary>
    private readonly record struct ErrorDetails(string Message, string? Param, string? Code)
    {
        public static ErrorDetails From(ClientResultException exception)
        {
            var body = exception.GetRawResponse()?.Content?.ToString();
            if (string.IsNullOrWhiteSpace(body))
            {
                return new ErrorDetails(string.Empty, null, null);
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return new ErrorDetails(body, null, null);
                }

                var error = root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
                return new ErrorDetails(StringOf(error, "message") ?? body, StringOf(error, "param"), StringOf(error, "code"));
            }
            catch (JsonException)
            {
                return new ErrorDetails(body, null, null);
            }
        }

        private static string? StringOf(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
