using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Domain.Text;
using DocProc.Mapping.Text;

namespace DocProc.Mapping.Identification;

/// <summary>The supplier's master data a <c>supplierValue</c> identifier reads (docs/11 §8.5).</summary>
public sealed record SupplierValues(string? VatId = null, string? TaxId = null, IReadOnlyList<string>? Ibans = null, IReadOnlyDictionary<string, string>? Attributes = null)
{
    public static SupplierValues None { get; } = new();

    /// <summary>The values a key names: <c>vatId</c>, <c>taxId</c>, <c>iban</c> (every IBAN of the supplier), <c>attributes.&lt;key&gt;</c>.</summary>
    public IReadOnlyList<string> ValuesOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        string?[] values = key switch
        {
            "vatId" => [VatId],
            "taxId" => [TaxId],
            "iban" => [.. Ibans ?? []],
            _ when key.StartsWith(SupplierValueKeys.AttributesPrefix, StringComparison.Ordinal)
                   && Attributes is { } attributes
                   && attributes.TryGetValue(key[SupplierValueKeys.AttributesPrefix.Length..], out var attribute) => [attribute],
            _ => [],
        };
        return [.. values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!)];
    }
}

/// <summary>The keys a <c>supplierValue</c> identifier may name.</summary>
public static class SupplierValueKeys
{
    public const string AttributesPrefix = "attributes.";

    public static readonly IReadOnlyList<string> Fixed = ["vatId", "taxId", "iban"];

    public static bool IsValid(string key) =>
        Fixed.Contains(key, StringComparer.Ordinal)
        || (key.StartsWith(AttributesPrefix, StringComparison.Ordinal) && key.Length > AttributesPrefix.Length && key.Length <= 80);

    /// <summary>A key in words, as the rule in words and the evidence name it: <c>VAT id</c>, <c>tax id</c>, <c>IBAN</c>, <c>attribute customerNo</c>.</summary>
    public static string Label(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key switch
        {
            "vatId" => "VAT id",
            "taxId" => "tax id",
            "iban" => "IBAN",
            _ when key.StartsWith(AttributesPrefix, StringComparison.Ordinal) && key.Length > AttributesPrefix.Length => $"attribute {key[AttributesPrefix.Length..]}",
            _ => key,
        };
    }
}

/// <summary>The source facts a <c>source</c> identifier reads (docs/11 §8.5): the item kind and the ingestion's file name, subject, mailbox and folder path.</summary>
public sealed record SourceFacts(string? ItemKind = null, string? FileName = null, string? Subject = null, string? Mailbox = null, string? FolderPath = null)
{
    public static SourceFacts None { get; } = new();

    public static readonly IReadOnlyList<string> Keys = ["itemKind", "fileName", "subject", "mailbox", "folderPath"];

    public string? ValueOf(string key) => key switch
    {
        "itemKind" => ItemKind,
        "fileName" => FileName,
        "subject" => Subject,
        "mailbox" => Mailbox,
        "folderPath" => FolderPath,
        _ => null,
    };
}

/// <summary>
/// The words of a document, normalised to the 1000 × 1000 page space and indexed once per document (docs/11 §8.5
/// "an index of normalised words per page, built once per document"), with whether the text layer came from OCR, which
/// is the only case <c>allowOcrErrors</c> relaxes.
/// </summary>
public sealed class DocumentWords
{
    private readonly Dictionary<int, WordPage> _pages;

    public DocumentWords(IReadOnlyList<WordPage> pages, bool fromOcr)
    {
        ArgumentNullException.ThrowIfNull(pages);
        _pages = pages.GroupBy(p => p.Number).ToDictionary(g => g.Key, g => g.First());
        Pages = [.. _pages.Values.OrderBy(p => p.Number)];
        FromOcr = fromOcr;
        LastPage = Pages.Count == 0 ? 0 : Pages[^1].Number;
    }

    public static DocumentWords Empty { get; } = new([], false);

    public static DocumentWords From(TextLayer? layer) =>
        layer is null ? Empty : new DocumentWords([.. layer.Pages.Select(WordPage.From)], layer.Source == TextLayerSource.Ocr);

    public IReadOnlyList<WordPage> Pages { get; }

    public bool FromOcr { get; }

    public int LastPage { get; }

    public WordPage? Page(int number) => _pages.GetValueOrDefault(number);
}

/// <summary>What one evaluation is run against.</summary>
public sealed record IdentificationContext(DocumentWords Words, SupplierValues? Supplier = null, SourceFacts? Source = null);

/// <summary>Why an identifier held: which one, what it is, where it was found and the words matched.</summary>
public sealed record IdentifierEvidence(string Path, string Kind, string Description, bool Positive, int? Page = null, BoundingBox? Box = null, string? Text = null);

/// <summary>
/// The strict result of an identification rule (docs/11 §8.5): true or false, the evidence of every identifier that
/// held, and the identifiers that decided against it (a positive one that did not hold, a <c>none</c> one that did).
/// </summary>
public sealed record IdentificationResult(bool Holds, IReadOnlyList<IdentifierEvidence> Evidence, IReadOnlyList<string> Failed)
{
    public static IdentificationResult False(string reason) => new(false, [], [reason]);
}

/// <summary>
/// Evaluates an <see cref="IdentificationRule"/> over a document (docs/11 §8.5 "Strict semantics"): every identifier is
/// evaluated, every group is decided exactly (<c>all</c> fully, at least one of <c>any</c>, nothing of <c>none</c>,
/// an implicit AND of the ones present), and nothing is scored. <c>allowOcrErrors</c> is the one relaxation: on an OCR
/// text layer only, a word of six or more letters may differ by one character.
/// </summary>
public static class IdentificationRuleEvaluator
{
    /// <summary>The time a pattern may take on one text before it counts as not holding.</summary>
    public static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>The fewest letters a word must have for <c>allowOcrErrors</c> to relax it.</summary>
    public const int OcrToleranceMinLetters = 6;

    private const int MaxCachedPatterns = 512;
    private static readonly ConcurrentDictionary<(string Pattern, bool IgnoreCase), Regex?> Patterns = new();

    public static IdentificationResult Evaluate(IdentificationRule rule, IdentificationContext context)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(context);
        var evidence = new List<IdentifierEvidence>();
        var failed = new List<string>();
        var holds = EvaluateGroup(rule.Root, string.Empty, IdentifierLocation.None, positive: true, context, evidence, failed);
        return new IdentificationResult(holds, evidence, failed);
    }

    /// <summary>
    /// A pattern as identification runs it (NonBacktracking, culture-invariant, <see cref="PatternTimeout"/>); null when
    /// it does not compile.
    /// </summary>
    public static Regex? Compile(string pattern, bool ignoreCase = false)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (Patterns.TryGetValue((pattern, ignoreCase), out var cached))
        {
            return cached;
        }

        Regex? regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), PatternTimeout);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            regex = null;
        }

        if (Patterns.Count >= MaxCachedPatterns)
        {
            Patterns.Clear();
        }

        Patterns[(pattern, ignoreCase)] = regex;
        return regex;
    }

    private static bool EvaluateGroup(IdentifierGroup group, string prefix, IdentifierLocation inherited, bool positive, IdentificationContext context, List<IdentifierEvidence> evidence, List<string> failed)
    {
        var location = group.Location.Over(inherited);
        var holds = true;

        // Every member is evaluated, also after the result is decided: the evidence and the failures are complete.
        if (group.All is { } all)
        {
            var results = Members(all, prefix + "all", location, positive, context, evidence, failed);
            holds &= results.All(r => r);
        }

        if (group.Any is { } any)
        {
            var results = Members(any, prefix + "any", location, positive, context, evidence, failed);
            holds &= results.Any(r => r);
        }

        if (group.None is { } none)
        {
            var results = Members(none, prefix + "none", location, positive: false, context, evidence, failed);
            holds &= !results.Any(r => r);
        }

        return holds;
    }

    private static List<bool> Members(IReadOnlyList<IdentificationNode> members, string prefix, IdentifierLocation location, bool positive, IdentificationContext context, List<IdentifierEvidence> evidence, List<string> failed)
    {
        var results = new List<bool>(members.Count);
        for (var i = 0; i < members.Count; i++)
        {
            var path = $"{prefix}[{i.ToString(CultureInfo.InvariantCulture)}]";
            bool held;
            if (members[i] is IdentifierGroup nested)
            {
                held = EvaluateGroup(nested, path + ".", location, positive, context, evidence, failed);
            }
            else
            {
                var identifier = (Identifier)members[i];
                var effective = identifier.Location.Over(location);
                var hit = Find(identifier, effective, context);
                held = hit is not null;
                var description = identifier.Describe(effective);
                if (hit is { } h)
                {
                    evidence.Add(new IdentifierEvidence(path, KindName(identifier.Kind), description, positive, h.Page, h.Box, h.Text));
                    if (!positive)
                    {
                        failed.Add($"{path}: {description} is present");
                    }
                }
                else if (positive)
                {
                    failed.Add($"{path}: {description} is not present");
                }
            }

            results.Add(held);
        }

        return results;
    }

    public static string KindName(IdentifierKind kind) => kind switch
    {
        IdentifierKind.Text => "text",
        IdentifierKind.Regex => "regex",
        IdentifierKind.SupplierValue => "supplierValue",
        IdentifierKind.Source => "source",
        IdentifierKind.XmlPath => "xmlPath",
        _ => "cell",
    };

    private readonly record struct Hit(int? Page, BoundingBox? Box, string? Text);

    private static Hit? Find(Identifier identifier, IdentifierLocation location, IdentificationContext context)
    {
        switch (identifier.Kind)
        {
            case IdentifierKind.Source:
                return FindSource(identifier, context.Source ?? SourceFacts.None);
            case IdentifierKind.XmlPath or IdentifierKind.Cell:
                // Structured sources arrive with the tree and tabular layers (P6); until then such an identifier never holds.
                return null;
        }

        var page = location.Page ?? PageSelector.Any;
        var area = location.Area();
        foreach (var wordPage in context.Words.Pages)
        {
            if (!page.Selects(wordPage.Number, context.Words.LastPage))
            {
                continue;
            }

            var words = wordPage.Words.Where(w => area.Contains(w.Box.CenterX, w.Box.CenterY)).ToList();
            if (words.Count == 0)
            {
                continue;
            }

            var hit = identifier.Kind switch
            {
                IdentifierKind.Text => FindText(identifier, words, context.Words.FromOcr),
                IdentifierKind.Regex => FindPattern(identifier.Value, words),
                _ => FindSupplierValue(identifier.Value, words, context.Supplier ?? SupplierValues.None),
            };
            if (hit is { } found)
            {
                return new Hit(wordPage.Number, PageGeometry.Union(found), string.Join(' ', found.Select(w => w.Text)));
            }
        }

        return null;
    }

    private static Hit? FindSource(Identifier identifier, SourceFacts source)
    {
        var value = source.ValueOf(identifier.Value);
        if (value is null)
        {
            return null;
        }

        if (identifier.EqualsValue is { } equals)
        {
            return string.Equals(value, equals, StringComparison.OrdinalIgnoreCase) ? new Hit(null, null, value) : null;
        }

        var regex = Compile(identifier.Pattern!, ignoreCase: true);
        try
        {
            return regex is not null && regex.IsMatch(value) ? new Hit(null, null, value) : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// The phrase as consecutive words of one line in reading order, whole words, case-insensitive unless asked; a word
    /// is compared without the punctuation around it (<c>Rechnung:</c> is the word <c>Rechnung</c>).
    /// </summary>
    private static List<IndexedWord>? FindText(Identifier identifier, List<IndexedWord> words, bool fromOcr)
    {
        var tokens = identifier.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => Token(t, identifier.CaseSensitive)).ToArray();
        if (tokens.Length == 0)
        {
            return null;
        }

        var tolerant = identifier.AllowOcrErrors && fromOcr;
        foreach (var line in PageGeometry.Lines(words))
        {
            var lineTokens = line.Words.Select(w => Token(w.Text, identifier.CaseSensitive)).ToArray();
            for (var start = 0; start + tokens.Length <= lineTokens.Length; start++)
            {
                var all = true;
                for (var k = 0; k < tokens.Length && all; k++)
                {
                    all = Same(tokens[k], lineTokens[start + k], tolerant);
                }

                if (all)
                {
                    return [.. line.Words.Skip(start).Take(tokens.Length)];
                }
            }
        }

        return null;
    }

    /// <summary>A word as identification compares it: without the punctuation around it (unless it is only punctuation), upper-cased unless case-sensitive.</summary>
    public static string Token(string word, bool caseSensitive = false)
    {
        ArgumentNullException.ThrowIfNull(word);
        var start = 0;
        var end = word.Length;
        while (start < end && !char.IsLetterOrDigit(word[start]))
        {
            start++;
        }

        while (end > start && !char.IsLetterOrDigit(word[end - 1]))
        {
            end--;
        }

        var core = start < end ? word[start..end] : word;
        return caseSensitive ? core : core.ToUpperInvariant();
    }

    private static bool Same(string expected, string actual, bool tolerant)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return true;
        }

        return tolerant && expected.Count(char.IsLetter) >= OcrToleranceMinLetters && WithinOneEdit(expected, actual);
    }

    /// <summary>Whether <paramref name="a"/> becomes <paramref name="b"/> by at most one substitution, insertion or deletion.</summary>
    public static bool WithinOneEdit(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (Math.Abs(a.Length - b.Length) > 1)
        {
            return false;
        }

        var i = 0;
        var j = 0;
        var edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j])
            {
                i++;
                j++;
                continue;
            }

            if (++edits > 1)
            {
                return false;
            }

            if (a.Length > b.Length)
            {
                i++;
            }
            else if (b.Length > a.Length)
            {
                j++;
            }
            else
            {
                i++;
                j++;
            }
        }

        return edits + (a.Length - i) + (b.Length - j) <= 1;
    }

    /// <summary>The pattern over the joined text of the words (reading order); the words the match covers.</summary>
    private static List<IndexedWord>? FindPattern(string pattern, List<IndexedWord> words)
    {
        var regex = Compile(pattern);
        if (regex is null)
        {
            return null;
        }

        var (text, spans) = Joined(words);
        Match match;
        try
        {
            match = regex.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        if (!match.Success)
        {
            return null;
        }

        var covered = spans.Where(s => s.Start < match.Index + Math.Max(match.Length, 1) && s.Start + s.Word.Text.Length > match.Index).Select(s => s.Word).ToList();
        return covered.Count > 0 ? covered : [spans[0].Word];
    }

    private static (string Text, List<(int Start, IndexedWord Word)> Spans) Joined(List<IndexedWord> words)
    {
        var builder = new StringBuilder();
        var spans = new List<(int, IndexedWord)>();
        foreach (var line in PageGeometry.Lines(words))
        {
            foreach (var word in line.Words)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                spans.Add((builder.Length, word));
                builder.Append(word.Text);
            }
        }

        return (builder.ToString(), spans);
    }

    /// <summary>
    /// A master-data value printed on one line, compared compacted (<see cref="Identifiers.NormalizeCompact"/>): the
    /// consecutive words whose compacted text holds the value as a whole, so <c>DE 811 111 111</c> and
    /// <c>USt-IdNr.: DE811111111</c> both hold for the VAT id <c>DE811111111</c>. "As a whole" (docs/11 §8.5, nothing
    /// nearly matched): the value begins and ends at a word's edge, at a character that is neither a letter nor a digit
    /// (the <c>:</c> of a label glued to it), or where letters turn into digits or back; so the longer VAT id
    /// <c>DE8111111119</c>, or a customer number <c>12345</c> inside the phone number <c>0401234567890</c>, does not hold.
    /// </summary>
    private static List<IndexedWord>? FindSupplierValue(string key, List<IndexedWord> words, SupplierValues supplier)
    {
        var values = supplier.ValuesOf(key).Select(Identifiers.NormalizeCompact).Where(v => v is { Length: > 0 }).Select(v => v!).ToList();
        if (values.Count == 0)
        {
            return null;
        }

        foreach (var line in PageGeometry.Lines(words))
        {
            // The line's compacted text, and for each of its characters the word it came from.
            var text = new StringBuilder();
            var owner = new List<int>();
            var wordStarts = new HashSet<int>();
            var wordEnds = new HashSet<int>();
            for (var w = 0; w < line.Words.Count; w++)
            {
                var compact = Identifiers.NormalizeCompact(line.Words[w].Text) ?? string.Empty;
                if (compact.Length == 0)
                {
                    continue;
                }

                wordStarts.Add(text.Length);
                text.Append(compact);
                owner.AddRange(Enumerable.Repeat(w, compact.Length));
                wordEnds.Add(text.Length);
            }

            var joined = text.ToString();
            foreach (var value in values)
            {
                for (var at = joined.IndexOf(value, StringComparison.Ordinal); at >= 0; at = joined.IndexOf(value, at + 1, StringComparison.Ordinal))
                {
                    var after = at + value.Length;
                    if (!Edge(joined, at, wordStarts, before: true) || !Edge(joined, after, wordEnds, before: false))
                    {
                        continue;
                    }

                    var first = owner[at];
                    var last = owner[after - 1];
                    return [.. line.Words.Skip(first).Take(last - first + 1)];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a value may begin (<paramref name="before"/>) or end at <paramref name="index"/> of the compacted line:
    /// at a word's edge, next to a character that is neither a letter nor a digit, or where letters and digits meet.
    /// </summary>
    private static bool Edge(string text, int index, HashSet<int> wordEdges, bool before)
    {
        if (index <= 0 || index >= text.Length || wordEdges.Contains(index))
        {
            return true;
        }

        var outside = before ? text[index - 1] : text[index];
        var inside = before ? text[index] : text[index - 1];
        if (!char.IsLetterOrDigit(outside) || !char.IsLetterOrDigit(inside))
        {
            return true;
        }

        return char.IsDigit(outside) != char.IsDigit(inside);
    }
}
