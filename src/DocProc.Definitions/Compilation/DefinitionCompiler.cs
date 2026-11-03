using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DocProc.Definitions.Compilation;

/// <summary>What the compiler may use beyond the definition: the fixed LLM schemas of the extraction adapters.</summary>
public sealed record CompileOptions
{
    public static CompileOptions Default { get; } = new();

    /// <summary>
    /// The LLM schema of each extraction adapter a definition can name (<c>llm.adapter</c>), by adapter id
    /// (<c>invoice_extraction@1</c>). The LLM project supplies them (<c>CanonicalSchemas</c>); a definition naming an
    /// adapter that is not listed does not compile.
    /// </summary>
    public IReadOnlyDictionary<string, JsonObject> AdapterSchemas { get; init; } = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
}

/// <summary>The LLM structured-output schema of a definition (artifact 2 of docs/11 §5.5).</summary>
/// <param name="Name">The schema name sent to the provider: <c>doc_&lt;key&gt;</c>, or the adapter's name.</param>
/// <param name="Version">The first 16 hex digits of the schema's SHA-256, or the adapter's version (<c>1</c>, <c>1+x.&lt;hash8&gt;</c> with custom fields).</param>
/// <param name="Schema">The schema itself.</param>
/// <param name="Hash">The first 16 hex digits of the SHA-256 of the compiled schema JSON (<c>LlmSchemaHash</c>).</param>
/// <param name="Adapter">The adapter whose schema this extends (<c>invoice_extraction@1</c>), or null for a definition-compiled schema.</param>
/// <param name="CustomPaths">With an adapter: the tenant's added fields carried in <c>x_custom</c>.</param>
public sealed record CompiledLlmSchema(string Name, string Version, JsonObject Schema, string Hash, string? Adapter, IReadOnlyList<string> CustomPaths)
{
    public string Json => JsonCanonicalizer.Compact(Schema);

    public override string ToString() => $"{Name}@{Version}";
}

/// <summary>
/// The four artifacts of a published definition version (docs/11 §5.5), stored on <c>DocumentTypeVersion</c>, plus
/// the normalised definition JSON and its content hash.
/// </summary>
public sealed record CompiledDefinition(
    TypeDefinition Definition,
    string DefinitionJson,
    string ContentHash,
    JsonObject JsonSchema,
    CompiledLlmSchema LlmSchema,
    JsonObject FormSpec,
    DescriptorIndex Descriptors)
{
    public string JsonSchemaJson => JsonCanonicalizer.Compact(JsonSchema);

    public string FormSpecJson => JsonCanonicalizer.Compact(FormSpec);
}

/// <summary>The artifact columns of a stored version (<c>DocumentTypeVersions</c>), as <see cref="DefinitionCompiler.FromStored"/> reads them.</summary>
public sealed record StoredArtifacts(
    string? JsonSchema,
    string? LlmSchema,
    string? LlmSchemaName,
    string? LlmSchemaVersion,
    string? LlmSchemaHash,
    string? FormSpec,
    string? Descriptors,
    string? ContentHash);

/// <summary>The compiler's answer: the artifacts, or the problems that stop the compile, and notes either way.</summary>
public sealed record CompileResult(CompiledDefinition? Compiled, IReadOnlyList<DefinitionProblem> Problems, IReadOnlyList<DefinitionProblem> Notes)
{
    public bool Succeeded => Compiled is not null && Problems.Count == 0;
}

/// <summary>
/// Compiles a definition into the artifacts the running code takes (docs/11 §5.5):
/// <list type="number">
/// <item><description><b>JSON Schema 2020-12 over the values view</b> (identifier as its verbatim string, money as
/// <c>{amount, currency}</c>, enum as its code, Missing as null): required fields are non-null, field constraints
/// (<c>pattern</c>, <c>min</c>, <c>max</c>, <c>maxLength</c>, <c>maxItems</c>) become keywords.</description></item>
/// <item><description><b>The LLM schema</b> by the portable rules of <c>PortableSchemaGenerator</c> (every property
/// required, optional values nullable, <c>additionalProperties: false</c>, no numeric or length constraints,
/// composites inlined, descriptions copied, the reserved <c>unreadableRegions</c> appended), or, for a type with an
/// extraction adapter, the adapter's schema unchanged plus a nullable <c>x_custom</c> object with the tenant's added
/// header fields when there are any.</description></item>
/// <item><description><b>The form spec</b>: sections and a tree of field specs for the review form.</description></item>
/// <item><description><b>The descriptor index</b> (<see cref="DescriptorIndex"/>).</description></item>
/// </list>
/// Compilation also enforces the size caps of §5.7: at most <see cref="MaxFields"/> fields, a depth of
/// <see cref="MaxDepth"/> and <see cref="MaxEnumValues"/> codes per enum. The structural checks are
/// <see cref="DefinitionChecker"/>'s; the compiler assumes they passed and refuses nothing they already cover.
/// </summary>
public static partial class DefinitionCompiler
{
    /// <summary>The most fields a definition may declare, nested fields included (§5.7).</summary>
    public const int MaxFields = 300;

    /// <summary>The deepest path, counted in segments (<c>lines[*].tiers[*].price</c> is 3).</summary>
    public const int MaxDepth = 6;

    /// <summary>The most inline codes an enum may list.</summary>
    public const int MaxEnumValues = 200;

    /// <summary>The adapter id of the coded invoice extraction (docs/11 §9.3).</summary>
    public const string InvoiceAdapter = "invoice_extraction@1";

    /// <summary>The reserved property of every LLM schema in which the model reports regions it could not read.</summary>
    public const string UnreadableRegions = "unreadableRegions";

    /// <summary>The property of an adapter schema that carries the tenant's added fields.</summary>
    public const string CustomObject = "x_custom";

    public static CompileResult Compile(TypeDefinition definition, CompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        options ??= CompileOptions.Default;
        var problems = new List<DefinitionProblem>(CheckSizeCaps(definition));
        var notes = new List<DefinitionProblem>();

        var llm = CompileLlmSchema(definition, options, problems, notes);
        if (problems.Count > 0 || llm is null)
        {
            return new CompileResult(null, problems, notes);
        }

        var definitionNode = JsonNode.Parse(DefinitionSerializer.Write(definition))!;
        var definitionJson = JsonCanonicalizer.Compact(definitionNode);
        var compiled = new CompiledDefinition(
            definition,
            definitionJson,
            JsonCanonicalizer.Sha256Hex(definitionNode),
            CompileJsonSchema(definition),
            llm,
            CompileFormSpec(definition),
            CompileDescriptors(definition));
        return new CompileResult(compiled, problems, notes);
    }

    /// <summary>
    /// The artifacts a published version stored (docs/11 §5.5: <c>CompiledJsonSchema</c>, <c>CompiledLlmSchema</c> with
    /// its name, version and hash, <c>FormSpecJson</c>, <c>DescriptorsJson</c>, <c>ContentHash</c>), read back instead
    /// of compiled again, so a later build with another compiler or template changes nothing about a published,
    /// pinned version. Null when a column is missing or does not read.
    /// </summary>
    public static CompiledDefinition? FromStored(TypeDefinition definition, StoredArtifacts stored)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(stored);
        if (stored is not { JsonSchema: { } jsonSchemaJson, LlmSchema: { } llmJson, LlmSchemaName: { } name, LlmSchemaVersion: { } version, LlmSchemaHash: { } hash, FormSpec: { } formSpecJson, Descriptors: { } descriptorsJson, ContentHash: { } contentHash })
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(jsonSchemaJson) is not JsonObject jsonSchema || JsonNode.Parse(llmJson) is not JsonObject llm || JsonNode.Parse(formSpecJson) is not JsonObject formSpec)
            {
                return null;
            }

            var adapter = AdapterOf(definition);
            IReadOnlyList<string> customPaths = adapter is not null && llm["properties"]?[CustomObject]?["properties"] is JsonObject custom
                ? [.. custom.Select(p => p.Key)]
                : [];
            var definitionJson = JsonCanonicalizer.Compact(JsonNode.Parse(DefinitionSerializer.Write(definition))!);
            return new CompiledDefinition(
                definition,
                definitionJson,
                contentHash,
                jsonSchema,
                new CompiledLlmSchema(name, version, llm, hash, adapter, customPaths),
                formSpec,
                DescriptorIndex.FromJson(descriptorsJson));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The size caps of docs/11 §5.7: at most <see cref="MaxFields"/> fields, a depth of <see cref="MaxDepth"/> and
    /// <see cref="MaxEnumValues"/> codes per enum. Compiling checks them, and so does saving a draft.
    /// </summary>
    public static IReadOnlyList<DefinitionProblem> CheckSizeCaps(TypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var problems = new List<DefinitionProblem>();
        var all = DefinitionPaths.Enumerate(definition).ToList();
        if (all.Count > MaxFields)
        {
            problems.Add(new("fields", $"declares {all.Count} fields; at most {MaxFields} are allowed."));
        }

        foreach (var (path, field) in all)
        {
            var depth = path.Split('.').Length;
            if (depth > MaxDepth)
            {
                problems.Add(new(path, $"is nested {depth} levels deep; at most {MaxDepth} are allowed."));
            }

            if (field.Options is { Count: > MaxEnumValues } options)
            {
                problems.Add(new(path, $"lists {options.Count} codes; at most {MaxEnumValues} are allowed."));
            }
        }

        return problems;
    }

    /// <summary><see cref="Compile"/> for a definition that must compile (a platform template); throws otherwise.</summary>
    public static CompiledDefinition CompileOrThrow(TypeDefinition definition, CompileOptions? options = null)
    {
        var result = Compile(definition, options);
        return result.Succeeded
            ? result.Compiled!
            : throw new DefinitionFormatException($"Definition '{definition.Key}' v{definition.Version} does not compile: {string.Join("; ", result.Problems)}");
    }

    // ---------------------------------------------------------------- 1. JSON Schema over the values view

    private static JsonObject CompileJsonSchema(TypeDefinition definition)
    {
        var root = ObjectSchema(definition.Fields, nullable: false);
        var schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = $"{definition.Key} v{definition.Version} (values view)",
        };
        foreach (var (key, value) in root.ToList())
        {
            root.Remove(key);
            schema[key] = value;
        }

        return schema;
    }

    private static JsonObject ObjectSchema(IReadOnlyList<FieldDefinition> fields, bool nullable)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var field in fields)
        {
            var isRequired = field.Required && !field.Deprecated;
            properties[field.Key] = FieldSchema(field, nullable: !isRequired);
            if (isRequired)
            {
                required.Add(field.Key);
            }
        }

        var schema = new JsonObject { ["type"] = nullable ? new JsonArray("object", "null") : "object", ["properties"] = properties };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static JsonObject FieldSchema(FieldDefinition field, bool nullable)
    {
        JsonObject schema;
        if (field.List)
        {
            schema = new JsonObject { ["type"] = Type("array", nullable), ["items"] = ValueSchema(field, nullable: false) };
            if (!nullable)
            {
                schema["minItems"] = 1;
            }
        }
        else
        {
            schema = ValueSchema(field, nullable);
        }

        if (field.Description is { Length: > 0 } description)
        {
            schema["description"] = description;
        }

        if (field.ReadOnly || !field.Provenance)
        {
            schema["readOnly"] = true;
        }

        if (field.Deprecated)
        {
            schema["deprecated"] = true;
        }

        return schema;
    }

    private static JsonObject ValueSchema(FieldDefinition field, bool nullable)
    {
        switch (field.Kind)
        {
            case FieldKinds.Array:
                var rows = new JsonObject { ["type"] = Type("array", nullable), ["items"] = ObjectSchema(field.Item?.Fields ?? [], nullable: false) };
                if (field.MaxItems is { } maxItems)
                {
                    rows["maxItems"] = maxItems;
                }

                return rows;
            case FieldKinds.Object:
                return ObjectSchema(field.Fields ?? [], nullable);
            case FieldKinds.Bag:
                return new JsonObject
                {
                    ["type"] = Type("object", nullable),
                    ["additionalProperties"] = new JsonObject { ["type"] = new JsonArray("string", "number", "boolean", "null") },
                };
            case FieldKinds.Enum when field.Options is { } options:
                return new JsonObject
                {
                    ["type"] = Type("string", nullable),
                    ["enum"] = new JsonArray([.. options.Select(o => (JsonNode?)o.Code), .. nullable ? new JsonNode?[] { null } : []]),
                };
            case FieldKinds.Money:
                var amount = new JsonObject { ["type"] = "number" };
                Bounds(amount, field);
                return new JsonObject
                {
                    ["type"] = Type("object", nullable),
                    ["properties"] = new JsonObject { ["amount"] = amount, ["currency"] = new JsonObject { ["type"] = new JsonArray("string", "null") } },
                    ["required"] = new JsonArray("amount"),
                };
            default:
                var scalar = KindSchema(field.Kind, nullable);
                if (field.Kind is FieldKinds.Integer or FieldKinds.Decimal or FieldKinds.Percent)
                {
                    Bounds(scalar, field);
                }

                if (field.Pattern is { } pattern && field.Kind is FieldKinds.String or FieldKinds.Text or FieldKinds.Identifier)
                {
                    scalar["pattern"] = pattern;
                }

                if (field.MaxLength is { } maxLength && field.Kind is FieldKinds.String or FieldKinds.Text or FieldKinds.Identifier)
                {
                    scalar["maxLength"] = maxLength;
                }

                return scalar;
        }
    }

    /// <summary>The values-view schema of a kind with no field attributes (also the shape of composite members).</summary>
    private static JsonObject KindSchema(string kind, bool nullable)
    {
        switch (kind)
        {
            case FieldKinds.String or FieldKinds.Text or FieldKinds.Identifier or FieldKinds.Unit or FieldKinds.Enum:
                return new JsonObject { ["type"] = Type("string", nullable) };
            case FieldKinds.Currency:
                return new JsonObject { ["type"] = Type("string", nullable), ["pattern"] = "^[A-Z]{3}$" };
            case FieldKinds.Integer:
                return new JsonObject { ["type"] = Type("integer", nullable) };
            case FieldKinds.Decimal or FieldKinds.Percent:
                return new JsonObject { ["type"] = Type("number", nullable) };
            case FieldKinds.Date:
                return new JsonObject { ["type"] = Type("string", nullable), ["format"] = "date" };
            case FieldKinds.Boolean:
                return new JsonObject { ["type"] = Type("boolean", nullable) };
            default:
                var members = new JsonObject();
                foreach (var (member, memberKind) in FieldKinds.CompositeMemberKinds[kind])
                {
                    members[member] = KindSchema(memberKind, nullable: true);
                }

                return new JsonObject { ["type"] = Type("object", nullable), ["properties"] = members };
        }
    }

    private static void Bounds(JsonObject schema, FieldDefinition field)
    {
        if (field.Min is { } min)
        {
            schema["minimum"] = min;
        }

        if (field.Max is { } max)
        {
            schema["maximum"] = max;
        }
    }

    private static JsonNode Type(string type, bool nullable) => nullable ? new JsonArray(type, "null") : type;

    // ---------------------------------------------------------------- 2. LLM schema

    private static CompiledLlmSchema? CompileLlmSchema(TypeDefinition definition, CompileOptions options, List<DefinitionProblem> problems, List<DefinitionProblem> notes)
    {
        var adapter = AdapterOf(definition);
        if (adapter is null)
        {
            var properties = LlmProperties(definition.Fields);
            properties[UnreadableRegions] = UnreadableRegionsSchema();
            var schema = LlmObject(properties, nullable: false);
            var hash = Hash16(schema);
            return new CompiledLlmSchema($"doc_{SchemaNamePattern().Replace(definition.Key, "_")}", hash, schema, hash, null, []);
        }

        if (!options.AdapterSchemas.TryGetValue(adapter, out var adapterSchema))
        {
            problems.Add(new("llm.adapter", $"the extraction adapter '{adapter}' is not available."));
            return null;
        }

        var at = adapter.IndexOf('@', StringComparison.Ordinal);
        var (name, version) = at > 0 ? (adapter[..at], adapter[(at + 1)..]) : (adapter, "1");

        // The tenant's added fields: header fields the template the type comes from does not have, in the version the
        // type was taken from (a later template version would make other fields the added ones).
        var template = definition.Base is { } baseRef ? StandardTemplates.Find(baseRef.Template, baseRef.Version) : null;
        if (definition.Base is { } missingBase && template is null)
        {
            problems.Add(new("base", $"the template {missingBase.Template} v{missingBase.Version} the type comes from is not available, so its added fields cannot be told apart."));
            return null;
        }

        var templateKeys = (template ?? definition).Fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var custom = definition.Fields.Where(f => !templateKeys.Contains(f.Key) && IsLlmField(f)).ToList();
        if (template is not null)
        {
            var templatePaths = DefinitionPaths.Enumerate(template).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var (path, field) in DefinitionPaths.Enumerate(definition).Where(f => f.Path.Contains('.', StringComparison.Ordinal) && !templatePaths.Contains(f.Path) && IsLlmField(f.Field)))
            {
                var top = path[..path.IndexOf('.', StringComparison.Ordinal)].Replace("[*]", string.Empty, StringComparison.Ordinal);
                if (templateKeys.Contains(top))
                {
                    notes.Add(new(path, $"is not read by the {adapter} extraction: only added header fields go into {CustomObject}; a reviewer or a supplier mapping fills it."));
                }
            }
        }

        if (custom.Count == 0)
        {
            var unchanged = (JsonObject)adapterSchema.DeepClone();
            return new CompiledLlmSchema(name, version, unchanged, Hash16(unchanged), adapter, []);
        }

        var extension = LlmObject(LlmProperties(custom), nullable: true);
        extension["description"] = "Fields this tenant added to the document type. Fill them from the document; null when not printed.";
        var extended = new JsonObject();
        foreach (var (key, value) in (JsonObject)adapterSchema.DeepClone())
        {
            if (key == "properties" && value is JsonObject adapterProperties)
            {
                var merged = new JsonObject();
                foreach (var (property, propertySchema) in adapterProperties.ToList())
                {
                    if (property == UnreadableRegions)
                    {
                        merged[CustomObject] = extension.DeepClone();
                    }

                    adapterProperties.Remove(property);
                    merged[property] = propertySchema;
                }

                merged[CustomObject] ??= extension.DeepClone();
                extended[key] = merged;
            }
            else if (key == "required" && value is JsonArray required)
            {
                var names = required.Select(r => r!.GetValue<string>()).ToList();
                var index = names.IndexOf(UnreadableRegions);
                names.Insert(index < 0 ? names.Count : index, CustomObject);
                extended[key] = new JsonArray([.. names.Select(n => (JsonNode)n)]);
            }
            else
            {
                extended[key] = value?.DeepClone();
            }
        }

        var customHash = JsonCanonicalizer.Sha256Hex(extension)[..8];
        return new CompiledLlmSchema(name, $"{version}+x.{customHash}", extended, Hash16(extended), adapter, [.. custom.Select(f => f.Key)]);
    }

    /// <summary>The extraction adapter of a definition: <c>llm.adapter</c>, or the <c>adapter</c> parameter of its <c>llm</c> step.</summary>
    public static string? AdapterOf(TypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Llm?.Adapter is { Length: > 0 } adapter)
        {
            return adapter;
        }

        var step = definition.Processing.Extract.FirstOrDefault(s => s.Kind == "llm");
        return step?.Params?["adapter"] is JsonValue value && value.TryGetValue<string>(out var named) ? named : null;
    }

    private static bool IsLlmField(FieldDefinition field) =>
        field.Provenance && !field.ReadOnly && !field.Deprecated && field.Kind != FieldKinds.Bag;

    private static JsonObject LlmProperties(IEnumerable<FieldDefinition> fields)
    {
        var properties = new JsonObject();
        foreach (var field in fields.Where(IsLlmField))
        {
            var schema = field.List
                ? new JsonObject { ["type"] = "array", ["items"] = LlmValue(field, nullable: false) }
                : LlmValue(field, nullable: field.Kind != FieldKinds.Array);
            var description = string.Join(" ", new[] { field.Description, field.Extraction?.LlmHint }.Where(d => !string.IsNullOrWhiteSpace(d)));
            if (description.Length > 0)
            {
                schema["description"] = description;
            }

            properties[field.Key] = schema;
        }

        return properties;
    }

    private static JsonObject LlmValue(FieldDefinition field, bool nullable)
    {
        switch (field.Kind)
        {
            case FieldKinds.Array:
                return new JsonObject { ["type"] = "array", ["items"] = LlmObject(LlmProperties(field.Item?.Fields ?? []), nullable: false) };
            case FieldKinds.Object:
                return LlmObject(LlmProperties(field.Fields ?? []), nullable);
            case FieldKinds.Enum when field.Options is { } options:
                return new JsonObject
                {
                    ["type"] = Type("string", nullable),
                    ["enum"] = new JsonArray([.. options.Select(o => (JsonNode?)o.Code), .. nullable ? new JsonNode?[] { null } : []]),
                };
            case FieldKinds.Money:
                // The amount only, as the invoice adapter asks for it; the currency is a field of its own (currencyFrom).
                return new JsonObject { ["type"] = Type("number", nullable) };
            default:
                return LlmKind(field.Kind, nullable);
        }
    }

    private static JsonObject LlmKind(string kind, bool nullable)
    {
        switch (kind)
        {
            case FieldKinds.String or FieldKinds.Text or FieldKinds.Identifier or FieldKinds.Unit or FieldKinds.Enum or FieldKinds.Currency:
                return new JsonObject { ["type"] = Type("string", nullable) };
            case FieldKinds.Integer:
                return new JsonObject { ["type"] = Type("integer", nullable) };
            case FieldKinds.Decimal or FieldKinds.Percent or FieldKinds.Money:
                return new JsonObject { ["type"] = Type("number", nullable) };
            case FieldKinds.Date:
                return new JsonObject { ["type"] = Type("string", nullable), ["format"] = "date" };
            case FieldKinds.Boolean:
                return new JsonObject { ["type"] = Type("boolean", nullable) };
            default:
                var members = new JsonObject();
                foreach (var (member, memberKind) in FieldKinds.CompositeMemberKinds[kind])
                {
                    members[member] = LlmKind(memberKind, nullable: true);
                }

                return LlmObject(members, nullable);
        }
    }

    private static JsonObject LlmObject(JsonObject properties, bool nullable) => new()
    {
        ["type"] = Type("object", nullable),
        ["properties"] = properties,
        ["additionalProperties"] = false,
        ["required"] = new JsonArray([.. properties.Select(p => (JsonNode)p.Key)]),
    };

    /// <summary>The reserved list of unreadable regions, as <c>invoice_extraction@1</c> has it.</summary>
    private static JsonObject UnreadableRegionsSchema() => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["page"] = new JsonObject { ["type"] = "integer" },
                ["area"] = new JsonObject { ["type"] = "string" },
                ["reason"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray("page", "area", "reason"),
            ["additionalProperties"] = false,
        },
    };

    private static string Hash16(JsonObject schema) => JsonCanonicalizer.Sha256Hex(JsonCanonicalizer.Compact(schema))[..16];

    // ---------------------------------------------------------------- 3. Form spec

    private static JsonObject CompileFormSpec(TypeDefinition definition)
    {
        var sections = new JsonArray();
        foreach (var section in definition.Ui?.Sections ?? [])
        {
            var entry = new JsonObject { ["id"] = section.Id };
            if (section.Label is { } label)
            {
                entry["label"] = Locales(label);
            }

            sections.Add(entry);
        }

        return new JsonObject
        {
            ["formatVersion"] = 1,
            ["key"] = definition.Key,
            ["version"] = definition.Version,
            ["name"] = Locales(definition.Name),
            ["sections"] = sections,
            ["fields"] = FieldSpecs(definition.Fields, prefix: null),
        };
    }

    private static JsonArray FieldSpecs(IReadOnlyList<FieldDefinition> fields, string? prefix)
    {
        var specs = new JsonArray();
        var ordered = fields.Select((f, i) => (Field: f, Index: i)).OrderBy(f => f.Field.Order ?? int.MaxValue).ThenBy(f => f.Index).Select(f => f.Field);
        foreach (var field in ordered)
        {
            var path = prefix is null ? field.Key : $"{prefix}.{field.Key}";
            var spec = new JsonObject
            {
                ["path"] = path,
                ["fieldId"] = field.Id,
                ["key"] = field.Key,
                ["kind"] = field.Kind,
            };
            if (field.Label is { } label)
            {
                spec["label"] = Locales(label);
            }

            Set(spec, "description", field.Description);
            Set(spec, "section", field.Section);
            spec["required"] = field.Required;
            spec["readOnly"] = field.ReadOnly || !field.Provenance;
            Flag(spec, "list", field.List);
            Flag(spec, "deprecated", field.Deprecated);
            Flag(spec, "sensitive", field.Sensitive);
            Flag(spec, "indexed", field.Indexed);
            if (!field.Provenance)
            {
                spec["provenance"] = false;
            }

            if (field.Options is { } options)
            {
                spec["options"] = new JsonArray([.. options.Select(o =>
                {
                    var option = new JsonObject { ["code"] = o.Code };
                    if (o.Label is { } optionLabel)
                    {
                        option["label"] = Locales(optionLabel);
                    }

                    return (JsonNode)option;
                })]);
            }

            Set(spec, "codeList", field.CodeList);
            Set(spec, "currencyPath", field.CurrencyFrom);
            if (field.VisibleWhen is { } visible)
            {
                spec["visibleWhen"] = new JsonObject { ["path"] = visible.Field, ["equals"] = visible.Value.DeepClone() };
            }

            Set(spec, "pattern", field.Pattern);
            if (field.Min is { } min)
            {
                spec["min"] = min;
            }

            if (field.Max is { } max)
            {
                spec["max"] = max;
            }

            if (field.MaxLength is { } maxLength)
            {
                spec["maxLength"] = maxLength;
            }

            if (field.MaxItems is { } maxItems)
            {
                spec["maxItems"] = maxItems;
            }

            Set(spec, "anchor", field.Anchor);
            if (FieldKinds.CompositeMemberKinds.TryGetValue(field.Kind, out var members) && field.Kind is not (FieldKinds.Identifier or FieldKinds.Money))
            {
                spec["members"] = new JsonArray([.. members.Select(m => (JsonNode)new JsonObject { ["key"] = m.Member, ["kind"] = m.Kind })]);
            }

            switch (field.Kind)
            {
                case FieldKinds.Array:
                    spec["children"] = FieldSpecs(field.Item?.Fields ?? [], $"{path}[*]");
                    break;
                case FieldKinds.Object:
                    spec["children"] = FieldSpecs(field.Fields ?? [], path);
                    break;
                default:
                    break;
            }

            specs.Add(spec);
        }

        return specs;
    }

    private static JsonObject Locales(IReadOnlyDictionary<string, string> values)
    {
        var result = new JsonObject();
        foreach (var (locale, text) in values)
        {
            result[locale] = text;
        }

        return result;
    }

    private static void Set(JsonObject target, string name, string? value)
    {
        if (value is not null)
        {
            target[name] = value;
        }
    }

    private static void Flag(JsonObject target, string name, bool value)
    {
        if (value)
        {
            target[name] = true;
        }
    }

    // ---------------------------------------------------------------- 4. Descriptor index

    private static DescriptorIndex CompileDescriptors(TypeDefinition definition)
    {
        var descriptors = new List<FieldDescriptor>();
        foreach (var (path, field) in DefinitionPaths.Enumerate(definition))
        {
            var rowPrefix = field.Kind == FieldKinds.Array ? $"{path}[*]" : path;
            var children = field.Kind switch
            {
                FieldKinds.Array => (field.Item?.Fields ?? []).Select(c => $"{rowPrefix}.{c.Key}").ToList(),
                FieldKinds.Object => (field.Fields ?? []).Select(c => $"{rowPrefix}.{c.Key}").ToList(),
                _ => [],
            };
            descriptors.Add(new FieldDescriptor
            {
                Path = path,
                FieldId = field.Id,
                Key = field.Key,
                Kind = field.Kind,
                IsList = field.List,
                Children = children,
                Members = FieldKinds.CompositeMemberKinds.TryGetValue(field.Kind, out var members) ? [.. members.Select(m => new MemberDescriptor(m.Member, m.Kind))] : [],
                CurrencyFrom = field.CurrencyFrom,
                Required = field.Required,
                Sensitive = field.Sensitive,
                Indexed = field.Indexed,
                Provenance = field.Provenance,
                ReadOnly = field.ReadOnly || !field.Provenance,
                Deprecated = field.Deprecated,
                Options = field.Options?.Select(o => o.Code).ToList(),
                CodeList = field.CodeList,
                Anchor = field.Anchor,
                MaxItems = field.MaxItems,
            });
        }

        return new DescriptorIndex(descriptors);
    }

    [GeneratedRegex("[^A-Za-z0-9_-]", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaNamePattern();
}
