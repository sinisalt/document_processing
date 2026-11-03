using System.Collections.Frozen;
using System.Text.Json;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Domain.Validation;

namespace DocProc.Definitions;

/// <summary>
/// The platform templates (docs/11 §5.1), shipped as embedded JSON (<c>Templates/std.*.json</c>) and read once. P0
/// seeds the templates of the legacy types: <c>std.invoice</c>, <c>std.credit-note</c>, <c>std.purchase-order</c>,
/// <c>std.delivery-note</c>, <c>std.statement</c>, <c>std.reminder</c> and <c>std.generic</c> (the legacy key
/// <c>Other</c>). Their field trees are exactly today's canonical records, so the stored documents are already valid
/// against them; <c>std.price-list</c> and <c>std.daily-specials</c> ship with P1. A template that does not load is a
/// build defect, so a bad resource fails the first use loudly rather than leaving a type without a definition.
/// </summary>
public static class StandardTemplates
{
    /// <summary>The resource name prefix of the templates (see the project file).</summary>
    public const string ResourcePrefix = "DocProc.Definitions.Templates.";

    private static readonly Lazy<Catalogue> Loaded = new(Load);

    /// <summary>Every platform template, by template key (<c>std.invoice</c>, ...), in key order.</summary>
    public static IReadOnlyList<TypeDefinition> All => Loaded.Value.All;

    /// <summary>The template with the key <paramref name="templateKey"/>, or null.</summary>
    public static TypeDefinition? Find(string templateKey) => Loaded.Value.ByKey.GetValueOrDefault(templateKey);

    /// <summary>
    /// The template every tenant adopts for a legacy type (docs/11 §21 step 2): the one whose <c>legacyKey</c> is the
    /// type's name. Null for <see cref="DocumentTypeKey.Unknown"/>, which stays a reserved key without a definition.
    /// </summary>
    public static TypeDefinition? ForLegacyType(DocumentTypeKey type) =>
        Loaded.Value.ByLegacyKey.GetValueOrDefault(type.Value);

    /// <summary>The template's JSON exactly as shipped.</summary>
    public static string ReadJson(string templateKey)
    {
        using var stream = typeof(StandardTemplates).Assembly.GetManifestResourceStream($"{ResourcePrefix}{templateKey}.json")
                           ?? throw new DefinitionFormatException($"No platform template '{templateKey}' is embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Catalogue Load()
    {
        var assembly = typeof(StandardTemplates).Assembly;
        var templates = new List<TypeDefinition>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var key = name[ResourcePrefix.Length..^".json".Length];
            TypeDefinition template;
            try
            {
                template = DefinitionSerializer.Parse(ReadJson(key));
            }
            catch (DefinitionFormatException e)
            {
                throw new DefinitionFormatException($"The platform template resource '{name}' does not load: {e.Message}", e);
            }

            if (!string.Equals(template.Key, key, StringComparison.Ordinal))
            {
                throw new DefinitionFormatException($"The platform template resource '{name}' declares the key '{template.Key}'.");
            }

            templates.Add(template);
        }

        var byLegacyKey = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var template in templates.Where(t => t.LegacyKey is not null))
        {
            if (!WellKnownTypeKeys.Legacy.Contains(template.LegacyKey, StringComparer.Ordinal) || template.LegacyKey == WellKnownTypeKeys.Unknown)
            {
                throw new DefinitionFormatException($"The platform template '{template.Key}' names the legacy key '{template.LegacyKey}', which is not a document type.");
            }

            if (!byLegacyKey.TryAdd(template.LegacyKey!, template))
            {
                throw new DefinitionFormatException($"Two platform templates claim the legacy key '{template.LegacyKey}'.");
            }
        }

        return new Catalogue(
            templates,
            templates.ToFrozenDictionary(t => t.Key, StringComparer.Ordinal),
            byLegacyKey.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private sealed record Catalogue(
        IReadOnlyList<TypeDefinition> All,
        FrozenDictionary<string, TypeDefinition> ByKey,
        FrozenDictionary<string, TypeDefinition> ByLegacyKey);
}

/// <summary>How a definition's lists become what the running code takes.</summary>
public static class TypeDefinitionExtensions
{
    /// <summary>
    /// The rule list as the validator runs it (docs/11 §11): one <see cref="RuleReference"/> per entry, in order,
    /// with its parameters as raw JSON values.
    /// </summary>
    public static IReadOnlyList<RuleReference> ToRuleReferences(this TypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return [.. definition.Rules.Select(ToRuleReference)];
    }

    private static RuleReference ToRuleReference(RuleDefinition rule)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, value) in rule.Params ?? [])
        {
            values[name] = JsonSerializer.SerializeToElement(value, DefinitionSerializer.Options);
        }

        FlagSeverity? severity = rule.Severity switch
        {
            "blocking" => FlagSeverity.Blocking,
            "warning" => FlagSeverity.Warning,
            "info" => FlagSeverity.Info,
            _ => null,
        };
        return new RuleReference(rule.Id, rule.Kind, values.Count == 0 ? RuleParams.None : new RuleParams(values), severity, rule.Message);
    }
}
