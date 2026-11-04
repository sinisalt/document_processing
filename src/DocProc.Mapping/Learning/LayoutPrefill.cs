using System.Text.Json.Nodes;
using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Mapping.Identification;
using DocProc.Mapping.Layouts;
using DocProc.Mapping.Text;
using DocProc.Mapping.Values;

namespace DocProc.Mapping.Learning;

/// <summary>Where a pre-filled value came from (docs/11 §12.10 step 4): the chip the review form shows on it.</summary>
public static class PrefillSources
{
    public const string MasterData = "masterData";
    public const string OtherLayout = "otherLayout";
    public const string KnownValue = "knownValue";
    public const string PendingLayout = "pendingLayout";
    public const string Llm = "llm";
}

/// <summary>A value the pre-fill searches for on the page: the supplier's master data or a value that was the same on every approved document of the supplier.</summary>
public sealed record PrefillValue(string Path, string Kind, string Text, string Source, bool WriteUnplaced = false);

/// <summary>
/// The <c>layout-prefill</c> step's search (docs/11 §9.3, §12.10 step 4) for a document of a known supplier that no
/// layout recognised: the labels and value patterns of the supplier's other layouts searched anywhere in the document
/// (their positions ignored: a label rule's page and every region rule are dropped), and master-data and known values
/// located by their text. Everything it finds is Inferred, with the place it was found, so the reviewer only confirms
/// it and the learner gets its position. It never applies another layout's positions.
/// </summary>
public static class LayoutPrefill
{
    /// <summary>Confidence of a value found by another layout's label.</summary>
    public const double LabelConfidence = 0.75;

    /// <summary>Confidence of a master-data or known value found on the page.</summary>
    public const double LocatedConfidence = 0.8;

    /// <summary>Confidence of a master-data value not found on the page.</summary>
    public const double UnplacedConfidence = 0.6;

    /// <summary>
    /// A field mapping of the other layouts' position-free rules: per path, the label and pattern rules of every layout
    /// (the first layouts first, at most <see cref="LayoutMapping.MaxRulesPerField"/>), their transforms from the first
    /// layout that has the path; constants are kept, regions and tables left out.
    /// </summary>
    public static LayoutMapping FromLayouts(IEnumerable<LayoutMapping> others)
    {
        ArgumentNullException.ThrowIfNull(others);
        var fields = new JsonObject();
        foreach (var mapping in others)
        {
            foreach (var field in mapping.Fields)
            {
                var rules = field.Rules.Where(r => r.Strategy is LayoutStrategies.LabelRight or LayoutStrategies.LabelBelow or LayoutStrategies.RegexAnywhere or LayoutStrategies.Constant).ToList();
                if (rules.Count == 0)
                {
                    continue;
                }

                if (fields[field.Path] is not JsonObject target)
                {
                    target = new JsonObject { ["rules"] = new JsonArray() };
                    if (field.Transforms.Count > 0)
                    {
                        target["transforms"] = new JsonArray([.. field.Transforms.Select(t => (JsonNode?)Transform(t))]);
                    }

                    fields[field.Path] = target;
                }

                var list = (JsonArray)target["rules"]!;
                foreach (var rule in rules)
                {
                    if (list.Count >= LayoutMapping.MaxRulesPerField)
                    {
                        break;
                    }

                    var json = Rule(rule);
                    if (!list.Any(r => JsonNode.DeepEquals(r, json)))
                    {
                        list.Add(json);
                    }
                }
            }
        }

        return LayoutMapping.Parse(new JsonObject { ["fields"] = fields }.ToJsonString());
    }

    /// <summary>Runs the position-free mapping: every hit Inferred, at the label confidence, named after the layout rule it came from.</summary>
    public static IReadOnlyList<LayoutHit> Search(LayoutMapping mapping, DocumentWords words, Func<string, LayoutFieldKind?> kindOf, ValueLocale locale, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var run = LayoutEngine.Run(mapping, words, "prefill@0", kindOf, _ => null, locale, cancellationToken: cancellationToken);
        return [.. run.Hits.Select(h => h with { Confidence = Math.Min(h.Confidence, LabelConfidence), Inferred = true, RuleRef = PrefillSources.OtherLayout, Strategy = PrefillSources.OtherLayout })];
    }

    /// <summary>
    /// The values located on the page by their text (identifiers compacted, other text word by word, case-insensitive),
    /// each with the place it was found; a value with <see cref="PrefillValue.WriteUnplaced"/> that is printed nowhere is
    /// still written, without a place, at a lower confidence.
    /// </summary>
    public static IReadOnlyList<LayoutHit> Locate(DocumentWords words, IReadOnlyList<PrefillValue> values, ValueLocale locale)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(values);
        var hits = new List<LayoutHit>();
        foreach (var value in values)
        {
            var parsed = PrintedValues.Parse(value.Kind, value.Text, locale);
            if (parsed.Value is null)
            {
                continue;
            }

            if (Find(words, value.Text) is { } found)
            {
                hits.Add(new LayoutHit(value.Path, value.Kind, found.Text, parsed.Value, found.Page, found.Box, value.Source, value.Source, LocatedConfidence, Inferred: true));
            }
            else if (value.WriteUnplaced)
            {
                hits.Add(new LayoutHit(value.Path, value.Kind, value.Text, parsed.Value, null, null, value.Source, value.Source, UnplacedConfidence, Inferred: true));
            }
        }

        return hits;
    }

    /// <summary>Where <paramref name="text"/> is printed: the first run of words of one line that reads as it (compacted for identifiers, token by token otherwise).</summary>
    public static (int Page, BoundingBox Box, string Text)? Find(DocumentWords words, string text)
    {
        ArgumentNullException.ThrowIfNull(words);
        var compact = Identifiers.NormalizeCompact(text);
        var tokens = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => IdentificationRuleEvaluator.Token(t)).ToArray();
        if (tokens.Length == 0)
        {
            return null;
        }

        foreach (var page in words.Pages)
        {
            foreach (var line in PageGeometry.Lines(page.Words))
            {
                for (var start = 0; start < line.Words.Count; start++)
                {
                    // Token by token.
                    if (start + tokens.Length <= line.Words.Count)
                    {
                        var all = true;
                        for (var k = 0; k < tokens.Length && all; k++)
                        {
                            all = string.Equals(IdentificationRuleEvaluator.Token(line.Words[start + k].Text), tokens[k], StringComparison.Ordinal);
                        }

                        if (all)
                        {
                            var run = line.Words.Skip(start).Take(tokens.Length).ToList();
                            return (page.Number, PageGeometry.Union(run)!.Value, string.Join(' ', run.Select(w => w.Text)));
                        }
                    }

                    // Compacted, for an identifier printed with other spacing ("DE 811 111 111").
                    if (compact is { Length: >= 5 })
                    {
                        var joined = string.Empty;
                        for (var end = start; end < line.Words.Count && end < start + 8; end++)
                        {
                            joined += Identifiers.NormalizeCompact(line.Words[end].Text) ?? string.Empty;
                            if (string.Equals(joined, compact, StringComparison.Ordinal))
                            {
                                var run = line.Words.Skip(start).Take(end - start + 1).ToList();
                                return (page.Number, PageGeometry.Union(run)!.Value, string.Join(' ', run.Select(w => w.Text)));
                            }

                            if (joined.Length >= compact.Length)
                            {
                                break;
                            }
                        }
                    }
                }
            }
        }

        return null;
    }

    private static JsonObject Rule(LayoutFieldRule rule)
    {
        var json = new JsonObject { ["strategy"] = rule.Strategy };
        if (rule.Labels is { Count: > 0 } labels)
        {
            json["labels"] = new JsonArray([.. labels.Select(l => (JsonNode?)JsonValue.Create(l))]);
            json["maxDistance"] = rule.MaxDistance;
        }

        if (rule.Pattern is { } pattern)
        {
            json["pattern"] = pattern;
            if (rule.Group > 0)
            {
                json["group"] = rule.Group;
            }
        }

        if (rule.Value is { } value)
        {
            json["value"] = value;
        }

        return json;
    }

    private static JsonObject Transform(LayoutTransform t)
    {
        var json = new JsonObject { ["op"] = t.Op };
        if (t.Pattern is { } p)
        {
            json["pattern"] = p;
        }

        if (t.Group > 0)
        {
            json["group"] = t.Group;
        }

        if (t.With is { } w)
        {
            json["with"] = w;
        }

        return json;
    }
}
