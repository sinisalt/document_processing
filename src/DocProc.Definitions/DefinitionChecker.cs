using System.Text.RegularExpressions;
using DocProc.Domain.Validation;

namespace DocProc.Definitions;

/// <summary>One thing wrong with a definition: where (a JSON-ish path into it) and what.</summary>
public sealed record DefinitionProblem(string Where, string Message)
{
    public override string ToString() => $"{Where}: {Message}";
}

/// <summary>
/// The structural publish-time checks of docs/11 §5.7 that need no compiler: key and id syntax, unique keys and ids,
/// known kinds and the attributes each kind allows, roles bound to fields of a fitting kind, rule kinds registered in
/// this deployment, step kinds of the right phase, and review-gate references. The compiler (P1) adds the meta-schema,
/// the portability and size caps and the dependents check on top. A definition with no problems is well formed; the
/// platform templates must have none.
/// </summary>
public static partial class DefinitionChecker
{
    /// <summary>The most rows a repeating group may declare (docs/11 §5.2).</summary>
    public const int MaxItemsLimit = 20_000;

    private static readonly HashSet<string> Severities = new(StringComparer.Ordinal) { "blocking", "warning", "info" };
    private static readonly HashSet<string> Priorities = new(StringComparer.Ordinal) { "normal", "high", "urgent" };
    private static readonly HashSet<string> SplitModes = new(StringComparer.Ordinal) { "auto", "never", "perPage", "llm" };

    /// <summary>The problems of <paramref name="definition"/>; rule kinds must be in <paramref name="registeredRuleKinds"/>.</summary>
    public static IReadOnlyList<DefinitionProblem> Check(TypeDefinition definition, IEnumerable<string> registeredRuleKinds)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(registeredRuleKinds);
        var problems = new List<DefinitionProblem>();
        var ruleKinds = registeredRuleKinds.ToHashSet(StringComparer.Ordinal);

        if (!TypeKeyPattern().IsMatch(definition.Key))
        {
            problems.Add(new("key", $"'{definition.Key}' is not a valid type key (a letter, then letters, digits, '_', '.' or '-', at most 64)."));
        }

        if (definition.Version < 1)
        {
            problems.Add(new("version", "must be 1 or more."));
        }

        if (definition.Name.Count == 0 || definition.Name.Values.Any(string.IsNullOrWhiteSpace))
        {
            problems.Add(new("name", "needs a non-empty name in at least one locale."));
        }

        if (definition.Base is { } baseRef && baseRef.Mode is not ("adopt" or "copy"))
        {
            problems.Add(new("base.mode", $"'{baseRef.Mode}' is neither 'adopt' nor 'copy'."));
        }

        var sections = definition.Ui?.Sections.Select(s => s.Id).ToList();
        if (sections is not null)
        {
            foreach (var duplicate in sections.GroupBy(s => s, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                problems.Add(new("ui.sections", $"section '{duplicate.Key}' is declared twice."));
            }
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        CheckFields(definition, definition.Fields, "fields", topLevel: true, sections, ids, problems);
        CheckRoles(definition, problems);
        CheckRules(definition, ruleKinds, problems);
        CheckProcessing(definition, problems);
        CheckClassification(definition.Classification, problems);
        return problems;
    }

    private static void CheckFields(
        TypeDefinition definition,
        IReadOnlyList<FieldDefinition> fields,
        string where,
        bool topLevel,
        List<string>? sections,
        HashSet<string> ids,
        List<DefinitionProblem> problems)
    {
        // Sibling keys are unique ignoring case: the field index (DocumentFieldIndex.FieldKey) and the ?field.<key>= filter
        // compare keys under the database's collation, case-insensitive on SQL Server by default, so refNo and refno
        // would share index rows.
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var at = $"{where}[{i}] ({field.Key})";
            if (!FieldIdPattern().IsMatch(field.Id))
            {
                problems.Add(new(at, $"id '{field.Id}' is not of the form fld_xxx (lower-case letters, digits, '_')."));
            }
            else if (!ids.Add(field.Id))
            {
                problems.Add(new(at, $"id '{field.Id}' is used by another field; ids are unique within a definition."));
            }

            if (!FieldKeyPattern().IsMatch(field.Key))
            {
                problems.Add(new(at, $"key '{field.Key}' is not a valid field key (a lower-case letter, then letters, digits or '_')."));
            }
            else if (!keys.TryAdd(field.Key, field.Key))
            {
                problems.Add(new(at, keys[field.Key] == field.Key
                    ? $"key '{field.Key}' is declared twice at this level."
                    : $"key '{field.Key}' differs from the key '{keys[field.Key]}' at this level only by case; keys are unique ignoring case."));
            }

            if (!FieldKinds.All.Contains(field.Kind))
            {
                problems.Add(new(at, $"kind '{field.Kind}' is not a field kind."));
                continue;
            }

            CheckKindAttributes(definition, field, at, problems);

            if (field.Section is { } section)
            {
                if (!topLevel)
                {
                    problems.Add(new(at, "only top-level fields name a section."));
                }
                else if (sections is not null && !sections.Contains(section, StringComparer.Ordinal))
                {
                    problems.Add(new(at, $"section '{section}' is not declared in ui.sections."));
                }
            }

            if (field.Kind == FieldKinds.Array && field.Item is { Fields: { } rowFields })
            {
                CheckFields(definition, rowFields, $"{at}.item.fields", topLevel: false, sections, ids, problems);
            }

            if (field.Kind == FieldKinds.Object && field.Fields is { } nested)
            {
                CheckFields(definition, nested, $"{at}.fields", topLevel: false, sections, ids, problems);
            }
        }
    }

    private static void CheckKindAttributes(TypeDefinition definition, FieldDefinition field, string at, List<DefinitionProblem> problems)
    {
        var isArray = field.Kind == FieldKinds.Array;
        var isObject = field.Kind == FieldKinds.Object;
        if (isArray)
        {
            if (field.Item is not { Fields.Count: > 0 } item)
            {
                problems.Add(new(at, "an array needs item.fields with at least one field."));
            }
            else if (field.Anchor is { } anchor && !item.Fields.Any(f => string.Equals(f.Key, anchor, StringComparison.Ordinal)))
            {
                problems.Add(new(at, $"anchor '{anchor}' is not a field of the row."));
            }

            if (field.MaxItems is { } maxItems && maxItems is < 1 or > MaxItemsLimit)
            {
                problems.Add(new(at, $"maxItems must be between 1 and {MaxItemsLimit}."));
            }
        }
        else
        {
            Forbid(field.Item is not null, "item", "arrays");
            Forbid(field.Anchor is not null, "anchor", "arrays");
            Forbid(field.MaxItems is not null, "maxItems", "arrays");
        }

        if (isObject)
        {
            if (field.Fields is not { Count: > 0 })
            {
                problems.Add(new(at, "an object needs fields."));
            }
        }
        else
        {
            Forbid(field.Fields is not null, "fields", "objects");
        }

        if (field.List && (isArray || isObject || field.Kind == FieldKinds.Bag))
        {
            problems.Add(new(at, $"list cannot be set on a field of kind '{field.Kind}'."));
        }

        if (field.Kind == FieldKinds.Enum)
        {
            if (field.Options is null && field.CodeList is null)
            {
                problems.Add(new(at, "an enum needs options or a codeList."));
            }

            if (field.Options is { } options)
            {
                foreach (var duplicate in options.GroupBy(o => o.Code, StringComparer.Ordinal).Where(g => g.Count() > 1))
                {
                    problems.Add(new(at, $"option '{duplicate.Key}' is listed twice."));
                }
            }
        }
        else
        {
            Forbid(field.Options is not null, "options", "enums");
            Forbid(field.CodeList is not null && field.Kind != FieldKinds.Unit, "codeList", "enums and units");
        }

        if (field.CurrencyFrom is { } currencyFrom)
        {
            if (field.Kind != FieldKinds.Money)
            {
                problems.Add(new(at, "currencyFrom is only allowed on money fields."));
            }
            else if (DefinitionPaths.Find(definition, currencyFrom) is not { Kind: FieldKinds.Currency, List: false })
            {
                problems.Add(new(at, $"currencyFrom '{currencyFrom}' does not name a currency field."));
            }
        }

        if (field.Min is { } min && field.Max is { } max && min > max)
        {
            problems.Add(new(at, "min is greater than max."));
        }

        if (field.MaxLength is < 1)
        {
            problems.Add(new(at, "maxLength must be 1 or more."));
        }

        if (field.Pattern is { } pattern && !IsValidPattern(pattern))
        {
            problems.Add(new(at, PatternSize.IsSmallEnough(pattern) ? "pattern is not a valid regular expression." : TooLarge("pattern", pattern)));
        }

        if (field.Extraction?.Pattern is { } hint && !IsValidPattern(hint))
        {
            problems.Add(new(at, PatternSize.IsSmallEnough(hint) ? "extraction.pattern is not a valid regular expression." : TooLarge("extraction.pattern", hint)));
        }

        if (field.VisibleWhen is { } visible && DefinitionPaths.Find(definition, visible.Field) is null)
        {
            problems.Add(new(at, $"visibleWhen names '{visible.Field}', which is not a field."));
        }

        void Forbid(bool present, string attribute, string allowedOn)
        {
            if (present)
            {
                problems.Add(new(at, $"{attribute} is only allowed on {allowedOn}."));
            }
        }
    }

    private static void CheckRoles(TypeDefinition definition, List<DefinitionProblem> problems)
    {
        var roles = definition.Roles;
        Role("documentNumber", roles.DocumentNumber, FieldKinds.Identifier);
        Role("documentDate", roles.DocumentDate, FieldKinds.Date);
        Role("dueDate", roles.DueDate, FieldKinds.Date);
        Role("validFrom", roles.ValidFrom, FieldKinds.Date);
        Role("validUntil", roles.ValidUntil, FieldKinds.Date);
        Role("currency", roles.Currency, FieldKinds.Currency);
        Role("total", roles.Total, FieldKinds.Money);
        Role("net", roles.Net, FieldKinds.Money);
        Role("tax", roles.Tax, FieldKinds.Money);
        Role("supplierParty", roles.SupplierParty, FieldKinds.Party);
        Role("customerParty", roles.CustomerParty, FieldKinds.Party);
        Role("lineItems", roles.LineItems, FieldKinds.Array);
        Role("taxBreakdown", roles.TaxBreakdown, FieldKinds.TaxRow, list: true);
        LineRole("lineNet", roles.LineNet, FieldKinds.Money);
        LineRole("lineQuantity", roles.LineQuantity, FieldKinds.Decimal);
        LineRole("lineUnitPrice", roles.LineUnitPrice, FieldKinds.Money);
        LineRole("lineSku", roles.LineSku, FieldKinds.String, FieldKinds.Identifier);

        if (roles.DuplicateKey is { } duplicateKey)
        {
            if (duplicateKey.Count == 0)
            {
                problems.Add(new("roles.duplicateKey", "lists no component."));
            }

            foreach (var component in duplicateKey.Where(c => c != "$supplierId" && DefinitionPaths.Find(definition, c) is null))
            {
                problems.Add(new("roles.duplicateKey", $"'{component}' is neither $supplierId nor a field."));
            }
        }

        void Role(string name, string? path, string kind, bool list = false)
        {
            if (path is null)
            {
                return;
            }

            var field = DefinitionPaths.Find(definition, path);
            if (field is null)
            {
                problems.Add(new($"roles.{name}", $"'{path}' is not a field."));
            }
            else if (field.Kind != kind || field.List != list || path.Contains("[*]", StringComparison.Ordinal))
            {
                problems.Add(new($"roles.{name}", $"'{path}' is a {Describe(field)} field; the role needs a single {(list ? "list of " : string.Empty)}{kind} field."));
            }
        }

        void LineRole(string name, string? path, params string[] kinds)
        {
            if (path is null)
            {
                return;
            }

            var rows = roles.LineItems is { } lineItems ? $"{lineItems}[*]." : null;
            var field = DefinitionPaths.Find(definition, path);
            if (rows is null)
            {
                problems.Add(new($"roles.{name}", "a line role needs the lineItems role."));
            }
            else if (!path.StartsWith(rows, StringComparison.Ordinal) || path[rows.Length..].Contains('.', StringComparison.Ordinal))
            {
                problems.Add(new($"roles.{name}", $"'{path}' is not a field of the lineItems rows ({rows}<field>)."));
            }
            else if (field is null)
            {
                problems.Add(new($"roles.{name}", $"'{path}' is not a field."));
            }
            else if (!kinds.Contains(field.Kind, StringComparer.Ordinal) || field.List)
            {
                problems.Add(new($"roles.{name}", $"'{path}' is a {Describe(field)} field; the role needs a {string.Join(" or ", kinds)} field."));
            }
        }

        static string Describe(FieldDefinition field) => field.List ? $"list of {field.Kind}" : field.Kind;
    }

    private static void CheckRules(TypeDefinition definition, HashSet<string> ruleKinds, List<DefinitionProblem> problems)
    {
        // Definition flags (docs/11 §11): codes of the form X_..., declared once, never a catalogue code, each with a
        // known severity; a rule names a declared one, and only a generic kind does (a coded invoice kind keeps its codes).
        var declared = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < definition.Flags.Count; i++)
        {
            var flag = definition.Flags[i];
            var at = $"flags[{i}] ({flag.Code})";
            if (!Domain.Flags.FlagCodes.IsDefinitionScoped(flag.Code))
            {
                problems.Add(new(at, $"'{flag.Code}' is not a definition flag code (X_ and upper-case letters, digits or '_', at most 64 characters)."));
            }
            else if (Domain.Flags.FlagCodes.IsKnown(flag.Code))
            {
                problems.Add(new(at, $"'{flag.Code}' is a platform flag code; a definition declares codes of its own."));
            }
            else if (!declared.Add(flag.Code))
            {
                problems.Add(new(at, $"flag code '{flag.Code}' is declared twice."));
            }

            if (flag.Severity is { } flagSeverity && !Severities.Contains(flagSeverity))
            {
                problems.Add(new(at, $"severity '{flagSeverity}' is not blocking, warning or info."));
            }
        }

        foreach (var unused in declared.Where(code => !definition.Rules.Any(r => r.Flag == code)))
        {
            problems.Add(new("flags", $"flag code '{unused}' is declared but no rule raises it."));
        }

        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < definition.Rules.Count; i++)
        {
            var rule = definition.Rules[i];
            var at = $"rules[{i}] ({rule.Id})";
            if (!ruleIds.Add(rule.Id))
            {
                problems.Add(new(at, $"rule id '{rule.Id}' is used twice."));
            }

            if (!ruleKinds.Contains(rule.Kind))
            {
                problems.Add(new(at, $"rule kind '{rule.Kind}' is not available yet."));
            }

            if (rule.Severity is { } severity && !Severities.Contains(severity))
            {
                problems.Add(new(at, $"severity '{severity}' is not blocking, warning or info."));
            }

            if (rule.Flag is { } code)
            {
                if (!declared.Contains(code))
                {
                    problems.Add(new(at, $"flag '{code}' is not declared in the definition's flags."));
                }

                if (rule.Kind.StartsWith("invoice.", StringComparison.Ordinal))
                {
                    problems.Add(new(at, $"the coded kind '{rule.Kind}' keeps its own flag codes; only a generic kind raises a definition flag."));
                }
            }
        }
    }

    private static void CheckProcessing(TypeDefinition definition, List<DefinitionProblem> problems)
    {
        var processing = definition.Processing;
        if (processing.Split is { } split && !SplitModes.Contains(split.Mode))
        {
            problems.Add(new("processing.split.mode", $"'{split.Mode}' is not auto, never, perPage or llm."));
        }

        if (processing.Classify?.LlmThreshold is < 0 or > 1)
        {
            problems.Add(new("processing.classify.llmThreshold", "must be between 0 and 1."));
        }

        Steps("extract", processing.Extract, StepKinds.Extract);
        Steps("validate", processing.Validate, StepKinds.Validate);

        var review = processing.Review;
        if (review.MinConfidence is < 0 or > 1)
        {
            problems.Add(new("processing.review.minConfidence", "must be between 0 and 1."));
        }

        if (review.Priority is { } priority && !Priorities.Contains(priority))
        {
            problems.Add(new("processing.review.priority", $"'{priority}' is not normal, high or urgent."));
        }

        if (review.DueBy is { } dueBy)
        {
            var bound = dueBy switch
            {
                "validFrom" => definition.Roles.ValidFrom,
                "validUntil" => definition.Roles.ValidUntil,
                _ => null,
            };
            if (bound is null)
            {
                problems.Add(new("processing.review.dueBy", $"'{dueBy}' is not a bound validFrom or validUntil role."));
            }
        }

        var ruleIds = definition.Rules.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in (review.RequirePassedRules ?? []).Concat(review.BlockOnWarnings ?? []).Where(id => !ruleIds.Contains(id)))
        {
            problems.Add(new("processing.review", $"names the rule '{id}', which the rule list does not have."));
        }

        void Steps(string phase, IReadOnlyList<StepDefinition> steps, IReadOnlySet<string> kinds)
        {
            var stepIds = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var at = $"processing.{phase}[{i}] ({step.Id})";
                if (!stepIds.Add(step.Id))
                {
                    problems.Add(new(at, $"step id '{step.Id}' is used twice in {phase}."));
                }

                if (!kinds.Contains(step.Kind))
                {
                    problems.Add(new(at, $"'{step.Kind}' is not a {phase} step kind."));
                }
            }
        }
    }

    private static void CheckClassification(ClassificationDefinition? classification, List<DefinitionProblem> problems)
    {
        if (classification is null)
        {
            return;
        }

        Phrases("keywords", classification.Keywords);
        Phrases("labels", classification.Labels);
        Phrases("dateLabels", classification.DateLabels);
        Phrases("negatives", classification.Negatives);

        void Phrases(string block, IReadOnlyDictionary<string, IReadOnlyList<string>>? byLanguage)
        {
            foreach (var (language, phrases) in byLanguage ?? new Dictionary<string, IReadOnlyList<string>>())
            {
                if (!LanguagePattern().IsMatch(language))
                {
                    problems.Add(new($"classification.{block}", $"'{language}' is not an ISO 639-1 language code."));
                }

                if (phrases.Count == 0 || phrases.Any(string.IsNullOrWhiteSpace))
                {
                    problems.Add(new($"classification.{block}.{language}", "needs non-empty phrases."));
                }

                // The classifier matches a phrase as words separated by white space or hyphens (docs/11 §9.4): a phrase
                // longer than it takes, more than it keeps, or one that is mostly separators is refused here rather
                // than silently dropped or run.
                if (phrases.Count > MaxClassificationPhrases)
                {
                    problems.Add(new($"classification.{block}.{language}", $"has more than {MaxClassificationPhrases} phrases."));
                }

                foreach (var phrase in phrases.Where(p => !string.IsNullOrWhiteSpace(p)))
                {
                    if (phrase.Trim().Length > MaxClassificationPhraseLength)
                    {
                        problems.Add(new($"classification.{block}.{language}", $"'{Shorten(phrase)}' is longer than {MaxClassificationPhraseLength} characters."));
                    }
                    else if (SeparatorRun().IsMatch(phrase) || !phrase.Any(char.IsLetterOrDigit))
                    {
                        problems.Add(new($"classification.{block}.{language}", $"'{Shorten(phrase)}' is not words separated by single spaces or hyphens."));
                    }
                }
            }
        }
    }

    /// <summary>The most phrases of one classification list (type, block and language); the classifier keeps no more.</summary>
    public const int MaxClassificationPhrases = 50;

    /// <summary>The longest classification phrase; the classifier drops longer ones.</summary>
    public const int MaxClassificationPhraseLength = 100;

    /// <summary>More than three spaces or hyphens in a row: not a phrase of words.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"[\s\-]{4,}", System.Text.RegularExpressions.RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial System.Text.RegularExpressions.Regex SeparatorRun();

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "…";

    /// <summary>The message for a pattern above <see cref="PatternSize.MaxUnrolled"/> or <see cref="PatternSize.MaxVariation"/>.</summary>
    internal static string TooLarge(string what, string pattern) =>
        $"{what} repeats too much: {PatternSize.Problem(pattern)}";

    /// <summary>
    /// Whether <paramref name="pattern"/> compiles the way the platform runs it (NonBacktracking, culture-invariant) and
    /// stays within the bounds of <see cref="PatternSize"/>: field patterns, extraction hints and the <c>pattern</c> rule's
    /// <c>params.pattern</c>.
    /// </summary>
    internal static bool IsValidPattern(string pattern)
    {
        if (!PatternSize.IsSmallEnough(pattern))
        {
            return false;
        }

        try
        {
            _ = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            // A construct NonBacktracking cannot run (backreferences, lookarounds) is refused like a syntax error.
            return false;
        }
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex TypeKeyPattern();

    [GeneratedRegex("^fld_[a-z0-9_]{1,60}$")]
    private static partial Regex FieldIdPattern();

    [GeneratedRegex("^[a-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex FieldKeyPattern();

    [GeneratedRegex("^[a-z]{2}$")]
    private static partial Regex LanguagePattern();
}
