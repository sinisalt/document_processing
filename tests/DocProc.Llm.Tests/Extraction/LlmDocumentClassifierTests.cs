using DocProc.Domain.Documents;
using DocProc.Llm.Contracts;
using DocProc.Llm.Extraction;
using DocProc.Llm.Prompts;
using DocProc.Llm.Schema;

namespace DocProc.Llm.Tests.Extraction;

public class LlmDocumentClassifierTests
{
    private static readonly PromptCatalogue Prompts = PromptCatalogue.Load();

    private static LlmDocumentClassifier Classifier(FakeGateway gateway, LlmExtractionOptions? options = null) =>
        new(gateway, Prompts, options ?? new LlmExtractionOptions());

    [Fact]
    public async Task Sends_the_first_page_image_and_the_text_excerpt_and_maps_the_answer()
    {
        var gateway = new FakeGateway().Answer(SampleAnswers.Classification);

        var outcome = await Classifier(gateway).ClassifyAsync(Inputs.TenantId, Inputs.SampleTextLayer(), Inputs.PngBytes, CancellationToken.None);

        outcome.Type.ShouldBe(DocumentType.CreditNote);
        outcome.Confidence.ShouldBe(0.82);
        outcome.Source.ShouldBe(TypeSource.Llm);
        outcome.Language.ShouldBe("de");

        var request = gateway.Requests.ShouldHaveSingleItem();
        request.Stage.ShouldBe(LlmStage.Classify);
        request.OutputSchema.ShouldBe(CanonicalSchemas.Classification);
        request.Prompt.CacheKey.ShouldBe(Prompts.ClassifyDocument.CacheKey);
        request.Options.MaxOutputTokens.ShouldBe(new LlmExtractionOptions().MaxAuxiliaryOutputTokens);
        request.Document.ShouldNotBeNull().PageImages.ShouldHaveSingleItem().Width.ShouldBe(123);
        request.Prompt.Task.ShouldContain("Classify the attached first page.");
        request.Prompt.Task.ShouldContain("RE-2026-0042");
    }

    [Fact]
    public async Task Text_excerpt_is_cut_to_the_configured_length_and_works_without_an_image()
    {
        var gateway = new FakeGateway().Answer(SampleAnswers.Classification);

        await Classifier(gateway, new LlmExtractionOptions { ClassificationTextChars = 12 }).ClassifyAsync(Inputs.TenantId, Inputs.SampleTextLayer(), null, CancellationToken.None);

        var request = gateway.Requests.Single();
        request.Document.ShouldBeNull();
        request.Prompt.Task.ShouldContain("no page image is available");
        request.Prompt.Task.ShouldContain("The first 12 characters of the text layer:\n<text>\nMETRO Deutsc\n</text>");
    }

    [Fact]
    public async Task Nothing_to_look_at_is_unknown_without_a_call()
    {
        var gateway = new FakeGateway();

        var outcome = await Classifier(gateway).ClassifyAsync(Inputs.TenantId, null, null, CancellationToken.None);

        outcome.ShouldBe(new Application.Abstractions.ClassificationOutcome(DocumentType.Unknown, 0, TypeSource.Llm));
        gateway.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Self_reported_confidence_is_clamped_and_capped()
    {
        var over = new FakeGateway().Answer("""{ "documentType": "Invoice", "confidence": 1.4, "language": " ", "supplierNameHint": null }""");
        var under = new FakeGateway().Answer("""{ "documentType": "Other", "confidence": -2, "language": null, "supplierNameHint": null }""");

        var high = await Classifier(over).ClassifyAsync(Inputs.TenantId, Inputs.SampleTextLayer(), null, CancellationToken.None);
        var low = await Classifier(under).ClassifyAsync(Inputs.TenantId, Inputs.SampleTextLayer(), null, CancellationToken.None);

        high.Confidence.ShouldBe(LlmDocumentClassifier.MaxConfidence);
        high.Language.ShouldBeNull();
        low.Confidence.ShouldBe(0);
        low.Type.ShouldBe(DocumentType.Other);
    }

    [Theory]
    [InlineData(ClassifiedDocumentType.Invoice, DocumentType.Invoice)]
    [InlineData(ClassifiedDocumentType.CreditNote, DocumentType.CreditNote)]
    [InlineData(ClassifiedDocumentType.PurchaseOrder, DocumentType.PurchaseOrder)]
    [InlineData(ClassifiedDocumentType.DeliveryNote, DocumentType.DeliveryNote)]
    [InlineData(ClassifiedDocumentType.Statement, DocumentType.Statement)]
    [InlineData(ClassifiedDocumentType.Reminder, DocumentType.Reminder)]
    [InlineData(ClassifiedDocumentType.Other, DocumentType.Other)]
    public void Every_classified_type_maps_to_a_document_type(ClassifiedDocumentType classified, DocumentType expected) =>
        LlmDocumentClassifier.Map(classified).ShouldBe(expected);

    [Fact]
    public async Task A_truncated_or_malformed_answer_fails_and_a_refusal_is_a_provider_fault()
    {
        var truncated = new FakeGateway().Answer("{\"documentType\":", StopReasons.MaxTokens);
        var refused = new FakeGateway().Answer("no", StopReasons.Refusal);

        await Should.ThrowAsync<LlmExtractionException>(() => Classifier(truncated).ClassifyAsync(Inputs.TenantId, Inputs.SampleTextLayer(), null, CancellationToken.None));
        var ex = await Should.ThrowAsync<LlmException>(() => Classifier(refused).ClassifyAsync(Inputs.TenantId, Inputs.SampleTextLayer(), null, CancellationToken.None));
        ex.Kind.ShouldBe(LlmErrorKind.ContentRefused);
    }
}
