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
    [InlineData("""$fmtNum(1234, 0, ".", ".")""", "1.234")]
    [InlineData("""$fmtNum(1234, 0, "", "'")""", "1'234")]
    [InlineData("""$fmtNum(1234.5, 2, ",", " ")""", "1 234,50")]
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
    [InlineData("""$fmtDate("2026-11-03", "d")""", "3")]
    [InlineData("""$fmtDate("2026-11-03", "M")""", "11")]
    [InlineData("""$fmtDate("2026-11-03", "yy")""", "26")]
    [InlineData("""$fmtDate("2026-10-29T09:12:00.1234567+01:00", "yyyy-MM-dd HH:mm zzz")""", "2026-10-29 09:12 +01:00")]
    [InlineData("""$fmtDate("2026-10-29T09:12", "HH:mm")""", "09:12")]
    [InlineData("""$fmtDate("2026-10-29T09:12:30Z", "ss")""", "30")]
    public async Task A_one_character_pattern_is_a_custom_pattern_and_ISO_date_times_are_read(string expression, string expected) =>
        (await OutputAsync(expression))!.GetValue<string>().ShouldBe(expected);

    [Theory]
    [InlineData("""$fmtDate("11/03/2026", "yyyy-MM-dd")""", "not a date")]
    [InlineData("""$fmtDate("2026-11-03 09:12", "yyyy-MM-dd")""", "not a date")]
    [InlineData("""$fmtDate("Nov 3, 2026", "yyyy-MM-dd")""", "not a date")]
    [InlineData("""$fmtDate("28.10.2026", "yyyyMMdd")""", "not a date")]
    [InlineData("""$fmtNum(1, 11)""", "decimals")]
    [InlineData("""$fmtNum("x", 2)""", "number")]
    [InlineData("""$fmtNum(1234.5, 2, "", "")""", "decimal separator is not empty")]
    [InlineData("""$fmtNum(1234.5, 2, "", ".")""", "decimal separator is not empty")]
    [InlineData("""$fmtNum(1234.5, 2, ".", ".")""", "differ")]
    [InlineData("""$fmtNum(1234.5, 2, ",", ",")""", "differ")]
    [InlineData("""$fmtNum(1234.5, 2, "0")""", "no digit")]
    [InlineData("""$fmtNum(1234.5, 2, ",", "1")""", "no digit")]
    [InlineData("""$fmtNum(1234.5, 2, "-")""", "no digit")]
    [InlineData("""$fmtNum(1234, 0, ".", "-")""", "no digit")]
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
    public async Task Table_matches_a_number_by_its_value_whatever_its_text()
    {
        LookupTableSnapshot[] lookups = [new("vat", CaseSensitive: false, [new("10", "ten"), new("19.00", "nineteen"), new("7", "seven")])];
        const string Expression = """{ "a": $table("vat", a, "def"), "b": $table("vat", b, "def"), "c": $table("vat", c, "def"), "lit": $table("vat", 10.0, "def"), "text": $table("vat", "10.0", "def") }""";

        var output = await OutputAsync(Expression, """{"a": 10.0, "b": 19, "c": 7.10}""", lookups);

        output!["a"]!.GetValue<string>().ShouldBe("ten", "10.0 from the values view keeps its text, and is the number 10");
        output["b"]!.GetValue<string>().ShouldBe("nineteen", "the entry 19.00 is the number 19");
        output["c"]!.GetValue<string>().ShouldBe("def", "7.10 is not 7");
        output["lit"]!.GetValue<string>().ShouldBe("ten");
        output["text"]!.GetValue<string>().ShouldBe("def", "a string is matched by its text");
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
    public async Task An_output_nested_deeper_than_the_limit_is_a_permanent_depth_error_and_the_limit_itself_renders()
    {
        // Built by iteration, so the evaluation depth cap does not see it; the host reads 64 levels at most.
        var deep = await RenderAsync("""$reduce([1..600], function($acc, $v){ {"a": $acc} }, {})""");
        deep.Error!.Kind.ShouldBe(RenderErrorKinds.DepthLimit);
        deep.Error.Code.ShouldBe("OUTPUT_DEPTH");
        deep.Error.IsPermanent.ShouldBeTrue();

        var over = await RenderAsync($$"""$reduce([1..{{RenderLimits.MaxOutputDepth}}], function($acc, $v){ {"a": $acc} }, {})""");
        over.Error!.Code.ShouldBe("OUTPUT_DEPTH", "one more level than the limit (the innermost {} is a level too)");

        var limit = await RenderAsync($$"""$reduce([1..{{RenderLimits.MaxOutputDepth - 1}}], function($acc, $v){ {"a": $acc} }, {})""");
        limit.Error.ShouldBeNull();
        limit.OutputText!.Count(c => c == '{').ShouldBe(RenderLimits.MaxOutputDepth);

        // A container shared in many places is walked once per depth, not once per path.
        var shared = await RenderAsync("""$reduce([1..30], function($acc, $v){ {"l": $acc, "r": $acc} }, 1) ~> $type()""");
        shared.Error.ShouldBeNull();
        (await OutputAsync("1 + 1"))!.GetValue<int>().ShouldBe(2);
    }

    [Theory]
    [InlineData('(', ')', "1")]
    [InlineData('[', ']', "1")]
    [InlineData('{', '}', "")]
    public async Task An_expression_nesting_past_the_limit_is_refused_before_the_renderer_parses_it(char open, char close, string inner)
    {
        var expression = new string(open, 5000) + inner + new string(close, 5000);

        var result = await RenderAsync(expression);
        result.Error!.Kind.ShouldBe(RenderErrorKinds.DepthLimit);
        result.Error.Code.ShouldBe("NESTING");
        result.Error.IsPermanent.ShouldBeTrue();
        (await Renderer.CheckAsync(expression, null, TestContext.Current.CancellationToken)).Error!.Code.ShouldBe("NESTING");
    }

    [Fact]
    public async Task A_long_run_of_minus_signs_is_refused_and_brackets_in_strings_and_comments_do_not_count()
    {
        (await RenderAsync(new string('-', 8000) + "1")).Error!.Code.ShouldBe("NESTING");
        (await RenderAsync(string.Join(" ", Enumerable.Repeat("-", 1500)) + " 1")).Error!.Code.ShouldBe("NESTING");

        var brackets = new string('(', 3000);
        (await OutputAsync($"\"{brackets}\" & '{brackets}' & /* {brackets} */ \"x\""))!.GetValue<string>().ShouldBe(brackets + brackets + "x");
        (await OutputAsync("1 - 1 - (-1) - -1"))!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public async Task A_long_operator_chain_is_answered_rather_than_crashing_the_renderer()
    {
        // Not bracket nesting, so not refused up front: the renderer's large stack keeps the engine from overflowing.
        var chain = "1" + string.Concat(Enumerable.Repeat(" + 1", 50_000));

        var result = await RenderAsync(chain);

        (result.Error?.Kind).ShouldNotBe(RenderErrorKinds.Unavailable, result.Error?.Message);
        (await OutputAsync("1 + 1"))!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public async Task An_error_message_is_cut_to_the_limit_in_the_renderer()
    {
        var result = await RenderAsync("""$error($join([1..100000].("x" & $string($))))""");

        result.Error!.Kind.ShouldBe(RenderErrorKinds.Runtime);
        result.Error.Message.Length.ShouldBeLessThanOrEqualTo(RenderLimits.MaxErrorMessageLength);
        result.Error.Message.ShouldEndWith("\u2026");
        result.Error.Message.ShouldStartWith("x1x2x3");
    }

    [Fact]
    public async Task A_renderer_that_stops_while_running_an_expression_is_tried_once_more_and_then_fails_the_render_for_good()
    {
        var folder = Directory.CreateTempSubdirectory("docproc-renderer-crash").FullName;
        try
        {
            // A stand-in renderer that reports ready, reads the request, says it started and exits: what an uncatchable
            // stack overflow in the engine looks like to the host.
            var crashing = Script(folder, "crash.sh", """
                echo '{"ready":true}'
                read line
                echo '{"started":true}'
                exit 134
                """);
            using (var renderer = Stub(crashing))
            {
                var result = await renderer.RenderAsync(new RenderRequest("1", null, Today), TestContext.Current.CancellationToken);
                result.Error!.Kind.ShouldBe(RenderErrorKinds.DepthLimit);
                result.Error.Code.ShouldBe("CRASH");
                result.Error.IsPermanent.ShouldBeTrue();
                (await renderer.CheckAsync("1", null, TestContext.Current.CancellationToken)).Error!.Code.ShouldBe("CRASH");
            }

            // Stopping once (the process was killed under it) and answering on the fresh process is no error.
            var marker = Path.Combine(folder, "crashed-once");
            var once = Script(folder, "once.sh", $$"""
                echo '{"ready":true}'
                read line
                echo '{"started":true}'
                if [ ! -f '{{marker}}' ]; then : > '{{marker}}'; exit 137; fi
                echo '{"id":0,"ok":true,"undefined":false,"output":7,"outputBytes":1,"elapsedMs":0,"schemaChecked":false,"schemaErrors":[],"referencedPaths":[],"tables":[]}'
                read line
                """);
            using (var renderer = Stub(once))
            {
                var result = await renderer.RenderAsync(new RenderRequest("7", null, Today), TestContext.Current.CancellationToken);
                result.Error.ShouldBeNull();
                result.Output!.GetValue<int>().ShouldBe(7);
            }

            // A renderer that stops before it has read the request is unavailable, which is retried later.
            var down = Script(folder, "down.sh", """
                echo '{"ready":true}'
                exit 1
                """);
            using (var renderer = Stub(down))
            {
                var result = await renderer.RenderAsync(new RenderRequest("1", null, Today), TestContext.Current.CancellationToken);
                result.Error!.Kind.ShouldBe(RenderErrorKinds.Unavailable);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        static string Script(string folder, string name, string text)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, text.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
            return path;
        }

        static JsonataRenderer Stub(string script) =>
            new(Options.Create(new JsonataRendererOptions { PoolSize = 1, RendererPath = script, DotnetHostPath = "/bin/sh" }));
    }

    [Fact]
    public void A_renderer_that_writes_thousands_of_error_lines_logs_the_first_ones()
    {
        // What a stack overflow looks like on standard error: a frame per line.
        var logger = new CollectingLogger();
        var log = new JsonataRenderer.ErrorOutputLog(logger);

        log.Line(null);
        log.Line(string.Empty);
        for (var i = 0; i < 5000; i++)
        {
            log.Line($"   at Jsonata.Net.Native.Impl.Parser.Frame{i}");
        }

        logger.Count(_ => true).ShouldBe(JsonataRenderer.MaxLoggedErrorLines + 1);
        logger.Count(m => m.Contains("Frame0", StringComparison.Ordinal)).ShouldBe(1);
        logger.Count(m => m.Contains("further lines", StringComparison.Ordinal)).ShouldBe(1);
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

/// <summary>The messages a <see cref="JsonataRenderer"/> logs.</summary>
internal sealed class CollectingLogger : Microsoft.Extensions.Logging.ILogger<JsonataRenderer>
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();

    public int Count(Func<string, bool> predicate) => _messages.Count(predicate);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _messages.Enqueue(formatter(state, exception));
}
