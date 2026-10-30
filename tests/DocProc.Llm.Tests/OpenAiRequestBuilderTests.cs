using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Llm.OpenAi;
using DocProc.Llm.Schema;
using OpenAI.Chat;
using static DocProc.Llm.Tests.OpenAiTestSupport;

namespace DocProc.Llm.Tests;

public class OpenAiRequestBuilderTests
{
    private static readonly LlmCapabilities Full = OpenAiCompatibleCapabilities.OpenAi;
    private static readonly LlmCapabilities SelfHosted = OpenAiCompatibleCapabilities.SelfHosted;

    [Fact]
    public void Builds_system_prefix_native_pdf_file_part_and_strict_json_schema_output()
    {
        var request = Request(new DocumentInput { FileName = "RE-42.pdf", Pdf = new byte[] { 1, 2, 3 }, PageTexts = ["ignored when a PDF is present"] });

        var chat = OpenAiRequestBuilder.Build(request, LlmProviderKind.OpenAi, Full);

        chat.Model.ShouldBe("gpt-5");
        chat.Messages.Count.ShouldBe(2);

        var system = Json(chat.Messages[0]);
        system.GetProperty("role").GetString().ShouldBe("system");
        var systemText = system.GetProperty("content").GetString()!;
        systemText.ShouldStartWith("Extract the invoice.");
        systemText.ShouldContain("<example>\n<input>\nRechnung 1\n</input>\n<output>\n{\"invoiceNumber\":\"1\"}\n</output>\n</example>");

        var user = Json(chat.Messages[1]);
        user.GetProperty("role").GetString().ShouldBe("user");
        var content = user.GetProperty("content").EnumerateArray().ToList();
        content.Count.ShouldBe(2);
        content[0].GetProperty("type").GetString().ShouldBe("file");
        content[0].GetProperty("file").GetProperty("filename").GetString().ShouldBe("RE-42.pdf");
        content[0].GetProperty("file").GetProperty("file_data").GetString().ShouldBe("data:application/pdf;base64," + Convert.ToBase64String([1, 2, 3]));
        content[1].GetProperty("type").GetString().ShouldBe("text");
        content[1].GetProperty("text").GetString().ShouldStartWith("Tenant:");

        var options = Json(chat.Options);
        options.GetProperty("max_completion_tokens").GetInt32().ShouldBe(4096);
        options.GetProperty("reasoning_effort").GetString().ShouldBe("medium");
        options.GetProperty("user").GetString().ShouldBe("dddddddd000000000000000000000001");
        var format = options.GetProperty("response_format");
        format.GetProperty("type").GetString().ShouldBe("json_schema");
        format.GetProperty("json_schema").GetProperty("name").GetString().ShouldBe("invoice_extraction");
        format.GetProperty("json_schema").GetProperty("strict").GetBoolean().ShouldBeTrue();
        format.GetProperty("json_schema").GetProperty("schema").GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void Sends_the_canonical_schema_unchanged()
    {
        var chat = OpenAiRequestBuilder.Build(Request(null), LlmProviderKind.OpenAi, Full);

        var sent = JsonNode.Parse(Json(chat.Options).GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").GetRawText());

        JsonNode.DeepEquals(sent, CanonicalSchemas.InvoiceExtraction.Schema).ShouldBeTrue("the schema sent in strict mode must be exactly the canonical one");
        SchemaPortability.Check(CanonicalSchemas.InvoiceExtraction).ShouldBeEmpty();
    }

    [Fact]
    public void Falls_back_to_page_images_and_text_layer_without_a_pdf()
    {
        var request = Request(new DocumentInput
        {
            FileName = "scan.pdf",
            PageImages = [new PageImage(1, new byte[] { 9 }, 100, 100), new PageImage(2, new byte[] { 8 }, 100, 100)],
            PageTexts = ["Rechnung Nr. 42", "Seite 2"],
        });

        var content = Json(OpenAiRequestBuilder.Build(request, LlmProviderKind.OpenAi, Full).Messages[1])
            .GetProperty("content").EnumerateArray().ToList();

        content.Select(c => c.GetProperty("type").GetString()).ShouldBe(["image_url", "image_url", "text", "text"]);
        content[0].GetProperty("image_url").GetProperty("url").GetString().ShouldBe("data:image/png;base64," + Convert.ToBase64String([9]));
        content[1].GetProperty("image_url").GetProperty("url").GetString().ShouldBe("data:image/png;base64," + Convert.ToBase64String([8]));
        var text = content[2].GetProperty("text").GetString()!;
        text.ShouldStartWith("<document title=\"scan.pdf (text layer)\">");
        text.ShouldContain("<page number=\"2\">\nSeite 2\n</page>");
        content[3].GetProperty("text").GetString().ShouldStartWith("Tenant:");
    }

    [Fact]
    public void Self_hosted_servers_get_images_json_object_mode_and_the_schema_in_the_prefix()
    {
        var request = Request(new DocumentInput
        {
            Pdf = new byte[] { 1, 2, 3 },
            PageImages = [new PageImage(1, new byte[] { 9 }, 100, 100)],
            PageTexts = ["Rechnung Nr. 42"],
        });

        var chat = OpenAiRequestBuilder.Build(request, LlmProviderKind.OpenAiCompatible, SelfHosted);

        Json(chat.Messages[1]).GetProperty("content").EnumerateArray().Select(c => c.GetProperty("type").GetString())
            .ShouldBe(["image_url", "text", "text"]);
        Json(chat.Options).GetProperty("response_format").GetProperty("type").GetString().ShouldBe("json_object");
        Json(chat.Messages[0]).GetProperty("content").GetString()!.ShouldContain(CanonicalSchemas.InvoiceExtraction.Json);
    }

    [Fact]
    public void Omits_images_when_the_server_has_no_vision()
    {
        var request = Request(new DocumentInput { PageImages = [new PageImage(1, new byte[] { 9 }, 100, 100)], PageTexts = ["Rechnung"] });

        var content = Json(OpenAiRequestBuilder.Build(request, LlmProviderKind.OpenAiCompatible, SelfHosted with { Vision = false }).Messages[1])
            .GetProperty("content").EnumerateArray().Select(c => c.GetProperty("type").GetString()).ToList();

        content.ShouldBe(["text", "text"]);
    }

    [Fact]
    public void Without_a_document_or_schema_only_the_task_and_prefix_are_sent()
    {
        var request = Request(null) with { OutputSchema = null, Options = new LlmOptions { Effort = ReasoningEffort.None } };

        var chat = OpenAiRequestBuilder.Build(request, LlmProviderKind.OpenAi, Full);

        // A lone text part collapses to plain string content, which every OpenAI-compatible server accepts.
        Json(chat.Messages[1]).GetProperty("content").GetString().ShouldBe(request.Prompt.Task);
        var options = Json(chat.Options);
        options.TryGetProperty("response_format", out _).ShouldBeFalse();
        options.TryGetProperty("reasoning_effort", out _).ShouldBeFalse();
        options.GetProperty("max_completion_tokens").GetInt32().ShouldBe(LlmOptions.Default.MaxOutputTokens);
    }

    [Fact]
    public void Uses_the_vendor_specific_system_prompt_when_one_exists()
    {
        var request = Request(null) with
        {
            Prompt = Request(null).Prompt with { SystemOverrides = new Dictionary<LlmProviderKind, string> { [LlmProviderKind.AzureOpenAi] = "Azure-specific wording." } },
        };

        Json(OpenAiRequestBuilder.Build(request, LlmProviderKind.AzureOpenAi, Full).Messages[0]).GetProperty("content").GetString().ShouldStartWith("Azure-specific wording.");
        Json(OpenAiRequestBuilder.Build(request, LlmProviderKind.OpenAi, Full).Messages[0]).GetProperty("content").GetString().ShouldStartWith("Extract the invoice.");
    }

    [Theory]
    [InlineData(ReasoningEffort.None, null)]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    public void Effort_maps_to_reasoning_effort_or_is_omitted(ReasoningEffort effort, string? expected)
    {
        OpenAiRequestBuilder.MapEffort(effort)?.ToString().ShouldBe(expected);

        var request = Request(null) with { Options = new LlmOptions { Effort = effort } };
        var options = Json(OpenAiRequestBuilder.Build(request, LlmProviderKind.OpenAi, Full).Options);
        if (expected is null)
        {
            options.TryGetProperty("reasoning_effort", out _).ShouldBeFalse();
        }
        else
        {
            options.GetProperty("reasoning_effort").GetString().ShouldBe(expected);
        }
    }

    [Fact]
    public void Clearing_the_effort_removes_the_parameter_from_the_wire()
    {
        var chat = OpenAiRequestBuilder.Build(Request(null), LlmProviderKind.OpenAi, Full);
        chat.HasReasoningEffort.ShouldBeTrue();

        chat.ClearReasoningEffort();

        chat.HasReasoningEffort.ShouldBeFalse();
        chat.Options.ReasoningEffortLevel.ShouldBeNull();
        Json(chat.Options).TryGetProperty("reasoning_effort", out _).ShouldBeFalse();
    }

    [Fact]
    public void The_messages_are_serialisable_sdk_models()
    {
        var chat = OpenAiRequestBuilder.Build(Request(new DocumentInput { Pdf = new byte[] { 1 } }), LlmProviderKind.OpenAi, Full);

        chat.Messages[0].ShouldBeOfType<SystemChatMessage>();
        chat.Messages[1].ShouldBeOfType<UserChatMessage>();
        Should.NotThrow(() => JsonDocument.Parse(Json(chat.Options).GetRawText()));
    }
}
