using System.Net;
using Anthropic.Exceptions;

namespace DocProc.Llm.Anthropic;

/// <summary>Normalises SDK failures to <see cref="LlmException"/> so retries and tenant flags never depend on the vendor.</summary>
public static class AnthropicErrorMapper
{
    public static LlmException Map(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            AnthropicApiException api => FromStatus(api.StatusCode, api),
            AnthropicException other => new LlmException(LlmErrorKind.Transient, other.Message, inner: other),
            HttpRequestException http => new LlmException(LlmErrorKind.Transient, http.Message, inner: http),
            TaskCanceledException timeout => new LlmException(LlmErrorKind.Transient, "The provider did not answer within the timeout.", inner: timeout),
            _ => new LlmException(LlmErrorKind.Transient, exception.Message, inner: exception),
        };
    }

    public static LlmException FromStatus(HttpStatusCode status, Exception? inner = null)
    {
        var code = (int)status;
        var message = inner?.Message ?? $"Provider returned HTTP {code}.";
        return code switch
        {
            401 or 403 => new LlmException(LlmErrorKind.Auth, message, inner: inner),
            402 => new LlmException(LlmErrorKind.Quota, message, inner: inner),
            429 => new LlmException(LlmErrorKind.RateLimited, message, TimeSpan.FromSeconds(30), inner),
            400 or 404 or 413 or 422 => new LlmException(LlmErrorKind.InvalidRequest, message, inner: inner),
            408 or 409 or >= 500 => new LlmException(LlmErrorKind.Transient, message, TimeSpan.FromSeconds(5), inner),
            _ => new LlmException(LlmErrorKind.Transient, message, inner: inner),
        };
    }
}
