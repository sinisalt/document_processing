using DocProc.Application.Abstractions;
using DocProc.Domain.Documents;
using DocProc.Llm.Extraction;
using DocProc.Llm.Prompts;
using DocProc.Llm.Schema;

namespace DocProc.Llm.Tests.Extraction;

/// <summary>
/// The LLM classifier against the tenant's types (docs/11 §9.4, P5): the schema's enum is the candidates' keys plus
/// Unknown, one schema version per candidate set, the Task lists the descriptions, and an answer outside the set is Unknown.
/// </summary>
public sealed class TenantClassifierTests
{
    private static readonly PromptCatalogue Prompts = PromptCatalogue.Load();

    private static readonly ClassificationCandidate[] Candidates =
    [
        new("Invoice", "Invoice: a request for payment"),
        new("return-slip", "Return slip: goods returned to a supplier"),
    ];

    private static LlmDocumentClassifier Classifier(FakeGateway gateway) => new(gateway, Prompts, new LlmExtractionOptions());

    [Fact]
    public async Task The_enum_is_the_tenant_types_plus_unknown_and_the_task_describes_them()
    {
        var gateway = new FakeGateway().Answer("""{ "documentType": "return-slip", "confidence": 0.91, "language": "de", "supplierNameHint": null }""");

        var outcome = await Classifier(gateway).ClassifyAsync(Inputs.TenantId, Inputs.DocumentId, Inputs.SampleTextLayer(), Inputs.PngBytes, Candidates, CancellationToken.None);

        outcome.Type.Value.ShouldBe("return-slip");
        outcome.Confidence.ShouldBe(0.91);
        outcome.Source.ShouldBe(TypeSource.Llm);
        var request = gateway.Requests.ShouldHaveSingleItem();
        request.Prompt.Id.ShouldBe(PromptIds.ClassifyTenantDocument);
        request.OutputSchema!.Name.ShouldBe("document_classification");
        request.OutputSchema.Version.ShouldStartWith("1+t.");
        request.OutputSchema.Schema["properties"]!["documentType"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["Invoice", "return-slip", "Unknown"]);
        SchemaPortability.Check(request.OutputSchema.Schema).ShouldBeEmpty();
        request.Prompt.Task.ShouldContain("`return-slip`: Return slip: goods returned to a supplier");
        request.Prompt.Task.ShouldContain("`Unknown`: none of the above.");
    }

    [Fact]
    public async Task An_answer_outside_the_candidates_or_unknown_is_unknown()
    {
        var outside = new FakeGateway().Answer("""{ "documentType": "Statement", "confidence": 0.8, "language": null, "supplierNameHint": null }""");
        (await Classifier(outside).ClassifyAsync(Inputs.TenantId, null, Inputs.SampleTextLayer(), null, Candidates, CancellationToken.None)).Type.ShouldBe(DocumentTypeKey.Unknown);

        var unknown = new FakeGateway().Answer("""{ "documentType": "Unknown", "confidence": 0.7, "language": null, "supplierNameHint": null }""");
        var outcome = await Classifier(unknown).ClassifyAsync(Inputs.TenantId, null, Inputs.SampleTextLayer(), null, Candidates, CancellationToken.None);
        outcome.Type.ShouldBe(DocumentTypeKey.Unknown);
        outcome.Confidence.ShouldBe(0);
    }

    [Fact]
    public void One_schema_version_per_candidate_set()
    {
        var first = LlmDocumentClassifier.TenantSchema(Candidates);
        var same = LlmDocumentClassifier.TenantSchema([.. Candidates]);
        var other = LlmDocumentClassifier.TenantSchema([Candidates[0]]);

        same.Version.ShouldBe(first.Version);
        other.Version.ShouldNotBe(first.Version);
        CanonicalSchemas.Classification.Schema["properties"]!["documentType"]!["enum"]!.AsArray().Count.ShouldBe(7, "the fixed schema is untouched");
    }
}
