using System.Diagnostics;
using System.Text.Json.Nodes;
using DocProc.Application.Outputs;
using DocProc.Infrastructure.Outputs;
using Microsoft.Extensions.Options;

namespace DocProc.Integration.Tests.Outputs;

/// <summary>One renderer pool for the tests of a class (starting a renderer process costs a few hundred milliseconds).</summary>
public sealed class RendererFixture : IDisposable
{
    public JsonataRenderer Renderer { get; } = new(Options.Create(new JsonataRendererOptions { PoolSize = 2 }));

    public void Dispose() => Renderer.Dispose();
}

/// <summary>
/// The output renderer of docs/11 §13.1: Jsonata.Net.Native in the renderer child process, the custom functions, the
/// limits (time, output size, depth, memory), the target-schema check and the scan of the paths an expression reads.
/// </summary>
public sealed class JsonataRendererTests(RendererFixture fixture) : IClassFixture<RendererFixture>
{
    private static readonly DateOnly Today = new(2026, 11, 3);

    private JsonataRenderer Renderer => fixture.Renderer;

    private async Task<RenderResult> RenderAsync(string expression, string input = "{}", IReadOnlyList<LookupTableSnapshot>? lookups = null, JsonObject? fields = null, JsonObject? schema = null) =>
        await Renderer.RenderAsync(
            new RenderRequest(expression, JsonNode.Parse(input), Today) { Lookups = lookups ?? [], Fields = fields, TargetSchema = schema },
            TestContext.Current.CancellationToken);

    private async Task<JsonNode?> OutputAsync(string expression, string input = "{}", IReadOnlyList<LookupTableSnapshot>? lookups = null, JsonObject? fields = null)
    {
        var result = await RenderAsync(expression, input, lookups, fields);
        result.Error.ShouldBeNull();
        return result.Output;
    }

    [Fact]
    public async Task Renders_an_expression_over_the_input()
    {
        var result = await RenderAsync("""{ "no": roles.documentNumber, "gross": -1 * roles.total.amount }""", """{"roles":{"documentNumber":"GS-1","total":{"amount":119.00}}}""");

        result.Succeeded.ShouldBeTrue();
        result.Output!["no"]!.GetValue<string>().ShouldBe("GS-1");
        result.Output["gross"]!.GetValue<decimal>().ShouldBe(-119m);
        result.OutputBytes.ShouldBe(result.OutputText!.Length);
        result.ReferencedPaths.ShouldBe(["roles", "roles.documentNumber", "roles.total", "roles.total.amount"]);
    }

    [Fact]
    public async Task Brackets_around_a_path_give_an_array_even_for_one_row_and_numbers_keep_their_text()
    {
        var output = await OutputAsync("""{ "rows": [ doc.lines.{ "net": lineNet.amount } ] }""", """{"doc":{"lines":{"lineNet":{"amount":100.00}}}}""");

        output!["rows"]!.AsArray().Count.ShouldBe(1);
        output.ToJsonString().ShouldBe("""{"rows":[{"net":100.00}]}""");
    }

    [Fact]
    public async Task A_block_comment_is_ignored_so_the_visual_field_map_can_travel_in_the_expression()
    {
        // The Integrations UI stores the visual field map as a leading comment of the expression it compiles to (docs/11 §13.1).
        var result = await RenderAsync("/* docproc:fieldmap {\"v\":1,\"rows\":[{\"k\":\"a*\\/b\"}]} */\n(\n  $root := $;\n  { /* inner */ \"no\": roles.documentNumber }\n)", """{"roles":{"documentNumber":"GS-1"}}""");

        result.Error.ShouldBeNull();
        result.Output!.ToJsonString().ShouldBe("""{"no":"GS-1"}""");
        result.ReferencedPaths.ShouldBe(["roles", "roles.documentNumber"]);
    }

    [Fact]
    public async Task An_expression_that_yields_nothing_is_undefined()
    {
        var result = await RenderAsync("missing.path");

        result.Error.ShouldBeNull();
        result.Undefined.ShouldBeTrue();
        result.Output.ShouldBeNull();
    }

    [Theory]
    [InlineData("""$fmtDate("2026-10-28", "dd.MM.yyyy")""", "28.10.2026")]
    [InlineData("""$fmtDate("2027-01-01", "yyyyMMdd")""", "20270101")]
    [InlineData("""$fmtDate("2026-10-29T09:12:00Z", "yyyy-MM-dd HH:mm")""", "2026-10-29 09:12")]
    [InlineData("""$fmtNum(1234.565, 2, ",", ".")""", "1.234,57")]
    [InlineData("""$fmtNum(-0.5, 0)""", "-1")]
    [InlineData("""$fmtNum(1234567.1, 3)""", "1234567.100")]
    [InlineData("""$fmtNum(50, 2, ",")""", "50,00")]
    [InlineData("""$today()""", "2026-11-03")]
    public async Task The_formatting_functions_and_today(string expression, string expected) =>
        (await OutputAsync(expression))!.GetValue<string>().ShouldBe(expected);

    [Fact]
    public async Task Formatting_nothing_gives_nothing()
    {
        (await RenderAsync("""$fmtDate(roles.missing, "dd.MM.yyyy")""")).Undefined.ShouldBeTrue();
        (await RenderAsync("""$fmtNum(roles.missing, 2)""")).Undefined.ShouldBeTrue();
    }

    [Theory]
    [InlineData("""$fmtDate("28.10.2026", "yyyyMMdd")""", "not a date")]
    [InlineData("""$fmtNum(1, 11)""", "decimals")]
    [InlineData("""$fmtNum("x", 2)""", "number")]
    public async Task A_custom_function_used_wrongly_is_a_function_error(string expression, string message)
    {
        var result = await RenderAsync(expression);

        result.Error.ShouldNotBeNull();
        result.Error.Kind.ShouldBe(RenderErrorKinds.Function);
        result.Error.Message.ShouldContain(message);
        result.Error.IsPermanent.ShouldBeTrue();
    }

    [Fact]
    public async Task Table_maps_by_the_entry_valid_today_ignoring_case_unless_the_table_is_case_sensitive()
    {
        LookupTableSnapshot[] lookups =
        [
            new("erp-uom", CaseSensitive: false, [
                new("KAR", "CT"),
                new("Krt", "CT"),
                new("KG", "KGM-OLD", ValidTo: new DateOnly(2026, 10, 31)),
                new("KG", "KGM", ValidFrom: new DateOnly(2026, 11, 1)),
                new("ST", "H87-NEXT", ValidFrom: new DateOnly(2026, 12, 1)),
            ]),
            new("codes", CaseSensitive: true, [new("abc", "lower")]),
        ];
        var output = await OutputAsync(
            """
            {
              "kar": $table("erp-uom", "kar"),
              "kg": $table("erp-uom", "KG"),
              "notYet": $table("erp-uom", "ST", "H87"),
              "none": $table("erp-uom", "XX", "ST"),
              "noDefault": $table("erp-uom", "XX"),
              "exact": $table("codes", "abc", "-"),
              "wrongCase": $table("codes", "ABC", "-"),
              "number": $table("codes", 5, "n")
            }
            """,
            lookups: lookups);

        output!["kar"]!.GetValue<string>().ShouldBe("CT");
        output["kg"]!.GetValue<string>().ShouldBe("KGM", "the entry valid from 1 November replaces the old one");
        output["notYet"]!.GetValue<string>().ShouldBe("H87", "an entry valid from December is not valid today");
        output["none"]!.GetValue<string>().ShouldBe("ST");
        output.AsObject().ContainsKey("noDefault").ShouldBeFalse("no entry and no default is nothing");
        output["exact"]!.GetValue<string>().ShouldBe("lower");
        output["wrongCase"]!.GetValue<string>().ShouldBe("-");
        output["number"]!.GetValue<string>().ShouldBe("n");
    }

    [Fact]
    public async Task Table_of_a_table_that_was_not_sent_names_it()
    {
        var result = await RenderAsync("""$table("erp-credit-reason", "Return")""");

        result.Error!.Kind.ShouldBe(RenderErrorKinds.Function);
        result.Error.Message.ShouldContain("'erp-credit-reason'");
        result.Tables.ShouldBe(["erp-credit-reason"]);
    }

    [Fact]
    public async Task Field_answers_the_envelope_of_a_path()
    {
        var fields = new JsonObject
        {
            ["invoiceNumber"] = new JsonObject { ["status"] = "Extracted", ["confidence"] = 0.97, ["method"] = "Llm", ["normalized"] = "GS20260042" },
            ["lines[0].quantity"] = new JsonObject { ["status"] = "Corrected", ["confidence"] = 1, ["method"] = "Human", ["normalized"] = 2 },
        };
        var output = await OutputAsync(
            """{ "s": $field("invoiceNumber").status, "c": $field("invoiceNumber").confidence, "m": $field("lines[0].quantity").method, "none": $field("dueDate") }""",
            fields: fields);

        output!["s"]!.GetValue<string>().ShouldBe("Extracted");
        output["c"]!.GetValue<double>().ShouldBe(0.97);
        output["m"]!.GetValue<string>().ShouldBe("Human");
        output.AsObject().ContainsKey("none").ShouldBeFalse();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"supplier":{"attributes":{"erpVendorId":null}}}""")]
    [InlineData("""{"supplier":{"attributes":{"erpVendorId":"  "}}}""")]
    [InlineData("""{"supplier":{"attributes":{"erpVendorId":[]}}}""")]
    public async Task Required_without_a_value_stops_the_render_with_its_message(string input)
    {
        var result = await RenderAsync("""{ "vendor": $required(supplier.attributes.erpVendorId, "Supplier has no ERP vendor id") }""", input);

        result.Error.ShouldNotBeNull();
        result.Error.Kind.ShouldBe(RenderErrorKinds.Required);
        result.Error.Message.ShouldBe("Supplier has no ERP vendor id");
        result.Error.IsPermanent.ShouldBeTrue();
        result.Output.ShouldBeNull();
    }

    [Fact]
    public async Task Required_with_a_value_returns_it()
    {
        var output = await OutputAsync("""{ "vendor": $required(supplier.attributes.erpVendorId, "x"), "zero": $required(0, "x"), "no": $required(false, "x") }""", """{"supplier":{"attributes":{"erpVendorId":"V-10042"}}}""");

        output!.ToJsonString().ShouldBe("""{"vendor":"V-10042","zero":0,"no":false}""");
    }

    [Fact]
    public async Task A_syntax_error_has_its_position()
    {
        var result = await RenderAsync("""{ "a": roles.documentNumber, """);

        result.Error!.Kind.ShouldBe(RenderErrorKinds.Syntax);
        result.Error.Code.ShouldNotBeNull();
        result.Error.Position.ShouldNotBeNull();
    }

    [Fact]
    public async Task There_are_no_io_functions()
    {
        foreach (var call in new[] { """$readFile("/etc/passwd")""", """$fetch("https://example.com")""", """$httpGet("http://169.254.169.254")""", """$env("PATH")""" })
        {
            var result = await RenderAsync(call);
            result.Error.ShouldNotBeNull(call);
            result.Error.Kind.ShouldBe(RenderErrorKinds.Runtime, call);
        }
    }

    [Fact]
    public async Task A_render_past_the_time_limit_is_stopped_and_the_next_render_works()
    {
        var watch = Stopwatch.StartNew();
        var result = await RenderAsync("( $f := function($n){ $n = 0 ? 1 : $f($n-1) + $f($n-1) }; $f(40) )");
        watch.Stop();

        result.Error!.Kind.ShouldBe(RenderErrorKinds.TimeLimit);
        result.Error.IsPermanent.ShouldBeTrue();
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15), "the process is killed at 2 s of processor time, at the latest at the 10 s wall-clock ceiling");
        watch.Elapsed.ShouldBeGreaterThanOrEqualTo(RenderLimits.Time - TimeSpan.FromMilliseconds(50));

        (await OutputAsync("$sum([1,2,3])"))!.GetValue<int>().ShouldBe(6);
    }

    [Fact]
    public async Task The_wall_clock_ceiling_stops_a_render_however_much_processor_time_is_left()
    {
        using var renderer = new JsonataRenderer(Options.Create(new JsonataRendererOptions { PoolSize = 1, TimeLimit = TimeSpan.FromSeconds(60), WallClockLimit = TimeSpan.FromSeconds(1) }));
        var watch = Stopwatch.StartNew();

        var result = await renderer.RenderAsync(new RenderRequest("( $f := function($n){ $n = 0 ? 1 : $f($n-1) + $f($n-1) }; $f(40) )", null, Today), TestContext.Current.CancellationToken);

        result.Error!.Kind.ShouldBe(RenderErrorKinds.TimeLimit);
        result.Error.Message.ShouldContain("1 s");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task An_output_over_five_megabytes_is_refused()
    {
        var result = await RenderAsync("[1..1000000]");

        result.Error!.Kind.ShouldBe(RenderErrorKinds.SizeLimit);
        result.Error.Message.ShouldContain(RenderLimits.MaxOutputBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Recursion_past_the_depth_cap_is_refused()
    {
        var deep = await RenderAsync("( $f := function($n){ $n <= 0 ? 0 : 1 + $f($n-1) }; $f(100000) )");
        deep.Error!.Kind.ShouldBe(RenderErrorKinds.DepthLimit);

        var tail = await RenderAsync("( $f := function($n, $acc){ $n <= 0 ? $acc : $f($n-1, $acc+1) }; $f(100000000, 0) )");
        tail.Error!.Kind.ShouldBe(RenderErrorKinds.DepthLimit, "the tail-recursion cap");
    }

    [Fact]
    public async Task A_render_that_allocates_past_the_heap_limit_is_stopped()
    {
        using var small = new JsonataRenderer(Options.Create(new JsonataRendererOptions { PoolSize = 1, HeapHardLimitBytes = 256L * 1024 * 1024, TimeLimit = TimeSpan.FromSeconds(20), WallClockLimit = TimeSpan.FromSeconds(60) }));

        var result = await small.RenderAsync(new RenderRequest("$count([1..1000000].([1..1000000]))", new JsonObject(), Today), TestContext.Current.CancellationToken);

        result.Error.ShouldNotBeNull();
        result.Error.Kind.ShouldBeOneOf(RenderErrorKinds.MemoryLimit, RenderErrorKinds.Unavailable);
        (await small.RenderAsync(new RenderRequest("1 + 1", null, Today), TestContext.Current.CancellationToken)).Output!.GetValue<int>().ShouldBe(2, "a new process replaces the one that ran out");
    }

    [Fact]
    public async Task The_output_is_checked_against_the_target_schema_with_the_path_of_each_error()
    {
        var schema = JsonNode.Parse("""
            {
              "type": "object",
              "required": ["header", "prices"],
              "properties": {
                "header": { "type": "object", "required": ["vendorNo"], "properties": { "vendorNo": { "type": "string", "minLength": 1 } } },
                "prices": { "type": "array", "items": { "type": "object", "required": ["price"], "properties": { "price": { "type": "number", "minimum": 0 } } } }
              }
            }
            """)!.AsObject();
        const string Expression = """{ "header": { "vendorNo": supplier.no }, "prices": [ rows.{ "price": p } ] }""";

        var fits = await RenderAsync(Expression, """{"supplier":{"no":"V-1"},"rows":[{"p":1},{"p":2}]}""", schema: schema);
        fits.Succeeded.ShouldBeTrue();
        fits.SchemaErrors.ShouldBeEmpty();

        var bad = await RenderAsync(Expression, """{"supplier":{"no":""},"rows":[{"p":1},{"p":-2},{"q":3}]}""", schema: schema);
        bad.Error.ShouldBeNull("the render itself succeeded");
        bad.Succeeded.ShouldBeFalse();
        bad.Output.ShouldNotBeNull();
        bad.SchemaErrors.ShouldContain(e => e.Path == "header.vendorNo" && e.Keyword == "minLength");
        bad.SchemaErrors.ShouldContain(e => e.Path == "prices[1].price" && e.Keyword == "minimum" && e.Location == "/prices/1/price");
        bad.SchemaErrors.ShouldContain(e => e.Path == "prices[2]" && e.Keyword == "required");
    }

    [Fact]
    public async Task A_target_schema_that_is_not_one_is_a_schema_error_and_a_remote_reference_is_not_fetched()
    {
        var invalid = await RenderAsync("1", schema: JsonNode.Parse("""{"type": 5}""")!.AsObject());
        invalid.Error!.Kind.ShouldBe(RenderErrorKinds.Schema);

        var remote = await RenderAsync("""{"a": 1}""", schema: JsonNode.Parse("""{"$ref": "https://example.com/schema.json"}""")!.AsObject());
        remote.Succeeded.ShouldBeFalse();
        (remote.Error?.Kind == RenderErrorKinds.Schema || remote.SchemaErrors.Count > 0).ShouldBeTrue("an unresolvable reference fails instead of being fetched");
    }

    [Fact]
    public async Task Check_parses_without_running_and_lists_the_paths_and_tables()
    {
        var check = await Renderer.CheckAsync(
            """( $root := $; { "rows": [ doc.lines[$not(status = "X")].{ "s": supplierSku, "v": $root.roles.validFrom, "u": $table("erp-uom", unit, "ST") } ] } )""",
            null,
            TestContext.Current.CancellationToken);

        check.Error.ShouldBeNull();
        check.ReferencedPaths.ShouldBe(["doc", "doc.lines", "doc.lines.status", "doc.lines.supplierSku", "doc.lines.unit", "roles", "roles.validFrom"]);
        check.Tables.ShouldBe(["erp-uom"]);

        (await Renderer.CheckAsync("{ ", null, TestContext.Current.CancellationToken)).Error!.Kind.ShouldBe(RenderErrorKinds.Syntax);
        (await Renderer.CheckAsync("1", JsonNode.Parse("""{"type": 5}""")!.AsObject(), TestContext.Current.CancellationToken)).Error!.Kind.ShouldBe(RenderErrorKinds.Schema);
        (await Renderer.CheckAsync(" ", null, TestContext.Current.CancellationToken)).Error!.Kind.ShouldBe(RenderErrorKinds.Request);
        (await Renderer.CheckAsync(new string('1', RenderLimits.MaxExpressionLength + 1), null, TestContext.Current.CancellationToken)).Error!.Kind.ShouldBe(RenderErrorKinds.Request);
    }

    [Fact]
    public async Task Renders_run_in_parallel_on_the_pool()
    {
        var renders = Enumerable.Range(0, 8).Select(i => RenderAsync($"$sum([1..{i + 1}])")).ToList();
        var results = await Task.WhenAll(renders);

        results.Select(r => r.Output!.GetValue<int>()).ShouldBe([1, 3, 6, 10, 15, 21, 28, 36]);
    }

    [Fact]
    public async Task A_renderer_that_cannot_be_started_is_unavailable_which_is_retryable()
    {
        using var broken = new JsonataRenderer(Options.Create(new JsonataRendererOptions { RendererPath = "/nonexistent/DocProc.Outputs.Renderer.dll" }));

        var result = await broken.RenderAsync(new RenderRequest("1", null, Today), TestContext.Current.CancellationToken);

        result.Error!.Kind.ShouldBe(RenderErrorKinds.Unavailable);
        result.Error.IsPermanent.ShouldBeFalse();
    }
}
