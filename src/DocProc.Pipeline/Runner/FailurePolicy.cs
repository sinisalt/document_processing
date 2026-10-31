using DocProc.Application.Abstractions;
using DocProc.Domain.Flags;
using DocProc.Llm;
using DocProc.Pipeline.Support;

namespace DocProc.Pipeline.Runner;

/// <summary>What the runner does after an attempt threw.</summary>
internal abstract record FailureDecision(string Reason)
{
    /// <summary>Re-enqueue the same message with the next attempt number after <see cref="Delay"/>.</summary>
    public sealed record Retry(string Reason, TimeSpan Delay) : FailureDecision(Reason);

    /// <summary>Move the document to Failed(reason); <see cref="FlagCode"/> is raised alongside when set.</summary>
    public sealed record Fail(string Reason, string? FlagCode = null) : FailureDecision(Reason);
}

/// <summary>
/// The failure policy of docs/03 "Idempotency and retries", as pure decision logic so it can be tested without a host:
/// <list type="bullet">
/// <item>a <see cref="DocumentProcessingException"/> or <see cref="StageFailedException"/> is about the document itself: Failed(reason), no retry;</item>
/// <item>a retryable <see cref="LlmException"/> (rate limit, transient) backs off exponentially up to the attempt limit, then fails with <c>LLM_PROVIDER_UNAVAILABLE</c>;</item>
/// <item>an <see cref="LlmException"/> that blocks the tenant (invalid key, quota, no configuration) fails at once with the same flag: a retry cannot fix a missing key, and the document waits for an operator;</item>
/// <item>a refused (<see cref="LlmErrorKind.ContentRefused"/>) or rejected (<see cref="LlmErrorKind.InvalidRequest"/>) LLM request fails at once without a flag: the provider would answer the same request the same way;</item>
/// <item>anything else (a bug, a storage hiccup, an unusable model answer) is retried with backoff up to the limit, then Failed(reason).</item>
/// </list>
/// </summary>
internal static class FailurePolicy
{
    public static FailureDecision Decide(Exception exception, int attempt, PipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(options);

        switch (exception)
        {
            case DocumentProcessingException processing:
                return new FailureDecision.Fail($"{processing.Failure}: {processing.Message}");

            case StageFailedException stage:
                return new FailureDecision.Fail(stage.Message);

            case LlmException { IsRetryable: true } llm:
                if (attempt < options.MaxAttempts)
                {
                    var backoff = options.BackoffAfter(attempt);
                    var delay = llm.RetryAfter is { } retryAfter && retryAfter > backoff ? retryAfter : backoff;
                    return new FailureDecision.Retry(Describe(llm), delay);
                }

                return new FailureDecision.Fail(Describe(llm), FlagCodes.LlmProviderUnavailable);

            case LlmException { BlocksTenant: true } llm:
                return new FailureDecision.Fail(Describe(llm), FlagCodes.LlmProviderUnavailable);

            case LlmException { Kind: LlmErrorKind.ContentRefused or LlmErrorKind.InvalidRequest } llm:
                // The same request would be refused or rejected again: resending it only spends tokens and time.
                return new FailureDecision.Fail(Describe(llm));

            default:
                return attempt < options.MaxAttempts
                    ? new FailureDecision.Retry(Describe(exception), options.BackoffAfter(attempt))
                    : new FailureDecision.Fail(Describe(exception));
        }
    }

    private static string Describe(LlmException exception) => $"LLM provider {exception.Kind}: {exception.Message}";

    private static string Describe(Exception exception) => $"{exception.GetType().Name}: {exception.Message}";
}
