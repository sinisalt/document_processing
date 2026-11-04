using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Definitions;
using DocProc.Domain.Validation;
using DocProc.Domain.Validation.Rules;

namespace DocProc.Application.Documents;

/// <summary>Why the rule conditions of the review gate hold a document back (docs/11 §11 layer 3).</summary>
public sealed record ReviewGateHold(string Reason, string Detail);

/// <summary>
/// The conditions of the review gate a type's definition sets itself (docs/11 §5.2, §11 layer 3, P5):
/// <c>review.requirePassedRules</c> (every rule named ran and raised no warning or blocking flag; a rule that did not
/// run does not count), <c>review.blockOnWarnings</c> (none of the rules named raised a warning) and
/// <c>review.lines</c> over the rows of the <c>lineItems</c> role (<c>requireSource</c>: every value's
/// <c>source.method</c> is one of the listed methods; <c>requireLocated</c>: every value has a page and a box;
/// <c>maxLowConfidenceRows</c>: at most that many rows hold a value below the confidence threshold). A reviewer can still
/// approve such a document; only auto-approval is held.
/// </summary>
public static class ReviewGateRules
{
    public const string RequiredRuleNotPassed = "REQUIRED_RULE_NOT_PASSED";
    public const string WarningBlocks = "RULE_WARNING_BLOCKS";
    public const string LinesSource = "LINES_SOURCE";
    public const string LinesLocated = "LINES_NOT_LOCATED";
    public const string LinesLowConfidence = "LINES_LOW_CONFIDENCE";

    /// <summary>The holds for one document; empty when the definition's conditions are met (or it sets none).</summary>
    public static IReadOnlyList<ReviewGateHold> Evaluate(ReviewPolicy review, ValidationResult? result, JsonObject? tree, string? lineItemsPath, double confidenceThreshold)
    {
        ArgumentNullException.ThrowIfNull(review);
        var holds = new List<ReviewGateHold>();
        foreach (var id in review.RequirePassedRules ?? [])
        {
            if (result is null || !result.Passed(id))
            {
                holds.Add(new ReviewGateHold(
                    RequiredRuleNotPassed,
                    result is null || !result.ByRule.ContainsKey(id) ? $"rule '{id}' did not run" : $"rule '{id}' did not pass"));
            }
        }

        foreach (var id in review.BlockOnWarnings ?? [])
        {
            if (result is not null && result.Warned(id))
            {
                holds.Add(new ReviewGateHold(WarningBlocks, $"rule '{id}' raised a warning"));
            }
        }

        if (review.Lines is { } lines && lineItemsPath is not null)
        {
            holds.AddRange(EvaluateLines(lines, tree, lineItemsPath, review.MinConfidence ?? confidenceThreshold));
        }

        return holds;
    }

    private static IEnumerable<ReviewGateHold> EvaluateLines(LinesPolicy lines, JsonObject? tree, string lineItemsPath, double threshold)
    {
        var rows = ValuePaths.Select(tree, lineItemsPath.EndsWith("[*]", StringComparison.Ordinal) ? lineItemsPath : lineItemsPath + "[*]")
            .Select(r => r.Value)
            .OfType<JsonObject>()
            .ToList();
        var allowed = lines.RequireSource is { Count: > 0 } methods ? methods.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var wrongSource = 0;
        var notLocated = 0;
        var lowRows = 0;
        foreach (var row in rows)
        {
            var low = false;
            foreach (var (_, status, confidence, value) in ProvenanceWalker.Envelopes(row))
            {
                if (value is null || status is Domain.Fields.FieldStatus.Missing or Domain.Fields.FieldStatus.Unreadable)
                {
                    continue;
                }

                var envelope = FindEnvelope(row, value);
                var source = envelope?["source"] as JsonObject;
                if (allowed is not null && (source?["method"] is not JsonValue method || method.GetValueKind() != JsonValueKind.String || !allowed.Contains(method.GetValue<string>())))
                {
                    wrongSource++;
                }

                if (lines.RequireLocated && (source?["page"] is null || source["bBox"] is null))
                {
                    notLocated++;
                }

                if (confidence is { } c && c < threshold)
                {
                    low = true;
                }
            }

            if (low)
            {
                lowRows++;
            }
        }

        if (wrongSource > 0)
        {
            yield return new ReviewGateHold(LinesSource, $"{wrongSource} row value(s) come from a source the type does not accept ({string.Join(", ", allowed!.Order(StringComparer.Ordinal))})");
        }

        if (notLocated > 0)
        {
            yield return new ReviewGateHold(LinesLocated, $"{notLocated} row value(s) were not located on the page");
        }

        if (lines.MaxLowConfidenceRows is { } max && lowRows > max)
        {
            yield return new ReviewGateHold(LinesLowConfidence, $"{lowRows} row(s) are below the confidence threshold (at most {max})");
        }
    }

    /// <summary>The envelope object whose <c>value</c> is <paramref name="value"/> (the walker hands out the value node).</summary>
    private static JsonObject? FindEnvelope(JsonNode root, JsonNode value) => value.Parent as JsonObject ?? (root as JsonObject);
}
