using System.Text.Json.Nodes;
using DocProc.Application.Configuration;
using DocProc.Definitions;
using DocProc.Definitions.Pipelines;
using DocProc.Domain.Documents;

namespace DocProc.Integration.Tests;

/// <summary>
/// The step model of docs/11 §10.1 (P5): the built-in steps per phase, the type's step list, the override layers, the
/// fixed steps (<c>classify.identification</c> first and non-removable, the review gate last) refused at save, at every
/// layer and in a stored pipeline, and the hash that identifies a pipeline.
/// </summary>
public sealed class PipelineResolverTests
{
    private static readonly string LookupDestination = Guid.NewGuid().ToString("D");

    private static TypeDefinition Invoice => StandardTemplates.ForLegacyType(DocumentTypeKey.Invoice)!;

    private static TypeDefinition PriceList => StandardTemplates.Find("std.price-list")!;

    [Fact]
    public void The_builtin_pipeline_lists_every_phase_in_order_with_identification_first_and_the_gate_last()
    {
        var pipeline = PipelineResolver.Baseline(Invoice, configurable: false);

        pipeline.Steps.Select(s => s.Phase).Distinct().ShouldBe(PipelinePhases.All);
        pipeline.Phase(PipelinePhases.Classify).First().Kind.ShouldBe(StepKindCatalogue.ClassifyIdentification);
        pipeline.Phase(PipelinePhases.Validate).Last().Kind.ShouldBe(StepKindCatalogue.ReviewGate);
        pipeline.Phase(PipelinePhases.Extract).Select(s => s.Id).ShouldBe(["xml", "map", "prefill", "llm"]);
        pipeline.Phase(PipelinePhases.Validate).Select(s => s.Kind).ShouldBe([StepKindCatalogue.ValidateRules, StepKindCatalogue.ValidateDuplicate, StepKindCatalogue.ReviewGate]);
        pipeline.Steps.ShouldAllBe(s => s.Enabled);
        pipeline.Overridden.ShouldBeFalse();
    }

    [Fact]
    public void Without_configurableSteps_the_type_s_step_parameters_and_added_steps_are_left_out()
    {
        var off = PipelineResolver.Baseline(PriceList, configurable: false);
        var on = PipelineResolver.Baseline(PriceList);

        off.Find("llm")!.Params.Count.ShouldBe(0, "the price list's llm parameters did nothing before P5");
        off.Find("defaults").ShouldBeNull();
        on.Find("llm")!.Text("effort").ShouldBe("low");
        on.Find("llm")!.Source.ShouldBe(PipelineSources.Type);
        on.Phase(PipelinePhases.Extract).Select(s => s.Id).ShouldBe(["xml", "map", "prefill", "llm", "defaults", "uom"]);
        on.Find("defaults")!.Runs.ShouldBeFalse("defaults is listed as the definition says, and not run by this build");
        on.Phase(PipelinePhases.Validate).Select(s => s.Kind).ShouldBe([StepKindCatalogue.ValidateSchema, StepKindCatalogue.ValidateRules, StepKindCatalogue.ReviewGate]);
    }

    [Fact]
    public void A_pipeline_without_a_type_has_the_builtin_extract_chain_and_the_legacy_validate_steps()
    {
        var pipeline = PipelineResolver.Baseline(null);

        pipeline.Phase(PipelinePhases.Extract).Select(s => s.Kind).ShouldBe([StepKindCatalogue.XmlEInvoice, StepKindCatalogue.SupplierMapping, StepKindCatalogue.LayoutPrefill, StepKindCatalogue.Llm]);
        pipeline.Runs(StepKindCatalogue.ValidateDuplicate).ShouldBeTrue();
    }

    [Fact]
    public void The_hash_is_stable_and_changes_with_any_step_or_parameter()
    {
        var a = PipelineResolver.Baseline(Invoice);
        var b = PipelineResolver.Baseline(Invoice);
        var disabled = Resolve(Invoice, Layer(PipelineSources.Supplier, Disable("llm")));
        var effort = Resolve(Invoice, Layer(PipelineSources.Supplier, Param("llm", ("effort", "high"))));

        a.Hash.ShouldBe(b.Hash);
        a.Hash.Length.ShouldBe(32);
        disabled.Hash.ShouldNotBe(a.Hash);
        effort.Hash.ShouldNotBe(a.Hash);
        effort.Hash.ShouldNotBe(disabled.Hash);
    }

    [Fact]
    public void The_four_operations_apply_in_order_group_then_supplier_then_layout()
    {
        var pipeline = Resolve(
            Invoice,
            Layer(PipelineSources.Group, Param("llm", ("effort", "low")), Disable("duplicate")),
            Layer(PipelineSources.Supplier, Param("llm", ("effort", "high")), InsertAfter("map", Lookup("erpVendor"))),
            Layer(PipelineSources.Layout, Replace("rules", new StepDefinition { Id = "rules", Kind = StepKindCatalogue.ValidateSchema })));

        pipeline.Find("llm")!.Text("effort").ShouldBe("high", "the supplier's override comes after the group's");
        pipeline.Find("llm")!.Source.ShouldBe(PipelineSources.Supplier);
        pipeline.Find("duplicate")!.Enabled.ShouldBeFalse();
        pipeline.Runs(StepKindCatalogue.ValidateDuplicate).ShouldBeFalse();
        pipeline.Phase(PipelinePhases.Extract).Select(s => s.Id).ShouldBe(["xml", "map", "erpVendor", "prefill", "llm"]);
        pipeline.Find("erpVendor")!.Source.ShouldBe(PipelineSources.Supplier);
        pipeline.Find("rules")!.Kind.ShouldBe(StepKindCatalogue.ValidateSchema);
        pipeline.Find("rules")!.Source.ShouldBe(PipelineSources.Layout);
        pipeline.Overridden.ShouldBeTrue();
    }

    [Fact]
    public void A_param_with_null_removes_the_parameter()
    {
        var pipeline = Resolve(PriceList, Layer(PipelineSources.Supplier, new PipelineOverride { Op = "param", Step = "llm", Params = new JsonObject { ["effort"] = null } }));

        pipeline.Find("llm")!.Params.ContainsKey("effort").ShouldBeFalse();
    }

    [Fact]
    public void An_override_for_a_step_the_pipeline_does_not_have_is_skipped_and_noted()
    {
        var pipeline = Resolve(Invoice, Layer(PipelineSources.Supplier, Disable("uom")));

        pipeline.Notes.ShouldHaveSingleItem().Message.ShouldContain("'uom'");
        pipeline.Steps.ShouldAllBe(s => s.Enabled);
    }

    public static TheoryData<string> Layers() => [PipelineSources.Group, PipelineSources.Supplier, PipelineSources.Layout];

    [Theory]
    [MemberData(nameof(Layers))]
    public void Disabling_replacing_or_inserting_ahead_of_classify_identification_is_refused_at_every_layer(string source)
    {
        var disable = PipelineResolver.Resolve(Invoice, [Layer(source, Disable(StepKindCatalogue.ClassifyIdentification))]);
        var replace = PipelineResolver.Resolve(Invoice, [Layer(source, Replace(StepKindCatalogue.ClassifyIdentification, new StepDefinition { Id = "x", Kind = StepKindCatalogue.ClassifyKeyword }))]);
        var parameters = PipelineResolver.Resolve(Invoice, [Layer(source, Param(StepKindCatalogue.ClassifyIdentification, ("skip", true)))]);
        var ahead = PipelineResolver.Resolve(Invoice, [Layer(source, InsertAfter("split.llm", Lookup("early")))]);
        var classifyKind = PipelineResolver.Resolve(Invoice, [Layer(source, InsertAfter(StepKindCatalogue.ClassifyIdentification, new StepDefinition { Id = "kw2", Kind = StepKindCatalogue.ClassifyKeyword }))]);

        foreach (var refused in new[] { disable, replace, parameters, ahead, classifyKind })
        {
            refused.Succeeded.ShouldBeFalse();
            refused.Pipeline.ShouldBeNull();
            refused.Problems.ShouldNotBeEmpty();
        }

        disable.Problems[0].Message.ShouldContain("classify.identification");
        replace.Problems[0].Message.ShouldContain("classify.identification");
        ahead.Problems.ShouldContain(p => p.Message.Contains("of the extract phase", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Layers))]
    public void The_review_gate_and_the_publish_steps_are_fixed_too(string source)
    {
        PipelineResolver.Resolve(Invoice, [Layer(source, Disable(StepKindCatalogue.ReviewGate))]).Succeeded.ShouldBeFalse();
        PipelineResolver.Resolve(Invoice, [Layer(source, Disable(StepKindCatalogue.PublishFanout))]).Succeeded.ShouldBeFalse();
        PipelineResolver.Resolve(Invoice, [Layer(source, Disable(StepKindCatalogue.MatchVatIban))]).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void The_save_check_refuses_the_fixed_steps_by_id_before_any_type_is_known()
    {
        var problems = PipelineResolver.Check(
            [
                Disable(StepKindCatalogue.ClassifyIdentification),
                Replace(StepKindCatalogue.ClassifyIdentification, new StepDefinition { Id = "x", Kind = StepKindCatalogue.ClassifyKeyword }),
                Disable(StepKindCatalogue.ReviewGate),
                InsertAfter("llm", new StepDefinition { Id = "kw", Kind = StepKindCatalogue.ClassifyKeyword }),
                InsertAfter("split.llm", Lookup("early")),
                new PipelineOverride { Op = "remove", Step = "llm" },
                Param("llm", ("effort", "extreme")),
                Param("classify.llm", ("threshold", 2)),
                InsertAfter("map", new StepDefinition { Id = "map", Kind = StepKindCatalogue.HttpLookup, Params = new JsonObject { ["destinationId"] = LookupDestination, ["response"] = "{}" } }),
                InsertAfter("map", new StepDefinition { Id = "bad", Kind = StepKindCatalogue.HttpLookup, Params = new JsonObject { ["response"] = "{}" } }),
            ],
            "settings.pipelineOverrides");

        problems.Select(p => p.Path).ShouldBe(
        [
            "settings.pipelineOverrides[0].step",
            "settings.pipelineOverrides[1].step",
            "settings.pipelineOverrides[2].step",
            "settings.pipelineOverrides[3].with.kind",
            "settings.pipelineOverrides[4].step",
            "settings.pipelineOverrides[5].op",
            "settings.pipelineOverrides[6].params",
            "settings.pipelineOverrides[7].params",
            "settings.pipelineOverrides[8].with.id",
            "settings.pipelineOverrides[9].with.params",
        ]);
        problems[0].Message.ShouldContain("fixed first step of Classify");
    }

    [Fact]
    public void A_valid_layer_passes_the_save_check()
    {
        PipelineResolver.Check(
            [
                Disable("llm"),
                Param("llm", ("effort", "medium"), ("maxCostPerDocument", 0.5)),
                Param("classify.llm", ("threshold", 0.6)),
                Param("split.heuristic", ("mode", "never")),
                InsertAfter("map", Lookup("erpVendor")),
            ],
            "settings.pipelineOverrides").ShouldBeEmpty();
    }

    [Fact]
    public void More_than_five_lookups_are_refused()
    {
        var layer = Layer(PipelineSources.Supplier, [.. Enumerable.Range(1, 6).Select(i => InsertAfter("map", Lookup($"lookup{i}")))]);

        PipelineResolver.Resolve(Invoice, [layer]).Succeeded.ShouldBeFalse();
        PipelineResolver.Check(layer.Overrides, "x").ShouldContain(p => p.Message.Contains("at most 5", StringComparison.Ordinal));
    }

    [Fact]
    public void Overrides_read_strictly_from_json()
    {
        PipelineOverridesJson.TryRead(JsonNode.Parse("""[{"op":"disable","step":"llm"}]"""), out var error).ShouldNotBeNull();
        error.ShouldBeNull();
        PipelineOverridesJson.TryRead(JsonNode.Parse("""[{"op":"disable","step":"llm","extra":1}]"""), out error).ShouldBeNull();
        error.ShouldNotBeNull();
        PipelineOverridesJson.TryRead(JsonNode.Parse("""{"op":"disable"}"""), out error).ShouldBeNull();
    }

    [Fact]
    public void Effective_type_with_a_pipeline_runs_its_enabled_validate_steps()
    {
        var type = new EffectiveType("Invoice", Invoice, DefinitionOrigin.Adopted, "std.invoice", 1);
        var pipeline = Resolve(Invoice, Layer(PipelineSources.Supplier, Disable("duplicate")));

        type.RunsValidateStep(ValidateStepKinds.Duplicate).ShouldBeTrue();
        var applied = type.WithPipeline(pipeline);
        applied.RunsValidateStep(ValidateStepKinds.Duplicate).ShouldBeFalse();
        applied.RunsValidateStep(ValidateStepKinds.Rules).ShouldBeTrue();
        applied.Rules.ShouldBe(type.Rules);

        // The type's own pipeline answers as the definition alone does.
        var own = type.WithPipeline(PipelineResolver.Baseline(Invoice));
        foreach (var kind in new[] { ValidateStepKinds.Schema, ValidateStepKinds.Rules, ValidateStepKinds.Duplicate })
        {
            own.RunsValidateStep(kind).ShouldBe(type.RunsValidateStep(kind), kind);
        }
    }

    [Fact]
    public void Every_template_resolves_and_its_validate_steps_match_the_definition()
    {
        foreach (var template in StandardTemplates.All)
        {
            var type = new EffectiveType(template.Key, template, DefinitionOrigin.Adopted, template.Key, template.Version);
            var pipeline = PipelineResolver.Baseline(template);
            foreach (var kind in new[] { ValidateStepKinds.Schema, ValidateStepKinds.Rules, ValidateStepKinds.Duplicate })
            {
                type.WithPipeline(pipeline).RunsValidateStep(kind).ShouldBe(type.RunsValidateStep(kind), $"{template.Key} {kind}");
            }
        }
    }

    [Fact]
    public void The_catalogue_lists_every_phase_and_each_params_schema_is_an_object_schema()
    {
        StepKindCatalogue.Available.Values.Select(k => k.Phase).Distinct().Order().ShouldBe(PipelinePhases.All.Order());
        StepKindCatalogue.Available.Values.ShouldAllBe(k => k.ParamsSchema["type"]!.GetValue<string>() == "object");
        StepKindCatalogue.Available.Values.Where(k => k.Insertable).Select(k => k.Kind).ShouldBe([StepKindCatalogue.HttpLookup]);
        StepKinds.Extract.ShouldAllBe(k => StepKindCatalogue.Find(k)!.Phase == PipelinePhases.Extract);
        StepKinds.Validate.ShouldAllBe(k => StepKindCatalogue.Find(k)!.Phase == PipelinePhases.Validate);
    }

    private static ResolvedPipeline Resolve(TypeDefinition definition, params PipelineLayer[] layers)
    {
        var resolution = PipelineResolver.Resolve(definition, layers);
        resolution.Problems.ShouldBeEmpty();
        return resolution.Pipeline!;
    }

    private static PipelineLayer Layer(string source, params PipelineOverride[] overrides) => new(source, "ref", overrides);

    private static PipelineOverride Disable(string step) => new() { Op = PipelineOverrideOps.Disable, Step = step };

    private static PipelineOverride Param(string step, params (string Name, object Value)[] values)
    {
        var parameters = new JsonObject();
        foreach (var (name, value) in values)
        {
            parameters[name] = JsonValue.Create(value);
        }

        return new PipelineOverride { Op = PipelineOverrideOps.Param, Step = step, Params = parameters };
    }

    private static PipelineOverride InsertAfter(string step, StepDefinition with) => new() { Op = PipelineOverrideOps.InsertAfter, Step = step, With = with };

    private static PipelineOverride Replace(string step, StepDefinition with) => new() { Op = PipelineOverrideOps.Replace, Step = step, With = with };

    private static StepDefinition Lookup(string id) => new()
    {
        Id = id,
        Kind = StepKindCatalogue.HttpLookup,
        Params = new JsonObject { ["destinationId"] = LookupDestination, ["request"] = "{\"vat\": supplier.vatId}", ["response"] = "{\"ext.erpVendor\": response.id}" },
    };
}
