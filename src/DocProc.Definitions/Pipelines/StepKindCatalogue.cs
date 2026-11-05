using System.Collections.Frozen;
using System.Text.Json.Nodes;

namespace DocProc.Definitions.Pipelines;

/// <summary>The fixed phases of the pipeline (docs/11 §10.1), in the order a document passes them.</summary>
public static class PipelinePhases
{
    public const string Split = "split";
    public const string Classify = "classify";
    public const string Match = "match";
    public const string Extract = "extract";
    public const string Validate = "validate";
    public const string Publish = "publish";

    public static readonly IReadOnlyList<string> All = [Split, Classify, Match, Extract, Validate, Publish];
}

/// <summary>
/// A step kind of the catalogue (docs/11 §10.2, <c>GET /step-kinds</c>): its key, its phase, what it does, the JSON
/// Schema of its <c>params</c>, and how overrides may treat it.
/// </summary>
/// <param name="Kind">The key (<c>llm</c>, <c>classify.identification</c>, <c>http-lookup</c>).</param>
/// <param name="Phase">The phase it belongs to (<see cref="PipelinePhases"/>).</param>
/// <param name="Description">What it does.</param>
/// <param name="ParamsSchema">The JSON Schema its <c>params</c> must fit.</param>
/// <param name="Fixed">
/// A step no override may disable, replace or move anything ahead of (<c>classify.identification</c>, docs/11 §10.1; the
/// review gate, match and publish steps, Correction (P5)); a fixed step with parameters takes <c>param</c> only.
/// </param>
/// <param name="Insertable">Whether an override may add a step of this kind (<c>insertAfter</c>, <c>replace</c>).</param>
/// <param name="Runs">
/// Whether this build runs the kind. A kind that does not run yet (<c>defaults</c>, <c>code-list</c>, <c>transform</c>) is
/// listed in a resolved pipeline so the type's definition reads as written, and is reported as not run.
/// </param>
/// <param name="DefaultId">The id the built-in step of this kind has when the definition names none.</param>
public sealed record StepKindDescriptor(string Kind, string Phase, string Description, JsonObject ParamsSchema, bool Fixed, bool Insertable, bool Runs, string DefaultId);

/// <summary>
/// The step kinds of this deployment per phase (docs/11 §10.1). The phases themselves are fixed; each phase handler runs
/// the steps of its phase from the resolved pipeline (<see cref="PipelineResolver"/>). Which steps a phase runs by
/// default is <see cref="PipelineDefaults"/>; this catalogue says what each kind is and which parameters it takes.
/// </summary>
public static class StepKindCatalogue
{
    public const string SplitHeuristic = "split.heuristic";
    public const string SplitLlm = "split.llm";
    public const string ClassifyIdentification = "classify.identification";
    public const string ClassifyKeyword = "classify.keyword";
    public const string ClassifyLlm = "classify.llm";
    public const string MatchSource = "match.source";
    public const string MatchVatIban = "match.vatIban";
    public const string MatchName = "match.name";
    public const string MatchLayout = "match.layout";
    public const string XmlEInvoice = "xml-einvoice";
    public const string SupplierMapping = "supplier-mapping";
    public const string LayoutPrefill = "layout-prefill";
    public const string Llm = "llm";
    public const string Defaults = "defaults";
    public const string CodeList = "code-list";
    public const string Transform = "transform";
    public const string HttpLookup = "http-lookup";
    public const string ValidateSchema = "validate.schema";
    public const string ValidateRules = "validate.rules";
    public const string ValidateDuplicate = "validate.duplicate";
    public const string ReviewGate = "review-gate";
    public const string PublishOutbox = "publish.outbox";
    public const string PublishFanout = "publish.fanout";

    /// <summary>The longest JSONata expression an <c>http-lookup</c> step holds (its request or its response mapping).</summary>
    public const int MaxLookupExpressionLength = 16 * 1024;

    /// <summary>The longest time one call of an <c>http-lookup</c> step may take (docs/11 §10.2: 5 s).</summary>
    public const int MaxLookupTimeoutMs = 5000;

    /// <summary>The most retries of one <c>http-lookup</c> call after a retryable failure.</summary>
    public const int MaxLookupRetries = 2;

    /// <summary>Every kind of this build, by key.</summary>
    public static FrozenDictionary<string, StepKindDescriptor> Available { get; } = Build().ToFrozenDictionary(d => d.Kind, StringComparer.Ordinal);

    /// <summary>The kinds of one phase, in catalogue order.</summary>
    public static IReadOnlyList<StepKindDescriptor> OfPhase(string phase) => [.. Build().Where(d => d.Phase == phase)];

    public static StepKindDescriptor? Find(string kind) => Available.GetValueOrDefault(kind);

    private static IEnumerable<StepKindDescriptor> Build()
    {
        yield return new(SplitHeuristic, PipelinePhases.Split, "Splits an attachment by its page signals; mode never keeps it whole, perPage makes one document per page, llm always asks the LLM splitter.", Params(("mode", Enum("auto", "never", "perPage", "llm"))), Fixed: false, Insertable: false, Runs: true, DefaultId: SplitHeuristic);
        yield return new(SplitLlm, PipelinePhases.Split, "Asks the LLM splitter when the heuristics are not confident or the attachment is long.", NoParams(), Fixed: false, Insertable: false, Runs: true, DefaultId: SplitLlm);

        yield return new(ClassifyIdentification, PipelinePhases.Classify, "The supplier's layouts (identification rules) decide the type and the layout; always the first step of Classify.", NoParams(), Fixed: true, Insertable: false, Runs: true, DefaultId: ClassifyIdentification);
        yield return new(ClassifyKeyword, PipelinePhases.Classify, "The weighted keyword rules of the tenant's types.", NoParams(), Fixed: false, Insertable: false, Runs: true, DefaultId: ClassifyKeyword);
        yield return new(ClassifyLlm, PipelinePhases.Classify, "The LLM classifier, asked below the threshold (default PipelineOptions.LlmClassificationThreshold) or for Unknown.", Params(("threshold", Number(0, 1))), Fixed: false, Insertable: false, Runs: true, DefaultId: ClassifyLlm);

        yield return new(MatchSource, PipelinePhases.Match, "Corroborates a supplier the source named (a source rule, the channel default, a hint).", NoParams(), Fixed: true, Insertable: false, Runs: true, DefaultId: MatchSource);
        yield return new(MatchVatIban, PipelinePhases.Match, "Matches the supplier by VAT id and IBAN.", NoParams(), Fixed: true, Insertable: false, Runs: true, DefaultId: MatchVatIban);
        yield return new(MatchName, PipelinePhases.Match, "Matches the supplier by its e-mail domain and fuzzy name.", NoParams(), Fixed: true, Insertable: false, Runs: true, DefaultId: MatchName);
        yield return new(MatchLayout, PipelinePhases.Match, "Resolves the layouts of a supplier the content named.", NoParams(), Fixed: true, Insertable: false, Runs: true, DefaultId: MatchLayout);

        yield return new(XmlEInvoice, PipelinePhases.Extract, "Reads an e-invoice's XML (UBL, CII).", NoParams(), Fixed: false, Insertable: false, Runs: true, DefaultId: "xml");
        yield return new(SupplierMapping, PipelinePhases.Extract, "Runs the field mapping of the layout the identification rule selected.", NoParams(), Fixed: false, Insertable: false, Runs: true, DefaultId: "map");
        yield return new(LayoutPrefill, PipelinePhases.Extract, "Pre-fills a NEW_LAYOUT document from the pending Candidate, the supplier's other layouts, its master data and the type's labels.", NoParams(), Fixed: false, Insertable: false, Runs: true, DefaultId: "prefill");
        yield return new(
            Llm,
            PipelinePhases.Extract,
            "The LLM extraction (the invoice family's prompt; fill-missing after a layout or the pre-fill). effort sets the reasoning effort; maxCostPerDocument (USD) skips the call once the document's LLM usage reaches it. For a definition-driven type, exclude names top-level fields not asked for (a price list's lines), and lines: perPage reads the rows page by page, one call per page, under maxCostPerDocument (rows the LLM read never auto-approve a price list, review.lines).",
            Params(
                ("adapter", String(64)),
                ("mode", Enum("full", "fill-missing")),
                ("exclude", new JsonObject { ["type"] = "array", ["items"] = String(200), ["maxItems"] = 50 }),
                ("lines", Enum("perPage")),
                ("effort", Enum("none", "low", "medium", "high")),
                ("maxCostPerDocument", Number(0, 1000))),
            Fixed: false,
            Insertable: false,
            Runs: true,
            DefaultId: "llm");
        yield return new(
            Defaults,
            PipelinePhases.Extract,
            "Writes default values as Inferred into fields the extraction left empty (mode ifMissing) or over them (override), never over a correction: values.<field> is {value} (a constant), {from} (the first of these fields, or $source.receivedDate, that has a value) or {fromSupplierSetting: defaults.currency|locale}. Runs for definition-driven types after the extraction chain.",
            Params(("values", new JsonObject
            {
                ["type"] = "object",
                ["maxProperties"] = 50,
                ["additionalProperties"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["value"] = new JsonObject(),
                        ["from"] = new JsonObject
                        {
                            ["anyOf"] = new JsonArray(String(200), new JsonObject { ["type"] = "array", ["items"] = String(200), ["minItems"] = 1, ["maxItems"] = 5 }),
                        },
                        ["fromSupplierSetting"] = Enum("defaults.currency", "locale"),
                        ["mode"] = Enum("ifMissing", "override"),
                    },
                    ["additionalProperties"] = false,
                },
            })),
            Fixed: false,
            Insertable: false,
            Runs: true,
            DefaultId: "defaults");
        yield return new(CodeList, PipelinePhases.Extract, "Maps values through a code list such as units (not run by this build).", OpenParams(), Fixed: false, Insertable: false, Runs: false, DefaultId: "code-list");
        yield return new(Transform, PipelinePhases.Extract, "Value transforms (not run by this build).", OpenParams(), Fixed: false, Insertable: false, Runs: false, DefaultId: "transform");
        yield return new(
            HttpLookup,
            PipelinePhases.Extract,
            "Calls an http destination of the tenant during Extract and writes the mapped answer into fields as Inferred (docs/11 §10.2). request is a JSONata expression over the values view whose result fills the URL template's {{output.*}} placeholders (and is the body of a POST); response maps {response, status, values} to an object of field paths and values.",
            Params(
                ["destinationId", "response"],
                ("destinationId", new JsonObject { ["type"] = "string", ["format"] = "uuid", ["minLength"] = 36, ["maxLength"] = 36 }),
                ("method", Enum("GET", "POST")),
                ("request", String(MaxLookupExpressionLength)),
                ("response", String(MaxLookupExpressionLength)),
                ("timeoutMs", Integer(100, MaxLookupTimeoutMs)),
                ("retries", Integer(0, MaxLookupRetries)),
                ("onFailure", Enum("warn", "block", "ignore")),
                ("mode", Enum("fillMissing", "override"))),
            Fixed: false,
            Insertable: true,
            Runs: true,
            DefaultId: "lookup");

        yield return new(ValidateSchema, PipelinePhases.Validate, "Checks the values view against the type's compiled JSON Schema (FIELD_REQUIRED, FIELD_INVALID).", NoParams(), Fixed: false, Insertable: false, Runs: true, DefaultId: "schema");
        yield return new(ValidateRules, PipelinePhases.Validate, "Runs the type's rule list.", NoParams(), Fixed: false, Insertable: false, Runs: true, DefaultId: "rules");
        yield return new(ValidateDuplicate, PipelinePhases.Validate, "The coded duplicate check (supplier and document number).", NoParams(), Fixed: false, Insertable: false, Runs: true, DefaultId: "duplicate");
        yield return new(ReviewGate, PipelinePhases.Validate, "Decides auto-approval or review (docs/11 §11 layer 3); always the last step of Validate.", NoParams(), Fixed: true, Insertable: false, Runs: true, DefaultId: ReviewGate);

        yield return new(PublishOutbox, PipelinePhases.Publish, "Hands the approved document on through the outbox.", NoParams(), Fixed: true, Insertable: false, Runs: true, DefaultId: PublishOutbox);
        yield return new(PublishFanout, PipelinePhases.Publish, "Renders and queues the deliveries of the matching output bindings.", NoParams(), Fixed: true, Insertable: false, Runs: true, DefaultId: PublishFanout);
    }

    private static JsonObject NoParams() => new() { ["type"] = "object", ["additionalProperties"] = false };

    /// <summary>The kinds this build does not run yet take what their definition says, unchecked beyond being an object.</summary>
    private static JsonObject OpenParams() => new() { ["type"] = "object" };

    private static JsonObject Params(params (string Name, JsonObject Schema)[] properties) => Params([], properties);

    private static JsonObject Params(string[] required, params (string Name, JsonObject Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties)
        {
            props[name] = schema;
        }

        var result = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        if (required.Length > 0)
        {
            result["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]);
        }

        return result;
    }

    private static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)v)]) };

    private static JsonObject String(int maxLength) => new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = maxLength };

    private static JsonObject Number(double minimum, double maximum) => new() { ["type"] = "number", ["minimum"] = minimum, ["maximum"] = maximum };

    private static JsonObject Integer(int minimum, int maximum) => new() { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum };
}
