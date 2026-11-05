using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Domain.Validation.Rules;
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
    public void The_added_fields_are_told_apart_against_the_template_version_the_type_came_from()
    {
        var template = StandardTemplates.Find("std.invoice")!;
        StandardTemplates.Find("std.invoice", template.Version).ShouldBeSameAs(template);
        StandardTemplates.Find("std.invoice", template.Version + 1).ShouldBeNull();
        var fork = template with
        {
            Key = "Invoice",
            LegacyKey = null,
            Base = new DefinitionBase { Template = "std.invoice", Version = template.Version + 1, Mode = "copy" },
            Fields = [.. template.Fields, Field("fld_c_scr", "supplierCustomerRef", "identifier")],
        };

        var result = DefinitionSchemaCompiler.Compile(fork);

        result.Succeeded.ShouldBeFalse("x_custom against another version of the template would name other fields");
        result.Problems.Single().Where.ShouldBe("base");
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

    /// <summary>
    /// docs/11 §5.7: the rule list and the duplicate key are capped too. Every rule runs on every validation of a
    /// document and each duplicate-key component is one more EXISTS over the field index, so without a cap a definition
    /// under the 1 MiB length (thousands of pattern rules, dozens of key components) cost seconds per document. The
    /// caps hold in the compile (and so on save and publish) and in the meta-schema.
    /// </summary>
    [Fact]
    public void The_rule_list_and_the_duplicate_key_are_capped()
    {
        var fields = Enumerable.Range(0, DefinitionCompiler.MaxDuplicateKeyComponents + 1).Select(i => Field($"fld_k{i}", $"k{i}", "string") with { Indexed = true }).ToList();
        List<RuleDefinition> Rules(int count) => [.. Enumerable.Range(0, count).Select(i => new RuleDefinition { Id = $"r{i}", Kind = "pattern", Params = new JsonObject { ["path"] = "k0", ["pattern"] = $"^A{i}$" } })];
        DefinitionRoles Key(int count) => new() { DuplicateKey = [.. fields.Take(count).Select(f => f.Key)] };

        var atCaps = Definition(fields) with { Rules = Rules(DefinitionCompiler.MaxRules), Roles = Key(DefinitionCompiler.MaxDuplicateKeyComponents) };
        DefinitionCompiler.CheckSizeCaps(atCaps).ShouldBeEmpty();
        DefinitionSchemaCompiler.Compile(atCaps).Problems.ShouldBeEmpty();
        DefinitionMetaSchema.Validate(DefinitionSerializer.Write(atCaps)).ShouldBeEmpty();

        var tooMany = atCaps with { Rules = Rules(DefinitionCompiler.MaxRules + 1), Roles = Key(DefinitionCompiler.MaxDuplicateKeyComponents + 1) };
        var problems = DefinitionSchemaCompiler.Compile(tooMany).Problems;
        problems.ShouldContain(p => p.Where == "rules" && p.Message == $"lists {DefinitionCompiler.MaxRules + 1} rules; at most {DefinitionCompiler.MaxRules} are allowed.");
        problems.ShouldContain(p => p.Where == "roles.duplicateKey" && p.Message == $"has {DefinitionCompiler.MaxDuplicateKeyComponents + 1} components; at most {DefinitionCompiler.MaxDuplicateKeyComponents} are allowed.");
        var meta = DefinitionMetaSchema.Validate(DefinitionSerializer.Write(tooMany));
        meta.ShouldContain(p => p.Where == "/rules" && p.Message.Contains("(maxItems)"));
        meta.ShouldContain(p => p.Where == "/roles/duplicateKey" && p.Message.Contains("(maxItems)"));

        // Every standard template stays well inside the caps.
        foreach (var template in StandardTemplates.All)
        {
            DefinitionCompiler.CheckSizeCaps(template).ShouldBeEmpty(template.Key);
        }
    }

    /// <summary>
    /// The rule references of a definition object are built once, so what a rule kind compiles from its parameters
    /// (the regex of a pattern rule) is compiled once per published version, whose definition object the process-wide
    /// cache shares between resolves, instead of once per evaluation.
    /// </summary>
    [Fact]
    public void The_rule_references_of_a_definition_are_built_once()
    {
        var definition = Definition([Field("fld_k", "k", "string")]) with
        {
            Rules = [new RuleDefinition { Id = "r_p", Kind = "pattern", Params = new JsonObject { ["path"] = "k", ["pattern"] = "^A[0-9]+$" } }],
        };

        var references = definition.ToRuleReferences();
        definition.ToRuleReferences().ShouldBeSameAs(references);
        GenericRules.PatternRegexOf(definition.ToRuleReferences()[0].Params).ShouldNotBeNull().ShouldBeSameAs(GenericRules.PatternRegexOf(references[0].Params));
        (definition with { }).ToRuleReferences().ShouldNotBeSameAs(references, "another definition object has its own");
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
    [InlineData("""{"id":"r","kind":"columnSum"}""", "rule kind 'columnSum' is not available yet")]
    [InlineData("""{"id":"r","kind":"tableComplete"}""", "do not fit the kind 'tableComplete'")]
    [InlineData("""{"id":"r","kind":"unique","params":{"path":"returnNumber"}}""", "must name a field of rows")]
    [InlineData("""{"id":"r","kind":"unique","params":{"path":"items[*].sku","scopeBy":["batch"]}}""", "scopeBy 'batch' is not a field of the items of items[*]")]
    [InlineData("""{"id":"r","kind":"deltaToPrevious","params":{"path":"items[*].quantity","joinOn":"ean"}}""", "joinOn 'ean' is not a field of the items of items[*]")]
    [InlineData("""{"id":"r","kind":"ordered","params":{"path":"items","by":"position","then":{"path":"quantity","order":"sideways"}}}""", "do not fit the kind 'ordered'")]
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

    [Theory]
    [InlineData("""{"id":"r","kind":"requiredIf","params":{"path":"deliveryNoteReference","when":"returnDate >"}}""", "when is not an expression of the rule language")]
    [InlineData("""{"id":"r","kind":"requiredIf","params":{"path":"deliveryNoteReference","when":"$fetch('https://x')"}}""", "is not a function")]
    [InlineData("""{"id":"r","kind":"requiredIf","params":{"path":"deliveryNoteReference","when":"$env = 1"}}""", "is not a variable here")]
    [InlineData("""{"id":"r","kind":"compare","params":{"left":"returnDate","op":"ge"}}""", "needs right (a path) or value")]
    [InlineData("""{"id":"r","kind":"compare","params":{"left":"returnDate","op":"before","right":"returnDate"}}""", "do not fit the kind 'compare'")]
    [InlineData("""{"id":"r","kind":"sumEquals","params":{"path":"items[*].weight","equals":"returnNumber"}}""", "path 'items[*].weight' is not a field")]
    [InlineData("""{"id":"r","kind":"arithmetic","params":{"rows":"items[*]","expression":"quantity * 2","equals":"total"}}""", "equals 'total' is not a field of the rows")]
    [InlineData("""{"id":"r","kind":"expression","params":{"forEach":"items[*]","predicate":"quantity > 0","field":"weight"}}""", "field 'weight' is not a field of the rows")]
    [InlineData("""{"id":"r","kind":"expression","params":{"predicate":"1 = 1","limit":3}}""", "do not fit the kind 'expression'")]
    public void P5_rule_parameters_and_expressions_are_checked_at_publish(string rule, string expected)
    {
        var report = Publish(ReturnSlipJson(rules: $"[{rule}]"));

        report.CanPublish.ShouldBeFalse();
        string.Join("\n", report.Problems).ShouldContain(expected);
    }

    [Fact]
    public void The_P5_rule_kinds_publish()
    {
        var report = Publish(ReturnSlipJson(rules: """
            [{"id":"r_dn","kind":"requiredIf","params":{"path":"deliveryNoteReference","when":"$count(items[reason = 'Wrong']) > 0"}},
             {"id":"r_date","kind":"compare","params":{"left":"returnDate","op":"le","value":"2099-12-31"}},
             {"id":"r_sum","kind":"sumEquals","params":{"path":"items[*].quantity","equals":"items[*].quantity","tolerance":0}},
             {"id":"r_row","kind":"arithmetic","params":{"rows":"items[*]","expression":"quantity * 1","equals":"quantity"}},
             {"id":"r_expr","kind":"expression","params":{"forEach":"items[*]","predicate":"quantity > 0 or reason = 'Surplus'","field":"quantity"}}]
            """));

        report.Problems.ShouldBeEmpty();
        report.CanPublish.ShouldBeTrue();
    }

    [Fact]
    public void Definition_flags_are_declared_once_raised_by_a_generic_rule_and_never_a_platform_code()
    {
        static string WithFlags(string flags, string rules) => ReturnSlipJson(rules: rules).Replace("\"rules\":", $"\"flags\":{flags},\"rules\":", StringComparison.Ordinal);

        var ok = Publish(WithFlags(
            """[{"code":"X_RETURN_LATE","severity":"blocking","message":{"en":"Returned too late","de":"Zu spät zurückgegeben"}}]""",
            """[{"id":"r_late","kind":"compare","flag":"X_RETURN_LATE","params":{"left":"returnDate","op":"le","value":"2099-12-31"}}]"""));
        ok.Problems.ShouldBeEmpty();
        var reference = ok.Definition!.ToRuleReferences().Single();
        reference.FlagCode.ShouldBe("X_RETURN_LATE");
        reference.Severity.ShouldBe(DocProc.Domain.Flags.FlagSeverity.Blocking);
        reference.Message!["de"].ShouldBe("Zu spät zurückgegeben");

        string Problems(string flags, string rules) => string.Join("\n", Publish(WithFlags(flags, rules)).Problems);
        Problems("""[{"code":"X_A"}]""", """[{"id":"r","kind":"compare","flag":"X_B","params":{"left":"returnDate","op":"le","value":"2099-12-31"}}]""")
            .ShouldContain("flag 'X_B' is not declared");
        Problems("""[{"code":"X_A"}]""", """[{"id":"r","kind":"compare","params":{"left":"returnDate","op":"le","value":"2099-12-31"}}]""")
            .ShouldContain("'X_A' is declared but no rule raises it");
        Problems("""[{"code":"X_A"},{"code":"X_A"}]""", """[{"id":"r","kind":"compare","flag":"X_A","params":{"left":"returnDate","op":"le","value":"2099-12-31"}}]""")
            .ShouldContain("declared twice");
        Problems("""[{"code":"RULE_FAILED"}]""", """[{"id":"r","kind":"duplicate"}]""").ShouldNotBeEmpty("the meta-schema refuses a code without the X_ prefix");
    }

    [Fact]
    public void Auto_approval_takes_effect_once_the_type_names_the_rules_that_vouch_for_it()
    {
        var vouched = ReturnSlipJson().Replace("\"review\":{\"autoApprove\":false}", "\"review\":{\"autoApprove\":true,\"requirePassedRules\":[\"r_qty\"],\"blockOnWarnings\":[\"r_dup\"],\"lines\":{\"requireSource\":[\"Profile\",\"Human\"],\"requireLocated\":true,\"maxLowConfidenceRows\":0}}", StringComparison.Ordinal);
        var report = Publish(vouched);
        report.CanPublish.ShouldBeTrue(string.Join("\n", report.Problems));
        report.Warnings.ShouldNotContain(w => w.Where == "processing.review.autoApprove");
        PublishChecks.AutoApproveHasNoEffect(report.Definition!).ShouldBeFalse();

        var unknown = Publish(vouched.Replace("[\"r_qty\"]", "[\"r_none\"]", StringComparison.Ordinal));
        string.Join("\n", unknown.Problems).ShouldContain("names the rule 'r_none'");
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

        // A new field that took a published field's id (another key and another kind) is refused, dependents or not.
        var reused = PublishChecks.Run(new PublishCheckInput { DefinitionJson = ReturnSlipJson(version: 2).Replace("\"key\":\"returnDate\",\"kind\":\"date\"", "\"key\":\"tour\",\"kind\":\"string\"", StringComparison.Ordinal), Published = published });
        reused.CanPublish.ShouldBeFalse();
        string.Join("\n", reused.Problems).ShouldContain("uses the id 'fld_rd' of the published field 'returnDate'");
    }

    [Fact]
    public void The_indexed_fields_of_a_version_are_its_own_dependents()
    {
        var published = DefinitionSerializer.Parse(ReturnSlipJson());

        PublishChecks.IndexedKeys(published).Select(d => d.Path).ShouldBe(["returnNumber"]);
    }

    [Theory]
    [InlineData("std.invoice")]
    [InlineData("std.credit-note")]
    public void An_invoice_family_fork_keeps_the_fields_of_the_invoice_record_as_it_holds_them(string template)
    {
        var fork = Fork(template, Field("fld_c_scr", "supplierCustomerRef", FieldKinds.Identifier));
        PublishReport Check(TypeDefinition candidate) => DefinitionSchemaCompiler.RunPublishChecks(new PublishCheckInput
        {
            DefinitionJson = DefinitionSerializer.Write(candidate),
            Published = StandardTemplates.Find(template),
            Dependents = [.. PublishChecks.IndexedKeys(StandardTemplates.Find(template)!)],
        });
        string Problems(PublishReport report) => string.Join("\n", report.Problems);

        // Relabelling, requiring, deprecating and adding fields is what a fork does (docs/11 §5.1).
        var allowed = ChangeField(ChangeField(fork, "notes", f => f with { Deprecated = true }), "lines[*].lotNumber", f => f with { Required = true, Label = new Dictionary<string, string> { ["en"] = "Batch" } });
        Check(allowed).CanPublish.ShouldBeTrue(Problems(Check(allowed)));

        var retyped = Check(ChangeField(ChangeField(fork, "deliveryDate", f => f with { Kind = FieldKinds.String }), "lines[*].unit", f => f with { Kind = FieldKinds.String, CodeList = null }));
        retyped.CanPublish.ShouldBeFalse();
        Problems(retyped).ShouldContain("deliveryDate: is held by the invoice record as date; it cannot become string");
        Problems(retyped).ShouldContain("lines[*].unit: is held by the invoice record as unit; it cannot become string");

        var removed = Check(fork with { Fields = [.. fork.Fields.Where(f => f.Key != "rounding")] });
        Problems(removed).ShouldContain("rounding: is a field of the invoice record");

        var renamed = Check(ChangeField(fork, "invoiceNumber", f => f with { Key = "documentNo" }) with { Roles = fork.Roles with { DocumentNumber = "documentNo", DuplicateKey = ["$supplierId", "documentNo"] } });
        Problems(renamed).ShouldContain("documentNo: is the invoice record's field 'invoiceNumber'");

        var listed = Check(ChangeField(fork, "originalInvoiceReference", f => f with { List = true }));
        Problems(listed).ShouldContain("cannot become a list of identifier");

        var bare = Check(ChangeField(fork, "lines[*].lineNumber", f => f with { Provenance = true, ReadOnly = false }));
        Problems(bare).ShouldContain("lines[*].lineNumber: is stored bare by the invoice record");

        var coded = Check(ChangeField(fork, "kind", f => f with { Options = [.. f.Options!, new EnumOption { Code = "Statement" }] }));
        Problems(coded).ShouldContain("kind: may only list codes the invoice record knows");
        Problems(coded).ShouldContain("not Statement");
        Check(ChangeField(fork, "kind", f => f with { Options = [.. f.Options!.Where(o => o.Code != "Proforma")] })).CanPublish.ShouldBeTrue("fewer codes are still codes the record knows");

        // A type outside the invoice family may restructure the same fields freely.
        var order = StandardTemplates.Find("std.purchase-order")!;
        var orderFork = ChangeField(order with { Key = "PurchaseOrder", LegacyKey = null, Version = 2, Base = new DefinitionBase { Template = order.Key, Version = order.Version, Mode = "copy" } }, "requestedDeliveryDate", f => f with { Kind = FieldKinds.String });
        DefinitionSchemaCompiler.RunPublishChecks(new PublishCheckInput { DefinitionJson = DefinitionSerializer.Write(orderFork) }).CanPublish.ShouldBeTrue();
    }

    [Fact]
    public void Auto_approval_on_a_type_outside_the_invoice_family_is_reported_as_having_no_effect()
    {
        var eager = ReturnSlipJson().Replace("\"review\":{\"autoApprove\":false}", "\"review\":{\"autoApprove\":true}", StringComparison.Ordinal);
        var report = Publish(eager);
        report.CanPublish.ShouldBeTrue(string.Join("\n", report.Problems));
        report.Warnings.ShouldContain(w => w.Where == "processing.review.autoApprove" && w.Message.Contains("always go to review", StringComparison.Ordinal));

        var order = StandardTemplates.Find("std.purchase-order")!;
        PublishChecks.AutoApproveHasNoEffect(order with { Processing = order.Processing with { Review = new ReviewPolicy { AutoApprove = true } } }).ShouldBeTrue();
        PublishChecks.AutoApproveHasNoEffect(Fork("std.invoice")).ShouldBeFalse();
        var invoice = Fork("std.invoice");
        PublishChecks.AutoApproveHasNoEffect(invoice with { Rules = [.. invoice.Rules.Where(r => r.Kind != "invoice.totals")] }).ShouldBeTrue();
        Publish(ReturnSlipJson()).Warnings.ShouldNotContain(w => w.Where == "processing.review.autoApprove");
    }

    /// <summary>
    /// An indexed row field publishes, with a warning: only the first rows' values are indexed (FieldIndexLimits), and
    /// docs/11 §5.8 advises against it. A header field that is indexed raises nothing.
    /// </summary>
    [Fact]
    public void An_indexed_row_field_is_published_with_a_warning_that_only_the_first_rows_are_indexed()
    {
        var indexedSku = ReturnSlipJson().Replace("""{"id":"fld_i_sku","key":"supplierSku","kind":"identifier"}""", """{"id":"fld_i_sku","key":"supplierSku","kind":"identifier","indexed":true}""", StringComparison.Ordinal);
        var report = Publish(indexedSku);
        report.CanPublish.ShouldBeTrue(string.Join("\n", report.Problems));
        var warning = report.Warnings.Single(w => w.Where == "items[*].supplierSku");
        warning.Message.ShouldContain($"only the values of its first {FieldIndexLimits.MaxValuesPerField} rows are indexed", Case.Sensitive);
        warning.Message.ShouldContain("§5.8", Case.Sensitive);

        Publish(ReturnSlipJson()).Warnings.ShouldNotContain(w => w.Message.Contains("is indexed but lies under an array", StringComparison.Ordinal), "returnNumber is a header field");
    }

    [Fact]
    public void A_renamed_field_names_its_former_keys_in_the_descriptors_of_the_new_version()
    {
        var v1 = DefinitionSerializer.Parse(ReturnSlipJson());
        var v2 = DefinitionSerializer.Parse(ReturnSlipJson(version: 2, returnDateKey: "returnedOn"));
        var v3 = DefinitionSerializer.Parse(ReturnSlipJson(version: 3, returnDateKey: "dateOfReturn")
            .Replace("\"key\":\"reason\"", "\"key\":\"returnReason\"", StringComparison.Ordinal));

        var compiled = DefinitionSchemaCompiler.Compile(v3).Compiled!;
        compiled.Descriptors.Fields.ShouldAllBe(f => f.FormerKeys == null, "the compiler alone knows no history");
        var descriptors = compiled.Descriptors.WithFormerKeys([v1, v2]);
        descriptors.Find("dateOfReturn")!.FormerKeys.ShouldBe(["returnDate", "returnedOn"]);
        descriptors.Find("items[*].returnReason")!.FormerKeys.ShouldBe(["reason"]);
        descriptors.Find("returnNumber")!.FormerKeys.ShouldBeNull();

        // Stored with the version and read back as such.
        DescriptorIndex.FromJson(descriptors.ToJson()).Find("dateOfReturn")!.FormerKeys.ShouldBe(["returnDate", "returnedOn"]);
        compiled.Descriptors.ToJson().ShouldNotContain("formerKeys");

        // A field moved into another group is not followed, and the publish report says its value stays behind.
        var moved = PublishChecks.Run(new PublishCheckInput
        {
            DefinitionJson = ReturnSlipJson(version: 2, withoutDeliveryNote: true).Replace("{\"id\":\"fld_i_desc\",\"key\":\"description\",\"kind\":\"text\",\"required\":true},", "{\"id\":\"fld_i_desc\",\"key\":\"description\",\"kind\":\"text\",\"required\":true},{\"id\":\"fld_dn\",\"key\":\"deliveryNoteReference\",\"kind\":\"identifier\"},", StringComparison.Ordinal),
            Published = v1,
        });
        moved.CanPublish.ShouldBeTrue(string.Join("\n", moved.Problems));
        moved.Changes.ShouldContain(c => c.Change == FieldChangeKind.Renamed && c.FieldId == "fld_dn" && c.NewPath == "items[*].deliveryNoteReference");
        moved.Warnings.ShouldContain(w => w.Where == "deliveryNoteReference" && w.Message.Contains("moves to items[*].deliveryNoteReference", StringComparison.Ordinal));
        moved.Compiled!.Descriptors.WithFormerKeys([v1]).Find("items[*].deliveryNoteReference")!.FormerKeys.ShouldBeNull();
        PublishChecks.Run(new PublishCheckInput { DefinitionJson = ReturnSlipJson(version: 2, returnDateKey: "returnedOn"), Published = v1 })
            .Warnings.ShouldNotContain(w => w.Message.Contains("moves to", StringComparison.Ordinal), "a rename in place keeps its value");
    }

    /// <summary>
    /// A version that gives a field's earlier key to another field (docs/11 §5.7): a tree stored under the earlier
    /// version holds the renamed field's value under that key, so completing it would put the value into the new field
    /// (a string in a date field) and leave the renamed one Missing. Refused while the earlier field still exists; a
    /// key of a removed field may be taken again, with a warning.
    /// </summary>
    [Fact]
    public void A_field_that_takes_the_former_key_of_another_field_is_refused()
    {
        var v1 = DefinitionSerializer.Parse(ReturnSlipJson());
        var renamedAndReused = ReturnSlipJson(version: 2)
            .Replace("{\"id\":\"fld_dn\",\"key\":\"deliveryNoteReference\",\"kind\":\"identifier\"},", "{\"id\":\"fld_dn\",\"key\":\"ourRef\",\"kind\":\"identifier\"},{\"id\":\"fld_new\",\"key\":\"deliveryNoteReference\",\"kind\":\"date\"},", StringComparison.Ordinal);

        var refused = PublishChecks.Run(new PublishCheckInput { DefinitionJson = renamedAndReused, Published = v1 });
        refused.CanPublish.ShouldBeFalse();
        refused.Problems.ShouldContain(p => p.Where == "deliveryNoteReference" && p.Message.Contains("'fld_dn' (now 'ourRef') had in version 1", StringComparison.Ordinal));

        // Also when the key was the field's in a version before the published one (v1 deprecated, v2 published).
        var v2 = DefinitionSerializer.Parse(ReturnSlipJson(version: 2).Replace("\"key\":\"deliveryNoteReference\"", "\"key\":\"ourRef\"", StringComparison.Ordinal));
        var v3 = renamedAndReused.Replace("\"version\":2", "\"version\":3", StringComparison.Ordinal);
        PublishChecks.Run(new PublishCheckInput { DefinitionJson = v3, Published = v2 }).CanPublish.ShouldBeTrue("without the earlier versions the check cannot know");
        var withHistory = PublishChecks.Run(new PublishCheckInput { DefinitionJson = v3, Published = v2, EarlierKeys = DescriptorIndex.KeySlotsOf(v1) });
        withHistory.CanPublish.ShouldBeFalse();
        withHistory.Problems.ShouldContain(p => p.Where == "deliveryNoteReference" && p.Message.Contains("version 1", StringComparison.Ordinal));

        // The key of a removed field may be taken by a new one; the report says what old documents show.
        var reusedRemoved = ReturnSlipJson(version: 2, withoutDeliveryNote: true)
            .Replace("{\"id\":\"fld_sup\",\"key\":\"supplier\",\"kind\":\"party\"},", "{\"id\":\"fld_sup\",\"key\":\"supplier\",\"kind\":\"party\"},{\"id\":\"fld_new\",\"key\":\"deliveryNoteReference\",\"kind\":\"date\"},", StringComparison.Ordinal);
        var warned = PublishChecks.Run(new PublishCheckInput { DefinitionJson = reusedRemoved, Published = v1 });
        warned.CanPublish.ShouldBeTrue(string.Join("\n", warned.Problems));
        warned.Warnings.ShouldContain(w => w.Where == "deliveryNoteReference" && w.Message.Contains("'fld_dn' that version 1 had and this version removes", StringComparison.Ordinal));

        // A rename alone, and a rename back to the earlier key, are fine.
        PublishChecks.Run(new PublishCheckInput { DefinitionJson = ReturnSlipJson(version: 3), Published = v2, EarlierKeys = DescriptorIndex.KeySlotsOf(v1) }).CanPublish.ShouldBeTrue();
        PublishChecks.Run(new PublishCheckInput { DefinitionJson = ReturnSlipJson(version: 2, returnDateKey: "returnedOn"), Published = v1 }).Problems.ShouldBeEmpty();
    }

    [Fact]
    public void The_former_keys_from_key_slots_are_those_from_the_definitions()
    {
        var v1 = DefinitionSerializer.Parse(ReturnSlipJson());
        var v2 = DefinitionSerializer.Parse(ReturnSlipJson(version: 2, returnDateKey: "returnedOn"));
        var v3 = DefinitionSchemaCompiler.Compile(DefinitionSerializer.Parse(ReturnSlipJson(version: 3, returnDateKey: "dateOfReturn"))).Compiled!.Descriptors;

        DescriptorIndex.KeySlotsOf(v1).ShouldContain(new FieldKeySlot("fld_i_reason", "fld_items", "reason", 1));
        DescriptorIndex.KeySlotsOf(v1).ShouldContain(new FieldKeySlot("fld_rd", null, "returnDate", 1));
        v3.WithFormerKeys([.. DescriptorIndex.KeySlotsOf(v1), .. DescriptorIndex.KeySlotsOf(v2)]).ToJson().ShouldBe(v3.WithFormerKeys([v1, v2]).ToJson());
    }

    /// <summary>
    /// The field index stores keys under the database's collation, case-insensitive on SQL Server by default: refNo and
    /// refno would share index rows (a primary key violation on every save). Sibling keys are unique ignoring case.
    /// </summary>
    [Fact]
    public void Sibling_field_keys_that_differ_only_by_case_are_refused()
    {
        var top = ReturnSlipJson().Replace("{\"id\":\"fld_sup\",\"key\":\"supplier\",\"kind\":\"party\"},", "{\"id\":\"fld_sup\",\"key\":\"supplier\",\"kind\":\"party\"},{\"id\":\"fld_a\",\"key\":\"refNo\",\"kind\":\"identifier\",\"indexed\":true},{\"id\":\"fld_b\",\"key\":\"refno\",\"kind\":\"identifier\",\"indexed\":true},", StringComparison.Ordinal);
        var report = Publish(top);
        report.CanPublish.ShouldBeFalse();
        report.Problems.ShouldContain(p => p.Message.Contains("key 'refno' differs from the key 'refNo' at this level only by case", StringComparison.Ordinal));

        var row = ReturnSlipJson().Replace("{\"id\":\"fld_i_qty\",\"key\":\"quantity\",\"kind\":\"decimal\",\"min\":0},", "{\"id\":\"fld_i_qty\",\"key\":\"quantity\",\"kind\":\"decimal\",\"min\":0},{\"id\":\"fld_i_q2\",\"key\":\"quantitY\",\"kind\":\"decimal\"},", StringComparison.Ordinal);
        Publish(row).Problems.ShouldContain(p => p.Message.Contains("only by case", StringComparison.Ordinal));

        // The same key in different groups is fine, and so is an exact duplicate reported as before.
        Publish(ReturnSlipJson()).CanPublish.ShouldBeTrue();
        var twice = ReturnSlipJson().Replace("{\"id\":\"fld_sup\",\"key\":\"supplier\",\"kind\":\"party\"},", "{\"id\":\"fld_sup\",\"key\":\"supplier\",\"kind\":\"party\"},{\"id\":\"fld_c\",\"key\":\"supplier\",\"kind\":\"party\"},", StringComparison.Ordinal);
        Publish(twice).Problems.ShouldContain(p => p.Message.Contains("key 'supplier' is declared twice at this level", StringComparison.Ordinal));
    }

    /// <summary>
    /// A <c>pattern</c> rule's <c>params.pattern</c> is compiled at publish the way the rule runs it (NonBacktracking):
    /// a construct that engine cannot run passed the parameter schema and then threw on every validation of the type.
    /// </summary>
    [Theory]
    [InlineData(@"(a)\\1")]
    [InlineData("(?=P)PL")]
    [InlineData("(?<=P)L")]
    [InlineData("(?>PL)")]
    [InlineData("(")]
    public void A_pattern_rule_whose_pattern_the_engine_cannot_run_is_refused(string pattern)
    {
        var report = Publish(ReturnSlipJson(rules: $$$"""[{"id":"r_probe","kind":"pattern","params":{"path":"returnNumber","pattern":"{{{pattern}}}"} }]"""));

        report.CanPublish.ShouldBeFalse();
        report.Problems.ShouldContain(p => p.Where == "rules[0] (r_probe).params" && p.Message.Contains("pattern is not a valid regular expression", StringComparison.Ordinal));
        Publish(ReturnSlipJson(rules: """[{"id":"r_probe","kind":"pattern","params":{"path":"returnNumber","pattern":"^RMA-[0-9]+$"}}]""")).CanPublish.ShouldBeTrue();
    }

    /// <summary>
    /// A pattern whose counted repetitions multiply (twenty characters, ten thousand automaton nodes under
    /// NonBacktracking) is refused at publish, as a field's pattern, an extraction hint and a pattern rule's parameter:
    /// the table tool and validation run it in the shared API and workers.
    /// </summary>
    [Fact]
    public void A_pattern_that_unrolls_too_far_is_refused_wherever_a_definition_names_one()
    {
        const string Huge = @"(?:[^x]{0,99}\\d){0,99}x";
        var field = ReturnSlipJson().Replace("{\"id\":\"fld_rn\",\"key\":\"returnNumber\",\"kind\":\"identifier\",\"required\":true,\"indexed\":true,", $"{{\"id\":\"fld_rn\",\"key\":\"returnNumber\",\"kind\":\"identifier\",\"required\":true,\"indexed\":true,\"pattern\":\"{Huge}\",", StringComparison.Ordinal);
        var report = Publish(field);
        report.CanPublish.ShouldBeFalse();
        report.Problems.ShouldContain(p => p.Message.StartsWith("pattern repeats too much", StringComparison.Ordinal));

        var rule = Publish(ReturnSlipJson(rules: $$$"""[{"id":"r_probe","kind":"pattern","params":{"path":"returnNumber","pattern":"{{{Huge}}}"} }]"""));
        rule.CanPublish.ShouldBeFalse();
        rule.Problems.ShouldContain(p => p.Where == "rules[0] (r_probe).params" && p.Message.StartsWith("pattern repeats too much", StringComparison.Ordinal));

        // Few atoms, but a repeated group whose length varies: seconds per match whatever the timeout.
        var varying = Publish(ReturnSlipJson(rules: """[{"id":"r_probe","kind":"pattern","params":{"path":"returnNumber","pattern":"(.{0,15}a){30}x"}}]"""));
        varying.CanPublish.ShouldBeFalse();
        varying.Problems.ShouldContain(p => p.Message.StartsWith("pattern repeats too much", StringComparison.Ordinal) && p.Message.Contains("combination of lengths", StringComparison.Ordinal));

        Publish(ReturnSlipJson(rules: """[{"id":"r_probe","kind":"pattern","params":{"path":"returnNumber","pattern":"^RMA-[0-9]{4,12}$"}}]""")).CanPublish.ShouldBeTrue();
    }

    // ------------------------------------------------------------------ price-list templates

    [Fact]
    public void The_price_list_templates_v1_are_always_reviewed_and_use_only_P1_rule_kinds()
    {
        // Version 1 (P1 to P4) as the catalogue migration froze it; the live templates are v2 (below).
        var frozen = Infrastructure.Catalogue.TypeCatalogueSeedSql.FrozenTemplates().ToDictionary(f => f.Template.Key, f => f, StringComparer.Ordinal);
        foreach (var key in new[] { "std.price-list", "std.daily-specials" })
        {
            var template = frozen[key].Template;

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
            PublishChecks.Run(new PublishCheckInput { DefinitionJson = frozen[key].Json }).Problems.ShouldBeEmpty(key);
        }

        frozen["std.price-list"].Template.Rules.Select(r => r.Id).ShouldBe(["r_validity", "r_from", "r_price_pos", "r_pack", "r_unit", "r_dup"]);
        frozen["std.daily-specials"].Template.Rules.Select(r => r.Id).ShouldBe(["r_validity", "r_not_expired", "r_price_pos", "r_pack", "r_unit", "r_dup"]);
    }

    [Fact]
    public void The_price_list_templates_v2_name_their_substitutes_for_a_totals_check_and_publish()
    {
        // docs/11 §5.8: v2 adds the line-table rules and the full review block, and changes no field.
        var frozen = Infrastructure.Catalogue.TypeCatalogueSeedSql.FrozenTemplates().ToDictionary(f => f.Template.Key, f => f.Template, StringComparer.Ordinal);
        foreach (var key in new[] { "std.price-list", "std.daily-specials" })
        {
            var template = StandardTemplates.Find(key)!;
            template.Version.ShouldBe(2);
            DefinitionSerializer.Write(template with { Version = 1, Base = frozen[key].Base, Rules = frozen[key].Rules, Processing = frozen[key].Processing })
                .ShouldBe(DefinitionSerializer.Write(frozen[key]), $"{key} v2 changes no field, role, classification or ui");
            template.Processing.Review.AutoApprove.ShouldBeTrue(key);
            template.Rules.Select(r => r.Kind).ShouldContain("tableComplete");
            template.Rules.Select(r => r.Kind).ShouldContain("unique");
            template.Rules.Select(r => r.Kind).ShouldContain("ordered");
            PublishChecks.Run(new PublishCheckInput { DefinitionJson = StandardTemplates.ReadJson(key) }).Problems.ShouldBeEmpty(key);
            template.Processing.Review.RequirePassedRules!.ShouldAllBe(id => template.Rules.Any(r => r.Id == id), $"{key} names the rules that stand in for a totals check");
        }

        var list = StandardTemplates.Find("std.price-list")!;
        list.Rules.Select(r => r.Id).ShouldBe(["r_validity", "r_from", "r_table", "r_count", "r_sku_unique", "r_price_pos", "r_pack", "r_unit", "r_tiers", "r_delta", "r_dup"]);
        list.Processing.Review.RequirePassedRules.ShouldBe(["r_table", "r_sku_unique", "r_price_pos"]);
        list.Processing.Review.BlockOnWarnings.ShouldBe(["r_delta", "r_dup"]);
        var specials = StandardTemplates.Find("std.daily-specials")!;
        specials.Rules.Select(r => r.Id).ShouldBe(["r_validity", "r_single_day", "r_not_expired", "r_table", "r_count", "r_sku_unique", "r_price_pos", "r_pack", "r_unit", "r_tiers", "r_below_prev", "r_dup"]);
        specials.Processing.Review.Priority.ShouldBe("urgent");
        specials.Processing.Review.DueBy.ShouldBe("validUntil");
        specials.Processing.Review.RequirePassedRules.ShouldBe(["r_table", "r_not_expired", "r_price_pos", "r_sku_unique"]);
        specials.Processing.Review.BlockOnWarnings.ShouldBe(["r_dup"]);
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

    /// <summary>The definition with the field at <paramref name="path"/> (a top-level key or <c>lines[*].key</c>) changed.</summary>
    private static TypeDefinition ChangeField(TypeDefinition definition, string path, Func<FieldDefinition, FieldDefinition> change)
    {
        var parts = path.Split("[*].");
        if (parts.Length == 1)
        {
            return definition with { Fields = [.. definition.Fields.Select(f => f.Key == path ? change(f) : f)] };
        }

        return definition with
        {
            Fields = [.. definition.Fields.Select(f => f.Key == parts[0] ? f with { Item = new FieldGroup { Fields = [.. f.Item!.Fields.Select(c => c.Key == parts[1] ? change(c) : c)] } } : f)],
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
