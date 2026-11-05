using System.Text.Json.Nodes;
using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Mapping.Identification;
using DocProc.Mapping.Text;

namespace DocProc.Mapping.Learning;

/// <summary>A proposed identifier (docs/11 §12.9 step 2): the identifier as JSON, its sentence, whether it holds on the document, and warnings for the card.</summary>
public sealed record IdentifierProposal(JsonObject Identifier, string Describe, bool Holds, IReadOnlyList<string> Warnings);

/// <summary>A proposed identification rule (docs/11 §12.6, "if they marked none, the learner proposes one"): the rule, its sentence and whether it holds.</summary>
public sealed record RuleProposal(JsonObject Rule, string Describe, bool Holds, IReadOnlyList<string> Notes);

/// <summary>
/// Turns a selection on the page into an identifier (<c>identifiers:propose</c>, docs/11 §12.9), and proposes an
/// identification rule for a reviewed document whose reviewer marked none (§12.6): the type keyword in the page-1 title
/// area plus the supplier's VAT id where it is printed. In layout scope a selection equal to the supplier's VAT id, tax
/// id, an IBAN or a scalar attribute becomes <c>supplierValue</c>; in tenant scope it stays text, with a warning
/// (§8.5: such a value rarely identifies a type for all suppliers). Pure and deterministic.
/// </summary>
public static class IdentifierProposer
{
    /// <summary>
    /// The identifier for the words selected on <paramref name="page"/>: those named by index, or those whose centre
    /// lies in <paramref name="box"/>. Words in the title band become <c>{text, page, region: "title"}</c>, others a
    /// <c>bBox</c> around the selection with the default tolerance. An identifier's words are on one line, so only the
    /// selection's first line is taken.
    /// </summary>
    public static IdentifierProposal? Propose(DocumentWords words, int page, BoundingBox? box, IReadOnlyList<int>? wordIndexes, bool tenantScope, SupplierValues? supplier)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (words.Page(page) is not { } wordPage)
        {
            return null;
        }

        var selected = wordIndexes is { Count: > 0 }
            ? wordPage.Words.Where(w => wordIndexes.Contains(w.Index)).ToList()
            : box is { } b ? [.. PageGeometry.Hit(wordPage.Words, b)] : [];
        if (selected.Count == 0)
        {
            return null;
        }

        var warnings = new List<string>();
        var lines = PageGeometry.Lines(selected);
        if (lines.Count > 1)
        {
            warnings.Add("The selection spans several lines; an identifier's words are on one line, so only the first line is used.");
        }

        var line = lines[0];
        var text = line.Text;
        var union = line.Box;
        var pageNode = words.LastPage > 1 && page == words.LastPage && page > 1 ? JsonValue.Create("last") : (JsonNode)JsonValue.Create(page);
        JsonObject identifier;
        var master = MasterDataKey(text, supplier);
        if (master is not null && !tenantScope)
        {
            identifier = new JsonObject { ["supplierValue"] = master, ["page"] = pageNode };
        }
        else
        {
            if (master is not null)
            {
                warnings.Add($"The selection is the supplier's {master}; a tenant rule runs for every supplier, so it is proposed as text, which rarely identifies a type for all suppliers.");
            }

            identifier = new JsonObject { ["text"] = text, ["page"] = pageNode };
            if (union.Y1 <= IdentificationRegions.TitleBottom)
            {
                identifier["region"] = IdentificationRegions.Title;
            }
            else
            {
                identifier["bBox"] = new JsonArray(Math.Floor(union.X0), Math.Floor(union.Y0), Math.Ceiling(union.X1), Math.Ceiling(union.Y1));
            }

            if (text.Any(char.IsDigit))
            {
                warnings.Add("The selection holds digits; a number that changes from document to document does not identify a design (turn it into a regex).");
            }
        }

        var rule = IdentificationRule.Parse(new JsonObject { ["all"] = new JsonArray(identifier.DeepClone()) });
        var holds = IdentificationRuleEvaluator.Evaluate(rule, new IdentificationContext(words, supplier ?? SupplierValues.None)).Holds;
        return new IdentifierProposal(identifier, rule.Describe(), holds, warnings);
    }

    /// <summary>
    /// A rule for a reviewed document (docs/11 §12.6): the line of the page-1 title area that holds one of the type's
    /// keywords (the run of its words without digits around the keyword; else the title area's first line without
    /// digits), plus <c>supplierValue: vatId</c> on the page it is printed on (<c>1</c>, <c>last</c> or any).
    /// </summary>
    public static RuleProposal? ProposeRule(DocumentWords words, IReadOnlyList<string> keywords, SupplierValues? supplier)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(keywords);
        var notes = new List<string>();
        var all = new JsonArray();
        if (TitleText(words, keywords) is { } title)
        {
            all.Add(new JsonObject { ["text"] = title, ["page"] = 1, ["region"] = IdentificationRegions.Title });
        }
        else
        {
            notes.Add("No title words were found on page 1; mark what identifies the design.");
        }

        if (supplier?.VatId is { Length: > 0 })
        {
            foreach (var page in new JsonNode[] { JsonValue.Create(1), JsonValue.Create("last"), JsonValue.Create("any") })
            {
                var candidate = new JsonObject { ["supplierValue"] = "vatId", ["page"] = page.DeepClone() };
                if (IdentificationRuleEvaluator.Evaluate(IdentificationRule.Parse(new JsonObject { ["all"] = new JsonArray(candidate.DeepClone()) }), new IdentificationContext(words, supplier)).Holds)
                {
                    if (page is JsonValue v && v.TryGetValue<string>(out var s) && s == "any")
                    {
                        candidate.Remove("page");
                    }

                    all.Add(candidate);
                    break;
                }
            }
        }

        if (all.Count == 0)
        {
            return null;
        }

        var rule = IdentificationRule.Parse(new JsonObject { ["all"] = all });
        var holds = IdentificationRuleEvaluator.Evaluate(rule, new IdentificationContext(words, supplier ?? SupplierValues.None)).Holds;
        return new RuleProposal(rule.ToJsonObject(), rule.Describe(), holds, notes);
    }

    /// <summary>The title words of page 1: the keyword's line (its run of words without digits) or the area's first line without digits.</summary>
    private static string? TitleText(DocumentWords words, IReadOnlyList<string> keywords)
    {
        if (words.Page(1) is not { } page)
        {
            return null;
        }

        var lines = PageGeometry.Lines(page.Words.Where(w => w.Box.CenterY <= IdentificationRegions.TitleBottom)).ToList();
        var tokens = keywords.Select(k => IdentificationRuleEvaluator.Token(k)).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);

        // The tallest line holding a keyword: a title is printed larger than the letterhead's address lines.
        foreach (var line in lines.OrderByDescending(l => l.Words.Max(w => w.Box.Height)).ThenBy(l => l.Box.Y0))
        {
            var index = line.Words.ToList().FindIndex(w => tokens.Contains(IdentificationRuleEvaluator.Token(w.Text)));
            if (index < 0)
            {
                continue;
            }

            var start = index;
            while (start > 0 && !line.Words[start - 1].Text.Any(char.IsDigit) && line.Words[index].Box.X0 - line.Words[start - 1].Box.X1 < 40)
            {
                start--;
            }

            var end = index;
            while (end < line.Words.Count - 1 && !line.Words[end + 1].Text.Any(char.IsDigit) && line.Words[end + 1].Box.X0 - line.Words[end].Box.X1 < 20)
            {
                end++;
            }

            return string.Join(' ', line.Words.Skip(start).Take(end - start + 1).Select(w => w.Text));
        }

        return lines.FirstOrDefault(l => !l.Text.Any(char.IsDigit) && l.Words.Any(w => w.Text.Count(char.IsLetter) >= 3))?.Text;
    }

    /// <summary>Which master-data value of the supplier the text is, compared compacted; null for none.</summary>
    private static string? MasterDataKey(string text, SupplierValues? supplier)
    {
        if (supplier is null || Identifiers.NormalizeCompact(text) is not { Length: > 0 } compact)
        {
            return null;
        }

        foreach (var key in SupplierValueKeys.Fixed.Concat((supplier.Attributes?.Keys ?? []).Order(StringComparer.Ordinal).Select(k => SupplierValueKeys.AttributesPrefix + k)))
        {
            if (supplier.ValuesOf(key).Any(v => string.Equals(Identifiers.NormalizeCompact(v), compact, StringComparison.Ordinal)))
            {
                return key;
            }
        }

        return null;
    }
}
