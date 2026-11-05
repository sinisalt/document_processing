using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DocProc.Definitions;

/// <summary>
/// A document type definition (docs/11 §5.2): the JSON a <c>DocumentTypeVersion</c> stores and a platform template
/// ships as. The records mirror the format one to one, so <see cref="DefinitionSerializer"/> can read a definition
/// strictly: an unknown property anywhere is an error, which is what keeps a typo from silently dropping a setting.
/// </summary>
public sealed record TypeDefinition
{
    /// <summary>The type key (<c>^[A-Za-z][A-Za-z0-9_.-]{0,63}$</c>); a platform template's key is its template id, such as <c>std.invoice</c>.</summary>
    public required string Key { get; init; }

    public required int Version { get; init; }

    /// <summary>
    /// Platform templates only: the key every existing tenant adopts the template under (docs/11 §21 step 2), which
    /// is the <c>DocumentTypeKey</c> name the stored documents already carry (<c>Invoice</c>, <c>Other</c>, ...).
    /// </summary>
    public string? LegacyKey { get; init; }

    /// <summary>The template and version a copy or an adoption was made from; null on a template or a new type.</summary>
    public DefinitionBase? Base { get; init; }

    /// <summary>The display name by locale (<c>en</c>, <c>de</c>, ...).</summary>
    public required IReadOnlyDictionary<string, string> Name { get; init; }

    public string? Description { get; init; }

    public DefinitionRoles Roles { get; init; } = new();

    public DefinitionUi? Ui { get; init; }

    /// <summary>The field tree, in display order. Empty for a type without canonical data.</summary>
    public required IReadOnlyList<FieldDefinition> Fields { get; init; }

    public IReadOnlyList<RuleDefinition> Rules { get; init; } = [];

    /// <summary>
    /// The definition's own flag codes (docs/11 §11, "definition-scoped codes <c>X_&lt;RULEID&gt;</c>"): each a code its
    /// rules may raise (<see cref="RuleDefinition.Flag"/>) with its severity and message. Empty for none.
    /// </summary>
    public IReadOnlyList<FlagDeclaration> Flags { get; init; } = [];

    public ProcessingDefinition Processing { get; init; } = new();

    public ClassificationDefinition? Classification { get; init; }

    public LlmDefinition? Llm { get; init; }
}

/// <summary>Where a definition came from (<c>base</c>): a template, its version and how it was taken.</summary>
public sealed record DefinitionBase
{
    public required string Template { get; init; }

    public required int Version { get; init; }

    /// <summary><c>adopt</c> (follows the template) or <c>copy</c> (a fork).</summary>
    public required string Mode { get; init; }
}

/// <summary>
/// One field (docs/11 §5.2). A field is one <c>FieldValue</c> envelope in the canonical tree unless
/// <see cref="Provenance"/> is false; <see cref="List"/> makes it a list of such values, and the kind <c>array</c>
/// a repeating group whose rows are described by <see cref="Item"/>.
/// </summary>
public sealed record FieldDefinition
{
    /// <summary>Stable and immutable, of the form <c>fld_xxx</c>; unique within the definition.</summary>
    public required string Id { get; init; }

    /// <summary>The JSON path segment, unique among its siblings.</summary>
    public required string Key { get; init; }

    /// <summary>One of <see cref="FieldKinds"/>.</summary>
    public required string Kind { get; init; }

    public IReadOnlyDictionary<string, string>? Label { get; init; }

    public string? Description { get; init; }

    public bool Required { get; init; }

    public bool ReadOnly { get; init; }

    /// <summary>The <c>ui.sections</c> entry the field is shown in (top-level fields).</summary>
    public string? Section { get; init; }

    public int? Order { get; init; }

    public VisibleWhen? VisibleWhen { get; init; }

    public ExtractionHints? Extraction { get; init; }

    public bool Indexed { get; init; }

    public bool Sensitive { get; init; }

    public bool Deprecated { get; init; }

    public string? Pattern { get; init; }

    public decimal? Min { get; init; }

    public decimal? Max { get; init; }

    public int? MaxLength { get; init; }

    /// <summary>On an <c>array</c>: the row cap of the repeating group (at most 20,000).</summary>
    public int? MaxItems { get; init; }

    /// <summary>On a <c>money</c> field: the field that holds its currency.</summary>
    public string? CurrencyFrom { get; init; }

    /// <summary>On an <c>enum</c>: the inline codes (or <see cref="CodeList"/>).</summary>
    public IReadOnlyList<EnumOption>? Options { get; init; }

    /// <summary>On an <c>enum</c> or <c>unit</c>: the lookup table of codes and aliases.</summary>
    public string? CodeList { get; init; }

    /// <summary>On an <c>array</c>: the row field that anchors a row on the page (<c>LocateLine</c>).</summary>
    public string? Anchor { get; init; }

    /// <summary>On an <c>array</c>: the fields of one row.</summary>
    public FieldGroup? Item { get; init; }

    /// <summary>On an <c>object</c>: the nested fields, each with its own envelope.</summary>
    public IReadOnlyList<FieldDefinition>? Fields { get; init; }

    /// <summary>A list of values of <see cref="Kind"/> (for example the order references), not a single value.</summary>
    public bool List { get; init; }

    /// <summary>
    /// False for a member that the canonical tree stores as a bare value, without the <c>FieldValue</c> envelope and
    /// its provenance. Only the legacy records have such members (a line number, the printed unit, the VAT summary);
    /// they are part of the definition so its paths are exactly the stored tree's, but no correction addresses them.
    /// </summary>
    public bool Provenance { get; init; } = true;
}

/// <summary>The fields of one row of an <c>array</c>.</summary>
public sealed record FieldGroup
{
    public required IReadOnlyList<FieldDefinition> Fields { get; init; }
}

public sealed record EnumOption
{
    public required string Code { get; init; }

    public IReadOnlyDictionary<string, string>? Label { get; init; }
}

/// <summary>Shows a field only when a sibling field equals a value.</summary>
public sealed record VisibleWhen
{
    public required string Field { get; init; }

    /// <summary>The value the sibling must have (<c>equals</c> in the JSON).</summary>
    [JsonPropertyName("equals")]
    public required JsonNode Value { get; init; }
}

/// <summary>Hints for reading a field from a page: printed labels by language, a pattern, a note for the LLM.</summary>
public sealed record ExtractionHints
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Labels { get; init; }

    public string? Pattern { get; init; }

    public string? LlmHint { get; init; }
}

/// <summary>
/// The role bindings (docs/11 §5.2): which field plays each well-known role. Paths are field paths, dotted for nested
/// fields and with <c>[*]</c> for the rows of an array (<c>lines[*].lineNet</c>). Every role is optional.
/// </summary>
public sealed record DefinitionRoles
{
    public string? DocumentNumber { get; init; }

    public string? DocumentDate { get; init; }

    public string? DueDate { get; init; }

    public string? Currency { get; init; }

    public string? Total { get; init; }

    public string? Net { get; init; }

    public string? Tax { get; init; }

    public string? ValidFrom { get; init; }

    public string? ValidUntil { get; init; }

    public string? SupplierParty { get; init; }

    public string? CustomerParty { get; init; }

    public string? LineItems { get; init; }

    public string? LineNet { get; init; }

    public string? LineQuantity { get; init; }

    public string? LineUnitPrice { get; init; }

    public string? LineSku { get; init; }

    public string? TaxBreakdown { get; init; }

    /// <summary>The components of the duplicate check: field paths, and <c>$supplierId</c> for the matched supplier.</summary>
    public IReadOnlyList<string>? DuplicateKey { get; init; }
}

public sealed record DefinitionUi
{
    public IReadOnlyList<UiSection> Sections { get; init; } = [];
}

public sealed record UiSection
{
    public required string Id { get; init; }

    public IReadOnlyDictionary<string, string>? Label { get; init; }
}

/// <summary>One entry of the rule list (docs/11 §11): a stable id, a registered kind and its parameters.</summary>
public sealed record RuleDefinition
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public JsonObject? Params { get; init; }

    /// <summary><c>blocking</c>, <c>warning</c> or <c>info</c>; null keeps the kind's own severity.</summary>
    public string? Severity { get; init; }

    public IReadOnlyDictionary<string, string>? Message { get; init; }

    public string? AppliesWhen { get; init; }

    /// <summary>The definition flag (a code of <see cref="TypeDefinition.Flags"/>) the rule raises instead of its kind's codes; null keeps the kind's.</summary>
    public string? Flag { get; init; }
}

/// <summary>
/// A flag code a definition declares (docs/11 §11): <c>X_</c> and upper-case letters, digits and <c>_</c>, at most 64
/// characters, with the severity it is raised with (unless the rule sets one) and its message by locale.
/// </summary>
public sealed record FlagDeclaration
{
    public required string Code { get; init; }

    /// <summary><c>blocking</c>, <c>warning</c> or <c>info</c>; null for warning.</summary>
    public string? Severity { get; init; }

    public IReadOnlyDictionary<string, string>? Message { get; init; }

    public string? Description { get; init; }
}

/// <summary>The steps per phase (docs/11 §10) and the review gate.</summary>
public sealed record ProcessingDefinition
{
    public SplitPolicy? Split { get; init; }

    public ClassifyPolicy? Classify { get; init; }

    public IReadOnlyList<StepDefinition> Extract { get; init; } = [];

    public IReadOnlyList<StepDefinition> Validate { get; init; } = [];

    public ReviewPolicy Review { get; init; } = new();
}

public sealed record SplitPolicy
{
    /// <summary><c>auto</c>, <c>never</c>, <c>perPage</c> or <c>llm</c>.</summary>
    public required string Mode { get; init; }
}

public sealed record ClassifyPolicy
{
    public double? LlmThreshold { get; init; }
}

/// <summary>One step of a phase: a stable id, a registered step kind and its parameters.</summary>
public sealed record StepDefinition
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public JsonObject? Params { get; init; }
}

/// <summary>The review gate of the type (docs/11 §5.2 and §11, layer 3).</summary>
public sealed record ReviewPolicy
{
    /// <summary>Whether a document of this type may be approved without a reviewer. False keeps every document in review.</summary>
    public bool AutoApprove { get; init; }

    public double? MinConfidence { get; init; }

    /// <summary><c>normal</c>, <c>high</c> or <c>urgent</c>.</summary>
    public string? Priority { get; init; }

    /// <summary>A date role (<c>validFrom</c> or <c>validUntil</c>) that sets the review task's due date.</summary>
    public string? DueBy { get; init; }

    public IReadOnlyList<string>? RequirePassedRules { get; init; }

    public IReadOnlyList<string>? BlockOnWarnings { get; init; }

    public LinesPolicy? Lines { get; init; }
}

public sealed record LinesPolicy
{
    public IReadOnlyList<string>? RequireSource { get; init; }

    public bool RequireLocated { get; init; }

    public int? MaxLowConfidenceRows { get; init; }
}

/// <summary>
/// The fuzzy keyword signals of the keyword classifier (docs/11 §5.2), by language: titles (<see cref="Keywords"/>),
/// document-number labels, date labels and negative phrases, with the weights the classifier gives each kind.
/// </summary>
public sealed record ClassificationDefinition
{
    public string? Description { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Keywords { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Labels { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>>? DateLabels { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Negatives { get; init; }
}

public sealed record LlmDefinition
{
    public string? Guidance { get; init; }

    /// <summary>The fixed extraction adapter of the type, such as <c>invoice_extraction@1</c>; null for a definition-compiled schema.</summary>
    public string? Adapter { get; init; }

    /// <summary><c>template</c> for the template's examples; P1 adds lists of examples.</summary>
    public string? Examples { get; init; }
}
