using System.ClientModel;
using System.Globalization;
using System.Net;
using DocProc.Llm.OpenAi;

namespace DocProc.Llm.Tests;

public class OpenAiErrorMapperTests
{
    private static ClientResultException ApiError(int status, string body = "{\"error\":{\"message\":\"boom\",\"type\":\"server_error\"}}", params (string Name, string Value)[] headers) =>
        new("boom", new FakePipelineResponse(status, body, headers));

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, LlmErrorKind.Auth)]
    [InlineData(HttpStatusCode.Forbidden, LlmErrorKind.Auth)]
    [InlineData(HttpStatusCode.PaymentRequired, LlmErrorKind.Quota)]
    [InlineData(HttpStatusCode.TooManyRequests, LlmErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.BadRequest, LlmErrorKind.InvalidRequest)]
    [InlineData(HttpStatusCode.NotFound, LlmErrorKind.InvalidRequest)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, LlmErrorKind.InvalidRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity, LlmErrorKind.InvalidRequest)]
    [InlineData(HttpStatusCode.RequestTimeout, LlmErrorKind.Transient)]
    [InlineData(HttpStatusCode.Conflict, LlmErrorKind.Transient)]
    [InlineData(HttpStatusCode.InternalServerError, LlmErrorKind.Transient)]
    [InlineData(HttpStatusCode.BadGateway, LlmErrorKind.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, LlmErrorKind.Transient)]
    [InlineData((HttpStatusCode)529, LlmErrorKind.Transient)]
    public void Http_errors_map_to_vendor_neutral_kinds(HttpStatusCode status, LlmErrorKind expected)
    {
        var mapped = OpenAiErrorMapper.Map(ApiError((int)status));

        mapped.Kind.ShouldBe(expected);
        mapped.IsRetryable.ShouldBe(expected is LlmErrorKind.RateLimited or LlmErrorKind.Transient);
        mapped.InnerException.ShouldBeOfType<ClientResultException>();
        if (expected == LlmErrorKind.RateLimited)
        {
            mapped.RetryAfter.ShouldBe(OpenAiErrorMapper.DefaultRateLimitDelay);
        }
    }

    [Fact]
    public void Rate_limits_honour_retry_after_in_seconds()
    {
        var mapped = OpenAiErrorMapper.Map(ApiError(429, headers: ("Retry-After", "17")));

        mapped.Kind.ShouldBe(LlmErrorKind.RateLimited);
        mapped.RetryAfter.ShouldBe(TimeSpan.FromSeconds(17));
    }

    [Fact]
    public void Rate_limits_honour_retry_after_in_milliseconds()
    {
        var mapped = OpenAiErrorMapper.Map(ApiError(429, headers: [("retry-after-ms", "250"), ("Retry-After", "1")]));

        mapped.RetryAfter.ShouldBe(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void Rate_limits_honour_retry_after_as_an_http_date()
    {
        var at = DateTimeOffset.UtcNow.AddSeconds(90);

        var mapped = OpenAiErrorMapper.Map(ApiError(429, headers: ("Retry-After", at.ToString("R", CultureInfo.InvariantCulture))));

        mapped.RetryAfter.ShouldNotBeNull();
        mapped.RetryAfter.Value.ShouldBeInRange(TimeSpan.FromSeconds(80), TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void A_past_retry_after_date_becomes_zero_and_garbage_falls_back_to_the_default()
    {
        OpenAiErrorMapper.Map(ApiError(429, headers: ("Retry-After", "Wed, 21 Oct 2015 07:28:00 GMT"))).RetryAfter.ShouldBe(TimeSpan.Zero);
        OpenAiErrorMapper.Map(ApiError(429, headers: ("Retry-After", "soon"))).RetryAfter.ShouldBe(OpenAiErrorMapper.DefaultRateLimitDelay);
    }

    [Fact]
    public void Server_errors_carry_a_short_default_delay_or_the_servers_retry_after()
    {
        OpenAiErrorMapper.Map(ApiError(503)).RetryAfter.ShouldBe(OpenAiErrorMapper.DefaultTransientDelay);
        OpenAiErrorMapper.Map(ApiError(503, headers: ("Retry-After", "2"))).RetryAfter.ShouldBe(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void A_result_exception_without_a_response_is_transient() =>
        OpenAiErrorMapper.Map(new ClientResultException("connection dropped")).Kind.ShouldBe(LlmErrorKind.Transient);

    [Fact]
    public void Network_failures_and_timeouts_are_transient()
    {
        OpenAiErrorMapper.Map(new HttpRequestException("connection reset")).Kind.ShouldBe(LlmErrorKind.Transient);
        OpenAiErrorMapper.Map(new TaskCanceledException("timed out")).Kind.ShouldBe(LlmErrorKind.Transient);
        OpenAiErrorMapper.Map(new InvalidOperationException("odd")).Kind.ShouldBe(LlmErrorKind.Transient);
    }

    [Fact]
    public void Detects_a_rejected_parameter_from_the_error_body()
    {
        var openAi = ApiError(400, FakeOpenAiHandler.ErrorJson("Unsupported parameter: 'reasoning_effort' is not supported with this model.", param: "reasoning_effort", code: "unsupported_parameter"));
        var vllm = ApiError(422, "{\"object\":\"error\",\"message\":\"[{'type': 'extra_forbidden', 'loc': ('body', 'reasoning_effort'), 'msg': 'Extra inputs are not permitted'}]\"}");
        var unrelated = ApiError(400, FakeOpenAiHandler.ErrorJson("Invalid schema for response_format", param: "response_format"));
        var serverError = ApiError(500, FakeOpenAiHandler.ErrorJson("reasoning_effort exploded", type: "server_error"));

        OpenAiErrorMapper.IsUnsupportedParameter(openAi, "reasoning_effort").ShouldBeTrue();
        OpenAiErrorMapper.IsUnsupportedParameter(vllm, "reasoning_effort").ShouldBeTrue();
        OpenAiErrorMapper.IsUnsupportedParameter(unrelated, "reasoning_effort").ShouldBeFalse();
        OpenAiErrorMapper.IsUnsupportedParameter(serverError, "reasoning_effort").ShouldBeFalse();
    }

    [Fact]
    public void A_rejected_value_is_not_an_unsupported_parameter()
    {
        var unsupportedValue = ApiError(400, FakeOpenAiHandler.ErrorJson("Unsupported value: 'high' is not supported with this model. Supported values are: 'low' and 'medium'.", param: "reasoning_effort", code: "unsupported_value"));
        var invalidValue = ApiError(400, FakeOpenAiHandler.ErrorJson("Invalid value: 'xhigh'. Supported values are: 'low', 'medium', and 'high'.", param: "reasoning_effort", code: "invalid_value"));
        var messageOnly = ApiError(400, "{\"error\":{\"message\":\"reasoning_effort: invalid value 'high'\"}}");

        OpenAiErrorMapper.IsUnsupportedParameter(unsupportedValue, "reasoning_effort").ShouldBeFalse("'not supported' inside a value error must not drop the parameter");
        OpenAiErrorMapper.IsUnsupportedValue(unsupportedValue, "reasoning_effort").ShouldBeTrue();
        OpenAiErrorMapper.IsUnsupportedValue(invalidValue, "reasoning_effort").ShouldBeTrue();
        OpenAiErrorMapper.IsUnsupportedValue(messageOnly, "reasoning_effort").ShouldBeTrue();
        OpenAiErrorMapper.SupportedValues(unsupportedValue).ShouldBe(["low", "medium"]);
        OpenAiErrorMapper.SupportedValues(invalidValue).ShouldBe(["low", "medium", "high"]);
        OpenAiErrorMapper.SupportedValues(messageOnly).ShouldBeEmpty();
    }

    [Fact]
    public void A_bare_mention_of_the_parameter_is_not_a_rejection()
    {
        var mention = ApiError(400, FakeOpenAiHandler.ErrorJson("Your request exceeded the context window; lower reasoning_effort or shorten the prompt."));
        var otherParam = ApiError(400, FakeOpenAiHandler.ErrorJson("Unsupported parameter: 'temperature' is not supported with reasoning_effort set.", param: "temperature", code: "unsupported_parameter"));
        var ollama = ApiError(400, "{\"error\":{\"message\":\"unknown parameter: reasoning_effort\"}}");

        OpenAiErrorMapper.Classify(mention, "reasoning_effort").ShouldBe(ParameterRejection.None);
        OpenAiErrorMapper.Classify(otherParam, "reasoning_effort").ShouldBe(ParameterRejection.None);
        OpenAiErrorMapper.Classify(ollama, "reasoning_effort").ShouldBe(ParameterRejection.UnsupportedParameter);
    }

    [Fact]
    public void From_status_without_an_inner_exception_describes_the_status() =>
        OpenAiErrorMapper.FromStatus(HttpStatusCode.BadGateway).Message.ShouldBe("Provider returned HTTP 502.");
}
