using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;

namespace DocProc.Llm.Extraction;

/// <summary>
/// Maps the answer of a definition-driven extraction (docs/11 §9.3, <see cref="LlmDefinitionExtractor"/>) into the
/// canonical tree of the type version: every field the schema asked for becomes a <c>FieldValue</c> envelope by its kind
/// (the kinds <see cref="CustomFieldMapper"/> maps for <c>x_custom</c>), located in the text layer by
/// <see cref="ProvenanceLocator"/> with LLM provenance; a list is a list of envelopes, a group (<c>object</c>) holds its
/// fields, a table (<c>array</c>) holds one row per answer row, located on the page of its anchor field. A money value
/// takes its currency from its <c>currencyFrom</c> field's answer, else the tenant's currency. A value the model did not
/// give, or one that does not fit its kind, is left out; the Extract stage completes the tree with Missing envelopes.
/// Bare members (<c>provenance: false</c>), read-only, deprecated and bag fields are never asked for and never mapped.
/// </summary>
public static class DefinitionMapper
{
    /// <summary>The most rows one table of an answer is mapped with (the meta-schema's cap on <c>maxItems</c>).</summary>
    public const int MaxRows = DefinitionChecker.MaxItemsLimit;

    public static JsonObject Map(JsonObject answer, DescriptorIndex descriptors, ProvenanceLocator locator, string? tenantCurrency)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(locator);
        var tree = new JsonObject();
        MapLevel(tree, answer, descriptors.Roots.ToList(), descriptors, locator, tenantCurrency, answer);
        return tree;
    }

    private static void MapLevel(JsonObject target, JsonObject answer, IReadOnlyList<FieldDescriptor> fields, DescriptorIndex descriptors, ProvenanceLocator locator, string? tenantCurrency, JsonObject root)
    {
        foreach (var field in fields)
        {
            if (!IsAsked(field) || !answer.TryGetPropertyValue(field.Key, out var raw) || raw is null)
            {
                continue;
            }

            switch (field.Kind)
            {
                case FieldKinds.Array when raw is JsonArray rows:
                    {
                        var children = field.Children.Select(c => descriptors.Find(c)).OfType<FieldDescriptor>().ToList();
                        var mapped = new JsonArray();
                        foreach (var row in rows.OfType<JsonObject>().Take(MaxRows))
                        {
                            var target2 = new JsonObject();
                            MapLevel(target2, row, children, descriptors, locator, tenantCurrency, root);
                            if (target2.Count > 0)
                            {
                                mapped.Add(target2);
                            }
                        }

                        target[field.Key] = mapped;
                        break;
                    }

                case FieldKinds.Object when raw is JsonObject group:
                    {
                        var children = field.Children.Select(c => descriptors.Find(c)).OfType<FieldDescriptor>().ToList();
                        var mapped = new JsonObject();
                        MapLevel(mapped, group, children, descriptors, locator, tenantCurrency, root);
                        target[field.Key] = mapped;
                        break;
                    }

                case FieldKinds.Array or FieldKinds.Object:
                    break;

                default:
                    {
                        var currency = field.Kind == FieldKinds.Money ? CurrencyOf(field, answer, root) ?? tenantCurrency : null;
                        if (field.IsList)
                        {
                            if (raw is JsonArray list)
                            {
                                target[field.Key] = new JsonArray([.. list.Select(item => CustomFieldMapper.Envelope(field, item, locator, currency)).OfType<JsonNode>()]);
                            }
                        }
                        else if (CustomFieldMapper.Envelope(field, raw, locator, currency) is { } envelope)
                        {
                            target[field.Key] = envelope;
                        }

                        break;
                    }
            }
        }
    }

    private static bool IsAsked(FieldDescriptor field) =>
        field.Provenance && !field.ReadOnly && !field.Deprecated && field.Kind != FieldKinds.Bag;

    /// <summary>The currency the answer gives a money field's <c>currencyFrom</c> field: a sibling's in the row, else at the root.</summary>
    private static string? CurrencyOf(FieldDescriptor field, JsonObject level, JsonObject root)
    {
        if (field.CurrencyFrom is not { } path)
        {
            return null;
        }

        var key = path[(path.LastIndexOf('.') + 1)..];
        var node = level[key] ?? (path.Contains('.', StringComparison.Ordinal) ? null : root[path]);
        return node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { Length: 3 } code
            ? code.ToUpperInvariant()
            : null;
    }
}
