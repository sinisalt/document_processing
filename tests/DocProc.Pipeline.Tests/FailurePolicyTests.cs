using DocProc.Application.Abstractions;
using DocProc.Domain.Flags;
using DocProc.Llm;
using DocProc.Pipeline.Runner;
using DocProc.Pipeline.Support;

namespace DocProc.Pipeline.Tests;

public class FailurePolicyTests
{
    private static readonly PipelineOptions Options = new() { RetryBaseDelay = TimeSpan.FromSeconds(5), MaxAttempts = 5, MaxRetryDelay = TimeSpan.FromMinutes(1) };

    [Fact]
    public void Document_problems_fail_without_a_retry()
    {
        FailurePolicy.Decide(new DocumentProcessingException(DocumentProcessingFailure.EncryptedPdf, "needs a password"), 1, Options)
            .ShouldBeOfType<FailureDecision.Fail>().ShouldSatisfyAllConditions(
                f => f.Reason.ShouldBe("EncryptedPdf: needs a password"),
                f => f.FlagCode.ShouldBeNull());

        FailurePolicy.Decide(new StageFailedException("unsupported format: Office"), 1, Options)
            .ShouldBeOfType<FailureDecision.Fail>().Reason.ShouldBe("unsupported format: Office");
    }

    [Fact]
    public void Retryable_llm_errors_back_off_exponentially_and_honour_retry_after()
    {
        FailurePolicy.Decide(new LlmException(LlmErrorKind.Transient, "503"), 1, Options)
            .ShouldBeOfType<FailureDecision.Retry>().Delay.ShouldBe(TimeSpan.FromSeconds(5));
        FailurePolicy.Decide(new LlmException(LlmErrorKind.Transient, "503"), 3, Options)
            .ShouldBeOfType<FailureDecision.Retry>().Delay.ShouldBe(TimeSpan.FromSeconds(20));
        FailurePolicy.Decide(new LlmException(LlmErrorKind.RateLimited, "429", retryAfter: TimeSpan.FromSeconds(30)), 1, Options)
            .ShouldBeOfType<FailureDecision.Retry>().Delay.ShouldBe(TimeSpan.FromSeconds(30));
        FailurePolicy.Decide(new LlmException(LlmErrorKind.Transient, "503"), 20, Options)
            .ShouldBeOfType<FailureDecision.Fail>().FlagCode.ShouldBe(FlagCodes.LlmProviderUnavailable);
    }

    [Fact]
    public void Last_attempt_of_a_retryable_llm_error_fails_with_the_provider_flag()
    {
        var decision = FailurePolicy.Decide(new LlmException(LlmErrorKind.Transient, "503"), 5, Options).ShouldBeOfType<FailureDecision.Fail>();
        decision.FlagCode.ShouldBe(FlagCodes.LlmProviderUnavailable);
        decision.Reason.ShouldContain("Transient");
    }

    [Theory]
    [InlineData(LlmErrorKind.Auth)]
    [InlineData(LlmErrorKind.Quota)]
    [InlineData(LlmErrorKind.Unavailable)]
    public void Tenant_blocking_llm_errors_fail_at_once(LlmErrorKind kind)
    {
        var decision = FailurePolicy.Decide(new LlmException(kind, "blocked"), 1, Options).ShouldBeOfType<FailureDecision.Fail>();
        decision.FlagCode.ShouldBe(FlagCodes.LlmProviderUnavailable);
    }

    [Fact]
    public void Other_exceptions_retry_until_the_attempt_limit()
    {
        FailurePolicy.Decide(new InvalidOperationException("boom"), 4, Options)
            .ShouldBeOfType<FailureDecision.Retry>().Delay.ShouldBe(TimeSpan.FromSeconds(40));
        FailurePolicy.Decide(new InvalidOperationException("boom"), 5, Options)
            .ShouldBeOfType<FailureDecision.Fail>().ShouldSatisfyAllConditions(
                f => f.Reason.ShouldBe("InvalidOperationException: boom"),
                f => f.FlagCode.ShouldBeNull());
    }

    [Fact]
    public void Backoff_is_capped()
    {
        Options.BackoffAfter(1).ShouldBe(TimeSpan.FromSeconds(5));
        Options.BackoffAfter(4).ShouldBe(TimeSpan.FromSeconds(40));
        Options.BackoffAfter(10).ShouldBe(TimeSpan.FromMinutes(1));
    }
}
