using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Llm.Schema;
using Json.Schema;

namespace DocProc.Integration.Tests;

/// <summary>
/// The meta-schema (docs/11 §5.2), the compiler and its four artifacts (§5.5), the size caps and publish checks
/// (§5.7) and the P1 price-list templates (§5.8).
/// </summary>
public sealed class DefinitionCompilerTests
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static TheoryData<string> TemplateKeys() => [.. StandardTemplates.All.Select(t => t.Key)];

    // ------------------------------------------------------------------ meta-schema

    [Fact]
    public void The_committed_meta_schema_is_the_generated_one()
    {
        var path = Path.Combine(RepositoryRoot(), "schemas", $"{DefinitionMetaSchema.Name}.v{DefinitionMetaSchema.Version}.json");
        if (Environment.GetEnvironmentVariable("UPDATE_SCHEMAS") == "1")
        {
            File.WriteAllText(path, DefinitionMetaSchema.SnapshotText);
        }

        File.Exists(path).ShouldBeTrue($"Missing snapshot {path}. Run the tests once with UPDATE_SCHEMAS=1.");
        File.ReadAllText(path).ReplaceLineEndings().ShouldBe(DefinitionMetaSchema.SnapshotText.ReplaceLineEndings(),
            "The meta-schema changed; if intended, bump DefinitionMetaSchema.Version for a breaking change or rerun with UPDATE_SCHEMAS=1.");
    }

    [Fact]
    public void The_meta_schema_is_a_2020_12_schema_that_closes_every_record()
    {
        var schema = DefinitionMetaSchema.Schema;

        schema["$schema"]!.GetValue<string>().ShouldBe("https://json-schema.org/draft/2020-12/schema");
        schema["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(["key", "version", "name", "fields"], ignoreOrder: true);
        _ = JsonSchema.FromText(schema.ToJsonString(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
    }

    [Theory]
    [MemberData(nameof(TemplateKeys))]
    public void Every_template_as_shipped_conforms_to_the_meta_schema(string key) =>
        DefinitionMetaSchema.Validate(StandardTemplates.ReadJson(key)).ShouldBeEmpty();

    [Theory]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[],"colour":"red"}""", "colour")]
    [InlineData("""{"key":"1x","version":1,"name":{"en":"X"},"fields":[]}""", "/key")]
    [InlineData("""{"key":"x","version":0,"name":{"en":"X"},"fields":[]}""", "/version")]
    [InlineData("""{"key":"x","version":1,"name":{},"fields":[]}""", "/name")]
    [InlineData("""{"key":"x","version":1,"name":{"english":"X"},"fields":[]}""", "/name")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"colour"}]}""", "/fields/0/kind")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"a","key":"a","kind":"string"}]}""", "/fields/0/id")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"array","item":{"fields":[{"id":"fld_b","key":"B","kind":"string"}]}}]}""", "/fields/0/item/fields/0/key")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[],"rules":[{"id":"r","kind":"range","severity":"fatal"}]}""", "/rules/0/severity")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[],"processing":{"review":{"priority":"asap"}}}""", "/processing/review/priority")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[],"processing":{"review":{"minConfidence":2}}}""", "/processing/review/minConfidence")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[],"classification":{"keywords":{"deu":["Rechnung"]}}}""", "/classification/keywords")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"array","maxItems":20001,"item":{"fields":[{"id":"fld_b","key":"b","kind":"string"}]}}]}""", "/fields/0/maxItems")]
    [InlineData("""{"key":"x","version":1,"name":{"en":"X"},"fields":"none"}""", "/fields")]
    public void The_meta_schema_names_where_a_definition_breaks_it(string json, string where)
    {
        var problems = DefinitionMetaSchema.Validate(json);

        problems.ShouldNotBeEmpty();
        string.Join("\n", problems).ShouldContain(where);
    }

    [Fact]
    public void Tenant_text_is_length_capped()
    {
        var guidance = new string('g', DefinitionMetaSchema.MaxGuidanceLength + 1);
        var description = new string('d', DefinitionMetaSchema.MaxDescriptionLength + 1);

        DefinitionMetaSchema.Validate($$$"""{"key":"x","version":1,"name":{"en":"X"},"fields":[],"llm":{"guidance":"{{{guidance}}}"}}""").ShouldNotBeEmpty();
        DefinitionMetaSchema.Validate($$$"""{"key":"x","version":1,"name":{"en":"X"},"fields":[{"id":"fld_a","key":"a","kind":"string","description":"{{{description}}}"}]}""").ShouldNotBeEmpty();
        DefinitionMetaSchema.Validate("not json").Single().Message.ShouldContain("not JSON");
    }

    // ------------------------------------------------------------------ compiler

    [Theory]
    [MemberData(nameof(TemplateKeys))]
    public void Every_template_compiles_to_a_portable_llm_schema_and_a_valid_json_schema(string key)
    {
        var compiled = DefinitionSchemaCompiler.Compile(StandardTemplates.Find(key)!);

        compiled.Problems.ShouldBeEmpty();
        var artifacts = compiled.Compiled.ShouldNotBeNull();
        SchemaPortability.Check(artifacts.LlmSchema.Schema).ShouldBeEmpty();
        _ = JsonSchema.FromText(artifacts.JsonSchemaJson, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        artifacts.ContentHash.Length.ShouldBe(64);
        artifacts.LlmSchema.Hash.Length.ShouldBe(16);
    }

    [Theory]
    [MemberData(nameof(TemplateKeys))]
    public void The_compiled_template_schemas_match_their_snapshots(string key)
    {
        var template = StandardTemplates.Find(key)!;
        var compiled = DefinitionSchemaCompiler.Compile(template).Compiled!;
        var snapshot = new JsonObject
        {
            ["jsonSchema"] = compiled.JsonSchema.DeepClone(),
            ["llmSchema"] = new JsonObject
            {
                ["name"] = compiled.LlmSchema.Name,
                ["version"] = compiled.LlmSchema.Version,
                ["adapter"] = compiled.LlmSchema.Adapter,
                // The adapter's schema is snapshotted on its own (invoice_extraction.v1.json); only its identity here.
                ["schema"] = compiled.LlmSchema.Adapter is null ? compiled.LlmSchema.Schema.DeepClone() : null,
            },
            // The review form's input (docs/11 §12.2): the web package's tests render these specs, so a compiler change
            // that would change the generated form shows up here first.
            ["formSpec"] = compiled.FormSpec.DeepClone(),
        };
        var text = JsonSerializer.Serialize(snapshot, Pretty) + Environment.NewLine;
        var path = Path.Combine(RepositoryRoot(), "schemas", "templates", $"{key}.v{template.Version}.json");
        if (Environment.GetEnvironmentVariable("UPDATE_SCHEMAS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        File.Exists(path).ShouldBeTrue($"Missing snapshot {path}. Run the tests once with UPDATE_SCHEMAS=1.");
        File.ReadAllText(path).ReplaceLineEndings().ShouldBe(text.ReplaceLineEndings(),
            $"The compiled schemas of {key} changed. A published template version is immutable: ship a new version instead, or rerun with UPDATE_SCHEMAS=1 for a compiler fix.");
    }

    [Theory]
    [InlineData("std.invoice")]
    [InlineData("std.credit-note")]
    public void The_invoice_family_compiles_to_invoice_extraction_v1_byte_for_byte(string key)
    {
        var llm = DefinitionSchemaCompiler.Compile(StandardTemplates.Find(key)!).Compiled!.LlmSchema;

        llm.Name.ShouldBe("invoice_extraction");
        llm.Version.ShouldBe("1");
        llm.Adapter.ShouldBe(DefinitionCompiler.InvoiceAdapter);
        llm.CustomPaths.ShouldBeEmpty();
        DefinitionSchemaCompiler.ToDefinition(llm).Json.ShouldBe(CanonicalSchemas.InvoiceExtraction.Json);
    }

    [Fact]
    public void A_fork_with_custom_fields_adds_a_nullable_x_custom_object_and_keeps_the_rest()
    {
        var fork = Fork("std.credit-note", Field("fld_c_scr", "supplierCustomerRef", "identifier", description: "Our account number at the supplier."), Field("fld_c_flag", "returnApproved", "boolean"));

        var compiled = DefinitionSchemaCompiler.Compile(fork);

        var llm = compiled.Compiled.ShouldNotBeNull().LlmSchema;
        llm.Name.ShouldBe("invoice_extraction");
        llm.Version.ShouldStartWith("1+x.");
        llm.Version.Length.ShouldBe("1+x.".Length + 8);
        llm.CustomPaths.ShouldBe(["supplierCustomerRef", "returnApproved"]);
        SchemaPortability.Check(llm.Schema).ShouldBeEmpty();
        var properties = llm.Schema["properties"]!.AsObject();
        properties.Select(p => p.Key).TakeLast(2).ShouldBe([DefinitionCompiler.CustomObject, DefinitionCompiler.UnreadableRegions]);
        var custom = properties[DefinitionCompiler.CustomObject]!.AsObject();
        custom["type"]!.AsArray().Select(t => t!.GetValue<string>()).ShouldBe(["object", "null"]);
        custom["properties"]!["supplierCustomerRef"]!["description"]!.GetValue<string>().ShouldBe("Our account number at the supplier.");
        llm.Schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldContain(DefinitionCompiler.CustomObject);

        // Every property of the adapter's schema is still there, unchanged.
        var original = CanonicalSchemas.InvoiceExtraction.Schema["properties"]!.AsObject();
        foreach (var (name, schema) in original)
        {
            JsonNode.DeepEquals(properties[name], schema).ShouldBeTrue(name);
        }
    }

    [Fact]
    public void A_line_level_custom_field_of_an_invoice_fork_is_noted_not_extracted()
    {
        var template = StandardTemplates.Find("std.invoice")!;
        var lines = template.Fields.Single(f => f.Key == "lines");
        var fork = template with
        {
            Key = "Invoice",
            LegacyKey = null,
            Base = new DefinitionBase { Template = "std.invoice", Version = 1, Mode = "copy" },
            Fields = [.. template.Fields.Select(f => f == lines ? f with { Item = new FieldGroup { Fields = [.. lines.Item!.Fields, Field("fld_l_store", "storeNo", "string")] } } : f)],
        };

        var result = DefinitionSchemaCompiler.Compile(fork);

        result.Problems.ShouldBeEmpty();
        result.Compiled!.LlmSchema.Version.ShouldBe("1");
        result.Notes.Single().Where.ShouldBe("lines[*].storeNo");
    }

    [Fact]
    public void A_type_without_an_adapter_gets_its_own_schema_named_after_its_key()
    {
        var compiled = DefinitionSchemaCompiler.Compile(ReturnSlip()).Compiled!;

        var llm = compiled.LlmSchema;
        llm.Name.ShouldBe("doc_return-slip");
        llm.Version.ShouldBe(llm.Hash);
        llm.Adapter.ShouldBeNull();
        var properties = llm.Schema["properties"]!.AsObject();
        properties.Select(p => p.Key).ShouldBe(["returnNumber", "returnDate", "supplier", "deliveryNoteReference", "items", DefinitionCompiler.UnreadableRegions]);
        properties["returnNumber"]!["type"]!.AsArray().Select(t => t!.GetValue<string>()).ShouldBe(["string", "null"]);
        properties["returnDate"]!["format"]!.GetValue<string>().ShouldBe("date");
        properties["supplier"]!["properties"]!["address"]!["properties"]!["city"].ShouldNotBeNull("composites are inlined, without $ref");
        properties["items"]!["type"]!.GetValue<string>().ShouldBe("array");
        var reason = properties["items"]!["items"]!["properties"]!["reason"]!;
        reason["enum"]!.AsArray().Select(v => v?.GetValue<string>()).ShouldBe(["Damaged", "Wrong", "Surplus", null]);
        properties["items"]!["items"]!["properties"]!["quantity"]!["minimum"].ShouldBeNull("the LLM schema carries no numeric constraints");
        SchemaPortability.Check(llm.Schema).ShouldBeEmpty();
    }

    [Fact]
    public void The_json_schema_describes_the_values_view_with_required_fields_non_null()
    {
        var schema = DefinitionSchemaCompiler.Compile(ReturnSlip()).Compiled!.JsonSchemaJson;
        var validator = JsonSchema.FromText(schema, new BuildOptions { SchemaRegistry = new SchemaRegistry() });

        bool Valid(string view) => validator.Evaluate(JsonDocument.Parse(view).RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;

        Valid("""{"returnNumber":"R-1","returnDate":"2026-11-02","supplier":{"name":"Nordwind","vatId":null},"items":[{"description":"Kaffee","quantity":2,"reason":"Damaged"}]}""").ShouldBeTrue();
        Valid("""{"returnNumber":null,"returnDate":"2026-11-02","items":[]}""").ShouldBeFalse("a required field is never null");
        Valid("""{"returnNumber":"R-1","returnDate":"2026-11-02","items":[{"description":"Kaffee","quantity":-1}]}""").ShouldBeFalse("min 0");
        Valid("""{"returnNumber":"R-1","returnDate":"2026-11-02","items":[{"description":"Kaffee","reason":"Lost"}]}""").ShouldBeFalse("not an option");
        Valid("""{"returnNumber":"R-1","returnDate":"2026-11-02","items":[{"quantity":1}]}""").ShouldBeFalse("a row's required field");
    }

    [Fact]
    public void The_price_list_caps_its_rows_at_ten_thousand_in_the_json_schema()
    {
        var schema = DefinitionSchemaCompiler.Compile(StandardTemplates.Find("std.price-list")!).Compiled!.JsonSchema;

        schema["properties"]!["lines"]!["maxItems"]!.GetValue<int>().ShouldBe(10_000);
        schema["properties"]!["lines"]!["items"]!["properties"]!["price"]!["properties"]!["amount"]!["type"]!.GetValue<string>().ShouldBe("number");
        schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(["validFrom", "currency", "supplier"]);
    }

    [Fact]
    public void The_descriptor_index_lists_every_field_by_path_and_finds_concrete_paths()
    {
        var invoice = StandardTemplates.Find("std.invoice")!;
        var index = DefinitionSchemaCompiler.Compile(invoice).Compiled!.Descriptors;

        index.Fields.Select(f => f.Path).ShouldBe(DefinitionPaths.Enumerate(invoice).Select(f => f.Path));
        index.Fields.Where(f => f.IsEnvelope).Select(f => f.IsList ? $"{f.Path}[*]" : f.Path).ShouldBe(DefinitionPaths.EnvelopePaths(invoice));
        index.Find("lines[3].lineNet")!.Kind.ShouldBe(FieldKinds.Money);
        index.Find("lines[3].lineNet")!.CurrencyFrom.ShouldBe("currency");
        index.Find("orderReferences[0]")!.IsList.ShouldBeTrue();
        index.Find("lines")!.Children.ShouldContain("lines[*].lineNet");
        index.Find("supplier")!.Members.Select(m => m.Key).ShouldBe(FieldKinds.CompositeMembers[FieldKinds.Party]);
        index.Find("taxBreakdown")!.Provenance.ShouldBeFalse();
        index.Find("nothing").ShouldBeNull();

        var again = DescriptorIndex.FromJson(index.ToJson());
        again.Fields.Select(f => (f.Path, f.Kind, string.Join(",", f.Children), string.Join(",", f.Members), f.Required, f.Provenance))
            .ShouldBe(index.Fields.Select(f => (f.Path, f.Kind, string.Join(",", f.Children), string.Join(",", f.Members), f.Required, f.Provenance)));
    }

    [Fact]
    public void The_invoice_envelope_paths_of_the_descriptors_are_the_records_fields()
    {
        var index = DefinitionSchemaCompiler.Compile(StandardTemplates.Find("std.invoice")!).Compiled!.Descriptors;
        var invoice = InvoiceWithOneLine();

        foreach (var (path, _) in invoice.EnumerateAllFields())
        {
            index.Find(path).ShouldNotBeNull(path).IsEnvelope.ShouldBeTrue(path);
        }
    }

    [Fact]
    public void The_form_spec_has_the_sections_and_a_tree_of_field_specs()
    {
        var form = DefinitionSchemaCompiler.Compile(StandardTemplates.Find("std.price-list")!).Compiled!.FormSpec;

        form["sections"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()).ShouldBe(["list", "supplier", "lines"]);
        var fields = form["fields"]!.AsArray();
        var lines = fields.Single(f => f!["key"]!.GetValue<string>() == "lines")!;
        lines["maxItems"]!.GetValue<int>().ShouldBe(10_000);
        lines["anchor"]!.GetValue<string>().ShouldBe("supplierSku");
        var price = lines["children"]!.AsArray().Single(f => f!["key"]!.GetValue<string>() == "price")!;
        price["path"]!.GetValue<string>().ShouldBe("lines[*].price");
        price["currencyPath"]!.GetValue<string>().ShouldBe("currency");
        price["required"]!.GetValue<bool>().ShouldBeTrue();
        var scope = fields.Single(f => f!["key"]!.GetValue<string>() == "listScope")!;
        scope["options"]!.AsArray().Select(o => o!["code"]!.GetValue<string>()).ShouldBe(["Full", "Changes"]);
        scope["label"]!["de"]!.GetValue<string>().ShouldBe("Umfang");
        fields.Single(f => f!["key"]!.GetValue<string>() == "supplier")!["members"]!.AsArray().Count.ShouldBe(9);
    }

    [Fact]
    public void Fields_are_ordered_by_order_then_as_declared_in_the_form()
    {
        var definition = ReturnSlip() with
        {
            Fields = [.. ReturnSlip().Fields.Select(f => f.Key == "items" ? f with { Order = 1 } : f)],
        };

        var form = DefinitionSchemaCompiler.Compile(definition).Compiled!.FormSpec;

        form["fields"]!.AsArray().Select(f => f!["key"]!.GetValue<string>()).ShouldBe(["items", "returnNumber", "returnDate", "supplier", "deliveryNoteReference"]);
    }

    [Fact]
    public void The_size_caps_stop_the_compile()
    {
        var many = Enumerable.Range(0, DefinitionCompiler.MaxFields + 1).Select(i => Field($"fld_f{i}", $"f{i}", "string")).ToList();
        Compile(Definition(many)).Problems.ShouldContain(p => p.Message.Contains("at most 300"));

        FieldDefinition Nested(int depth) => depth == 0
            ? Field("fld_leaf", "leaf", "string")
            : Field($"fld_o{depth}", $"o{depth}", "object") with { Fields = [Nested(depth - 1)] };
        Compile(Definition([Nested(DefinitionCompiler.MaxDepth)])).Problems.ShouldContain(p => p.Message.Contains("levels deep"));
        Compile(Definition([Nested(DefinitionCompiler.MaxDepth - 1)])).Problems.ShouldBeEmpty();

        var options = Enumerable.Range(0, DefinitionCompiler.MaxEnumValues + 1).Select(i => new EnumOption { Code = $"C{i}" }).ToList();
        Compile(Definition([Field("fld_e", "e", "enum") with { Options = options }])).Problems.ShouldContain(p => p.Message.Contains("at most 200"));

        static CompileResult Compile(TypeDefinition definition) => DefinitionSchemaCompiler.Compile(definition);
    }

    [Fact]
    public void An_adapter_this_deployment_does_not_have_stops_the_compile()
    {
        var definition = ReturnSlip() with { Llm = new LlmDefinition { Adapter = "invoice_extraction@9" } };

        DefinitionSchemaCompiler.Compile(definition).Problems.Single().Message.ShouldContain("invoice_extraction@9");
    }

    [Fact]
    public void The_content_hash_ignores_formatting_and_member_order()
    {
        var a = DefinitionSerializer.Parse("""{"key":"x","version":1,"name":{"en":"X","de":"Y"},"fields":[{"id":"fld_a","key":"a","kind":"decimal","min":1.0}]}""");
        var b = DefinitionSerializer.Parse("""
            {
              "version": 1, "key": "x", "fields": [ { "kind": "decimal", "key": "a", "id": "fld_a", "min": 1 } ],
              "name": { "de": "Y", "en": "X" }
            }
            """);

        DefinitionSchemaCompiler.Compile(a).Compiled!.ContentHash.ShouldBe(DefinitionSchemaCompiler.Compile(b).Compiled!.ContentHash);
        DefinitionSchemaCompiler.Compile(a with { Version = 2 }).Compiled!.ContentHash.ShouldNotBe(DefinitionSchemaCompiler.Compile(a).Compiled!.ContentHash);
    }

    [Theory]
    [InlineData(0d, "0")]
    [InlineData(1d, "1")]
    [InlineData(-1.5, "-1.5")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.0000001, "1e-7")]
    [InlineData(20000d, "20000")]
    [InlineData(1e21, "1e+21")]
    [InlineData(123456789012345680000d, "123456789012345680000")]
    [InlineData(0.02, "0.02")]
    public void Numbers_are_written_in_the_ecmascript_form_of_rfc_8785(double value, string expected) =>
        JsonCanonicalizer.FormatNumber(value).ShouldBe(expected);

    [Fact]
    public void Canonical_json_sorts_members_and_escapes_minimally()
    {
        var node = JsonNode.Parse("""{"b":1,"a":[true,null,"x\"yä\n"],"A":{"z":2.50,"y":"€"}}""");

        JsonCanonicalizer.Canonicalize(node).ShouldBe("{\"A\":{\"y\":\"€\",\"z\":2.5},\"a\":[true,null,\"x\\\"yä\\n\"],\"b\":1}");
    }

    // ------------------------------------------------------------------ publish checks

    [Fact]
    public void Publishing_runs_the_meta_schema_first()
    {
        var report = Publish("""{"key":"x","version":1,"name":{"en":"X"},"fields":[],"colour":"red"}""");

        report.CanPublish.ShouldBeFalse();
        report.Problems.ShouldNotBeEmpty();
        report.Definition.ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"id":"r","kind":"tableComplete"}""", "rule kind 'tableComplete' is not available yet")]
    [InlineData("""{"id":"r","kind":"range","params":{"path":"items[*].quantity","minimum":1}}""", "do not fit the kind 'range'")]
    [InlineData("""{"id":"r","kind":"range","params":{"min":1}}""", "do not fit the kind 'range'")]
    [InlineData("""{"id":"r","kind":"range","params":{"path":"items[*].weight","min":1}}""", "path 'items[*].weight' is not a field")]
    [InlineData("""{"id":"r","kind":"dateOrder","params":{"before":"returnDate","after":"shippedDate"}}""", "after 'shippedDate' is not a field")]
    [InlineData("""{"id":"r","kind":"invoice.totals","params":{"tolerance":0.02}}""", "do not fit the kind 'invoice.totals'")]
    [InlineData("""{"id":"r","kind":"duplicate","params":{"onMatch":"merge"}}""", "do not fit the kind 'duplicate'")]
    public void Rule_parameters_are_checked_against_the_kind_and_the_fields(string rule, string expected)
    {
        var report = Publish(ReturnSlipJson(rules: $"[{rule}]"));

        report.CanPublish.ShouldBeFalse();
        string.Join("\n", report.Problems).ShouldContain(expected);
    }

    [Fact]
    public void Rule_paths_may_address_the_members_of_a_value()
    {
        var report = Publish(ReturnSlipJson(rules: """[{"id":"r","kind":"pattern","params":{"path":"supplier.address.countryCode","pattern":"^[A-Z]{2}$"}},{"id":"r2","kind":"range","params":{"path":"items[*].quantity","min":0.001}}]"""));

        report.Problems.ShouldBeEmpty();
        report.CanPublish.ShouldBeTrue();
    }

    [Fact]
    public void A_duplicate_key_component_must_be_indexed()
    {
        var report = Publish(ReturnSlipJson(duplicateKey: """["$supplierId","returnNumber","returnDate"]"""));

        string.Join("\n", report.Problems).ShouldContain("'returnDate' is not indexed");
    }

    [Fact]
    public void A_removed_or_retyped_field_with_dependents_blocks_and_a_rename_warns()
    {
        var published = DefinitionSerializer.Parse(ReturnSlipJson());
        var dependents = new[]
        {
            new DependentReference("outputMapping", "erp-returns@2", "fld_dn", "deliveryNoteReference"),
            new DependentReference("supplierLayout", "nordwind-return@1", "fld_rd", "returnDate"),
        };

        var removed = PublishChecks.Run(new PublishCheckInput { DefinitionJson = ReturnSlipJson(version: 2, withoutDeliveryNote: true), Published = published, Dependents = dependents });
        removed.CanPublish.ShouldBeFalse();
        removed.BlockingDependents.Single().Name.ShouldBe("erp-returns@2");
        removed.Changes.ShouldContain(c => c.Change == FieldChangeKind.Removed && c.FieldId == "fld_dn");

        var renamed = PublishChecks.Run(new PublishCheckInput { DefinitionJson = ReturnSlipJson(version: 2, returnDateKey: "returnedOn"), Published = published, Dependents = dependents });
        renamed.CanPublish.ShouldBeTrue();
        renamed.Changes.ShouldContain(c => c.Change == FieldChangeKind.Renamed && c.OldPath == "returnDate" && c.NewPath == "returnedOn");
        renamed.Warnings.ShouldContain(w => w.Message.Contains("renamed to returnedOn"));

        var retyped = PublishChecks.Run(new PublishCheckInput { DefinitionJson = ReturnSlipJson(version: 2).Replace("\"key\":\"returnDate\",\"kind\":\"date\"", "\"key\":\"returnDate\",\"kind\":\"string\"", StringComparison.Ordinal), Published = published, Dependents = dependents });
        retyped.CanPublish.ShouldBeFalse();
        string.Join("\n", retyped.Problems).ShouldContain("changes kind from date to string");

        var unreferenced = PublishChecks.Run(new PublishCheckInput { DefinitionJson = ReturnSlipJson(version: 2, withoutDeliveryNote: true), Published = published });
        unreferenced.CanPublish.ShouldBeTrue("a removed field nothing refers to does not block");
    }

    [Fact]
    public void The_indexed_fields_of_a_version_are_its_own_dependents()
    {
        var published = DefinitionSerializer.Parse(ReturnSlipJson());

        PublishChecks.IndexedKeys(published).Select(d => d.Path).ShouldBe(["returnNumber"]);
    }

    // ------------------------------------------------------------------ price-list templates

    [Fact]
    public void The_price_list_templates_v1_are_always_reviewed_and_use_only_P1_rule_kinds()
    {
        foreach (var key in new[] { "std.price-list", "std.daily-specials" })
        {
            var template = StandardTemplates.Find(key)!;

            template.Version.ShouldBe(1);
            template.Processing.Review.AutoApprove.ShouldBeFalse(key);
            template.Rules.Select(r => RuleKinds.Available.GetValueOrDefault(r.Kind)?.Phase).ShouldAllBe(phase => phase == RuleKinds.P1);
            template.Roles.ValidFrom.ShouldBe("validFrom");
            template.Roles.ValidUntil.ShouldBe("validUntil");
            template.Roles.LineSku.ShouldBe("lines[*].supplierSku");
            template.Roles.Total.ShouldBeNull("a price list has no totals");
            template.Processing.Split!.Mode.ShouldBe("never");
            DefinitionPaths.Find(template, "lines")!.MaxItems.ShouldBe(10_000);
            DefinitionPaths.Enumerate(template).Where(f => f.Path.StartsWith("lines[*].", StringComparison.Ordinal)).ShouldAllBe(f => !f.Field.Indexed);
            PublishChecks.Run(new PublishCheckInput { DefinitionJson = StandardTemplates.ReadJson(key) }).Problems.ShouldBeEmpty(key);
        }

        StandardTemplates.Find("std.price-list")!.Rules.Select(r => r.Id).ShouldBe(["r_validity", "r_from", "r_price_pos", "r_pack", "r_unit", "r_dup"]);
        StandardTemplates.Find("std.daily-specials")!.Rules.Select(r => r.Id).ShouldBe(["r_validity", "r_not_expired", "r_price_pos", "r_pack", "r_unit", "r_dup"]);
    }

    [Fact]
    public void The_daily_specials_share_the_price_list_field_tree_roles_and_ui()
    {
        var list = StandardTemplates.Find("std.price-list")!;
        var specials = StandardTemplates.Find("std.daily-specials")!;

        DefinitionSerializer.Write(specials with { Key = list.Key, Base = null, Name = list.Name, Description = list.Description, Rules = list.Rules, Processing = list.Processing, Classification = list.Classification, Llm = list.Llm })
            .ShouldBe(DefinitionSerializer.Write(list));
        specials.Classification!.Keywords!["de"].ShouldContain("Tagesangebot");
    }

    [Fact]
    public void A_template_version_the_seed_migration_froze_never_changes_in_place()
    {
        var frozen = Infrastructure.Catalogue.TypeCatalogueSeedSql.FrozenTemplates();

        frozen.Select(f => f.Template.Key).ShouldBe(StandardTemplates.All.Select(t => t.Key), "every template the build ships was seeded by the migration or comes with a later one");
        foreach (var (template, json) in frozen)
        {
            var live = StandardTemplates.Find(template.Key)!;
            if (live.Version == template.Version)
            {
                JsonCanonicalizer.Sha256Hex(JsonNode.Parse(StandardTemplates.ReadJson(template.Key))).ShouldBe(
                    JsonCanonicalizer.Sha256Hex(JsonNode.Parse(json)),
                    $"{template.Key} v{template.Version} is published; a change ships as a new version (docs/11 §5.7)");
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private static PublishReport Publish(string json) => DefinitionSchemaCompiler.RunPublishChecks(new PublishCheckInput { DefinitionJson = json });

    private static FieldDefinition Field(string id, string key, string kind, string? description = null) =>
        new() { Id = id, Key = key, Kind = kind, Description = description };

    private static TypeDefinition Definition(IReadOnlyList<FieldDefinition> fields) =>
        new() { Key = "test", Version = 1, Name = new Dictionary<string, string> { ["en"] = "Test" }, Fields = fields };

    private static TypeDefinition Fork(string template, params FieldDefinition[] added)
    {
        var source = StandardTemplates.Find(template)!;
        return source with
        {
            Key = source.LegacyKey!,
            LegacyKey = null,
            Version = 2,
            Base = new DefinitionBase { Template = template, Version = source.Version, Mode = "copy" },
            Fields = [.. source.Fields, .. added],
        };
    }

    private static TypeDefinition ReturnSlip() => DefinitionSerializer.Parse(ReturnSlipJson());

    /// <summary>The new type of docs/11 §5.4, the return slip.</summary>
    private static string ReturnSlipJson(int version = 1, string rules = """[{"id":"r_dup","kind":"duplicate"},{"id":"r_qty","kind":"range","params":{"path":"items[*].quantity","min":0.001}}]""", string duplicateKey = """["$supplierId","returnNumber"]""", bool withoutDeliveryNote = false, string returnDateKey = "returnDate")
    {
        var deliveryNote = withoutDeliveryNote ? string.Empty : """{"id":"fld_dn","key":"deliveryNoteReference","kind":"identifier"},""";
        return $$$"""
            {"key":"return-slip","version":{{{version}}},"base":{"template":"std.generic","version":1,"mode":"copy"},
             "name":{"en":"Return slip","de":"Retourenschein"},
             "roles":{"documentNumber":"returnNumber","documentDate":"{{{returnDateKey}}}","supplierParty":"supplier","lineItems":"items","duplicateKey":{{{duplicateKey}}}},
             "fields":[
               {"id":"fld_rn","key":"returnNumber","kind":"identifier","required":true,"indexed":true,"extraction":{"labels":{"de":["Retouren-Nr.","RMA"] } } },
               {"id":"fld_rd","key":"{{{returnDateKey}}}","kind":"date","required":true},
               {"id":"fld_sup","key":"supplier","kind":"party"},
               {{{deliveryNote}}}
               {"id":"fld_items","key":"items","kind":"array","anchor":"description","item":{"fields":[
                 {"id":"fld_i_sku","key":"supplierSku","kind":"identifier"},
                 {"id":"fld_i_desc","key":"description","kind":"text","required":true},
                 {"id":"fld_i_qty","key":"quantity","kind":"decimal","min":0},
                 {"id":"fld_i_reason","key":"reason","kind":"enum","options":[{"code":"Damaged"},{"code":"Wrong"},{"code":"Surplus"}]}
               ]}}
             ],
             "rules":{{{rules}}},
             "processing":{"extract":[{"id":"map","kind":"supplier-mapping"},{"id":"llm","kind":"llm","params":{"mode":"fill-missing"}}],"review":{"autoApprove":false}},
             "classification":{"description":"Goods returned to a supplier","keywords":{"de":["Retourenschein","Rücksendung"] } } }
            """;
    }

    private static Invoice InvoiceWithOneLine() => new()
    {
        OrderReferences = [FieldValue<DocumentIdentifier>.Missing()],
        DeliveryNoteReferences = [FieldValue<DocumentIdentifier>.Missing()],
        Lines = [new InvoiceLine { LineNumber = 1 }],
    };

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocProc.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root (DocProc.slnx).");
    }
}
