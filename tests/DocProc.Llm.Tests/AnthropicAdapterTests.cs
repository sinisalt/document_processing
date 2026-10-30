using System.Net;
using System.Text.Json;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using DocProc.Llm.Anthropic;
using DocProc.Llm.Schema;

namespace DocProc.Llm.Tests;

public class AnthropicAdapterTests
{
    private static LlmRequest Request(DocumentInput? document) => new()
    {
        Stage = LlmStage.Extract,
        Model = "claude-opus-5-5",
        Prompt = new PromptBundle
        {
            Id = "extract-invoice",
            Version = "1",
            System = "Extract the invoice.",
            Examples = [new PromptExample("Rechnung 1", "{\"invoiceNumber\":\"1\"}")],
            Task = "Tenant: Gasthaus zur Post GmbH, DE123456789.",
        },
        Document = document,
        OutputSchema = CanonicalSchemas.InvoiceExtraction,
        Options = new LlmOptions { Effort = ReasoningEffort.Medium, MaxOutputTokens = 4096 },
        DocumentId = Guid.Parse("dddddddd-0000-0000-0000-000000000001"),
    };

    private static JsonElement Body(MessageCreateParams p) =>
        JsonSerializer.SerializeToElement(p.RawBodyData);

    [Fact]
    public void Builds_cached_system_prefix_native_pdf_block_and_json_schema_output()
    {
        var request = Request(new DocumentInput { FileName = "RE-42.pdf", Pdf = new byte[] { 1, 2, 3 }, PageTexts = ["ignored when a PDF is present"] });

        var body = Body(AnthropicRequestBuilder.Build(request, LlmProviderKind.Anthropic));

        body.GetProperty("model").GetString().ShouldBe("claude-opus-5-5");
        body.GetProperty("max_tokens").GetInt32().ShouldBe(4096);

        var system = body.GetProperty("system").EnumerateArray().ToList();
        system.Count.ShouldBe(2);
        system[0].GetProperty("text").GetString().ShouldBe("Extract the invoice.");
        system[0].TryGetProperty("cache_control", out _).ShouldBeFalse();
        system[1].GetProperty("text").GetString()!.ShouldContain("<example>");
        system[1].GetProperty("cache_control").GetProperty("ttl").GetString().ShouldBe("1h");

        var content = body.GetProperty("messages")[0].GetProperty("content").EnumerateArray().ToList();
        content.Count.ShouldBe(2);
        content[0].GetProperty("type").GetString().ShouldBe("document");
        content[0].GetProperty("source").GetProperty("type").GetString().ShouldBe("base64");
        content[0].GetProperty("source").GetProperty("media_type").GetString().ShouldBe("application/pdf");
        content[0].GetProperty("source").GetProperty("data").GetString().ShouldBe(Convert.ToBase64String([1, 2, 3]));
        content[0].GetProperty("title").GetString().ShouldBe("RE-42.pdf");
        content[1].GetProperty("type").GetString().ShouldBe("text");
        content[1].GetProperty("text").GetString().ShouldStartWith("Tenant:");

        var output = body.GetProperty("output_config");
        output.GetProperty("effort").GetString().ShouldBe("medium");
        output.GetProperty("format").GetProperty("type").GetString().ShouldBe("json_schema");
        output.GetProperty("format").GetProperty("schema").GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();

        body.GetProperty("metadata").GetProperty("user_id").GetString().ShouldBe("dddddddd000000000000000000000001");
    }

    [Fact]
    public void Falls_back_to_page_images_and_text_layer_without_a_pdf()
    {
        var request = Request(new DocumentInput
        {
            PageImages = [new PageImage(1, new byte[] { 9 }, 100, 100), new PageImage(2, new byte[] { 8 }, 100, 100)],
            PageTexts = ["Rechnung Nr. 42", "Seite 2"],
        });

        var content = Body(AnthropicRequestBuilder.Build(request, LlmProviderKind.Anthropic))
            .GetProperty("messages")[0].GetProperty("content").EnumerateArray().ToList();

        content.Select(c => c.GetProperty("type").GetString()).ShouldBe(["image", "image", "document", "text"]);
        content[0].GetProperty("source").GetProperty("media_type").GetString().ShouldBe("image/png");
        var text = content[2].GetProperty("source");
        text.GetProperty("type").GetString().ShouldBe("text");
        text.GetProperty("data").GetString()!.ShouldContain("<page number=\"2\">");
    }

    [Fact]
    public void Uses_the_vendor_specific_system_prompt_when_one_exists()
    {
        var request = Request(null) with
        {
            Prompt = Request(null).Prompt with { SystemOverrides = new Dictionary<LlmProviderKind, string> { [LlmProviderKind.Anthropic] = "Claude-specific wording." } },
        };

        Body(AnthropicRequestBuilder.Build(request, LlmProviderKind.Anthropic)).GetProperty("system")[0].GetProperty("text").GetString().ShouldBe("Claude-specific wording.");
        Body(AnthropicRequestBuilder.Build(request, LlmProviderKind.AnthropicFoundry)).GetProperty("system")[0].GetProperty("text").GetString().ShouldBe("Extract the invoice.");
    }

    [Theory]
    [InlineData(ReasoningEffort.None, "low")]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    public void Effort_degrades_to_the_nearest_supported_level(ReasoningEffort effort, string expected) =>
        AnthropicRequestBuilder.MapEffort(effort).ToString().ShouldBe(expected, StringCompareShould.IgnoreCase);

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, LlmErrorKind.Auth)]
    [InlineData(HttpStatusCode.Forbidden, LlmErrorKind.Auth)]
    [InlineData(HttpStatusCode.PaymentRequired, LlmErrorKind.Quota)]
    [InlineData(HttpStatusCode.TooManyRequests, LlmErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.BadRequest, LlmErrorKind.InvalidRequest)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, LlmErrorKind.InvalidRequest)]
    [InlineData(HttpStatusCode.InternalServerError, LlmErrorKind.Transient)]
    [InlineData((HttpStatusCode)529, LlmErrorKind.Transient)]
    public void Http_errors_map_to_vendor_neutral_kinds(HttpStatusCode status, LlmErrorKind expected)
    {
        var mapped = AnthropicErrorMapper.Map(new AnthropicApiException("boom") { StatusCode = status, ResponseBody = "{}" });

        mapped.Kind.ShouldBe(expected);
        mapped.IsRetryable.ShouldBe(expected is LlmErrorKind.RateLimited or LlmErrorKind.Transient);
        if (expected == LlmErrorKind.RateLimited)
        {
            mapped.RetryAfter.ShouldNotBeNull();
        }
    }

    [Fact]
    public void Network_failures_are_transient() =>
        AnthropicErrorMapper.Map(new HttpRequestException("connection reset")).Kind.ShouldBe(LlmErrorKind.Transient);
}
