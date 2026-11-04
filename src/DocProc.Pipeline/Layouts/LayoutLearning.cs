using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Documents;
using DocProc.Application.Suppliers;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Mapping.Identification;
using DocProc.Mapping.Layouts;
using DocProc.Mapping.Learning;
using DocProc.Mapping.Values;
using DocProc.Pipeline.Review;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Pipeline.Layouts;

/// <summary>A layout drafted from a reviewed document (docs/11 §12.6): its field mapping, rule, the learner's report, the similarity profile and the rule's evidence on the document.</summary>
public sealed record LayoutDraft(
    JsonObject Mapping,
    LayoutMapping Parsed,
    IdentificationRule Rule,
    bool RuleProposed,
    IReadOnlyList<LearnedField> Fields,
    IReadOnlyList<LearnSkip> Skipped,
    SimilarityProfile Profile,
    IdentificationResult Evidence,
    IReadOnlyList<string> Notes);

/// <summary>Per field of the replay (docs/11 §8.3): hits, mismatches and misses on the replay set, and the agreement.</summary>
public sealed record ReplayFieldStat(string Path, int Hits, int Mismatches, int Missing, double Agreement);

/// <summary>One document of the replay set: whether the rule holds, and per covered field <c>hit</c>, <c>mismatch</c>, <c>missing</c> or <c>skipped</c>.</summary>
public sealed record ReplayDocument(Guid DocumentId, string? FileName, string Status, bool Holds, IReadOnlyDictionary<string, string> Fields);

/// <summary>A document of another layout (or a pending Candidate) the rule under test holds on: an overlap that blocks activation.</summary>
public sealed record ReplayOverlap(Guid DocumentId, Guid? LayoutId, string? LayoutKey, string Reason);

/// <summary>
/// The dry-run replay of a layout version (docs/11 §8.3, stored in <c>ReplayJson</c>): the identification test (the rule
/// holds on the documents it was built from, and on no approved document of the owner's other layouts nor a pending
/// document of another Candidate), and the field replay over the replay set (the approved documents of the supplier and
/// type, golden documents and the version's own documents on which the rule holds), with the per-field matrix and the
/// activation condition: identification passes and every field agrees on at least <see cref="Threshold"/> of a
/// non-empty set.
/// </summary>
public sealed record ReplayReport(
    bool IdentificationPassed,
    IReadOnlyList<Guid> SourceNotMatched,
    IReadOnlyList<ReplayOverlap> Overlaps,
    IReadOnlyList<ReplayDocument> Documents,
    IReadOnlyList<ReplayFieldStat> Fields,
    double Threshold,
    bool FieldsPassed,
    DateTimeOffset At)
{
    public bool Passed => IdentificationPassed && FieldsPassed;

    public string ToJson() => JsonSerializer.Serialize(this, PipelineJson.Options);
}

/// <summary>Why an activation was refused, or the version it activated with the documents waiting for it.</summary>
public sealed record ActivationOutcome(bool Activated, ReplayReport Replay, IReadOnlyList<Guid> Waiting, string? Refusal);

/// <summary>
/// Learning layouts from review and their lifecycle (docs/11 §8.3, §12.6, §12.10): drafts a layout from a reviewed
/// document with <see cref="AnchorLearner"/> (a proposed rule when the reviewer marked none), replays a version over its
/// replay set, activates it under the one activation condition of §8.3 with the tenant's warm-up, finds the documents a
/// rule holds on (the sibling re-test), attaches pending <c>NEW_LAYOUT</c> documents to a Candidate, and notifies the
/// tenant's <c>MappingAuthor</c>s of Candidates with documents waiting. Used by the API's review and layout endpoints.
/// </summary>
public sealed class LayoutLearning(DocProcDbContext db, IContainerBlobStore blobs, IEffectiveConfigResolver configs, TimeProvider clock)
{
    /// <summary>The approved documents of the supplier and type a replay takes by default.</summary>
    public const int DefaultReplayLastN = 50;

    /// <summary>The documents a sibling re-test or a "no longer recognised" check looks at, at most.</summary>
    public const int MaxMatching = 200;

    public const string NotificationAudience = "MappingAuthor";
    public const string CandidateWaiting = "layout.candidate_waiting";

    /// <summary>The words of a document's stored text layer (none when it has none).</summary>
    public async Task<DocumentWords> WordsAsync(Document document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        try
        {
            return DocumentWords.From(await PageSources.ReadTextLayerAsync(blobs, document, cancellationToken).ConfigureAwait(false));
        }
        catch (BlobNotFoundException)
        {
            return DocumentWords.Empty;
        }
    }

    /// <summary>The type version a document's tree is read and written against (its pinned one, else the published one).</summary>
    public async Task<EffectiveType?> TypeOfAsync(Document document, string? typeKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var key = typeKey is null ? document.Type : DocumentTypeKey.Parse(typeKey);
        var pinned = typeKey is null || key == document.Type ? document.DocumentTypeVersionId : null;
        var config = await configs.ResolveAsync(new EffectiveConfigRequest(document.TenantId, key, document.SupplierId, pinned), cancellationToken).ConfigureAwait(false);
        return config.Type;
    }

    /// <summary>
    /// Drafts a layout from the reviewed document (docs/11 §12.6): its confirmed values with their places and its tables
    /// (the review tool's specification where one wrote the rows), the labels the supplier's other layouts use for the
    /// same fields preferred, the rule the reviewer marked or else a proposed one. Null with a reason when the document
    /// cannot teach a layout.
    /// </summary>
    public async Task<(LayoutDraft? Draft, string? Problem)> DraftAsync(Document document, LayoutSetting setting, string? identificationJson, IReadOnlyCollection<string>? onlyFields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(setting);
        var type = await TypeOfAsync(document, null, cancellationToken).ConfigureAwait(false);
        if (type?.Compiled is not { } compiled)
        {
            return (null, $"The type {document.Type} has no compiled definition to learn a layout against.");
        }

        if (document.CanonicalJson is null || JsonNode.Parse(document.CanonicalJson) is not JsonObject tree || !DefinitionTree.HoldsValue(tree, ["kind"]))
        {
            return (null, "The document holds no values to learn from.");
        }

        var words = await WordsAsync(document, cancellationToken).ConfigureAwait(false);
        if (words.Pages.Count == 0)
        {
            return (null, "The document has no text layer; a layout is learned from the words of the page.");
        }

        var locale = LocaleOf(setting.Supplier, null);
        var synonyms = await SynonymsAsync(setting, document.Type.Value, cancellationToken).ConfigureAwait(false);
        var specs = await TableSpecificationsAsync(document.Id, cancellationToken).ConfigureAwait(false);
        var (samples, tables) = Samples(tree, compiled.Descriptors, words, specs, onlyFields);
        var learned = AnchorLearner.Learn(new LearnRequest(words, samples, tables, locale, synonyms));
        var notes = new List<string>();

        IdentificationRule? rule = null;
        var proposed = false;
        if (identificationJson is not null)
        {
            rule = IdentificationRule.TryParse(identificationJson, out var error) ?? throw new IdentificationRuleException(error ?? "The rule does not read.");
        }
        else if (IdentifierProposer.ProposeRule(words, KeywordsOf(type), setting.Values) is { } proposal)
        {
            rule = IdentificationRule.Parse(proposal.Rule);
            proposed = true;
            notes.AddRange(proposal.Notes);
            notes.Add($"The identification rule was proposed: {proposal.Describe}. Confirm it, or mark what identifies the design.");
        }

        if (rule is null)
        {
            return (null, "No identification rule was marked and none could be proposed (no title words, no VAT id on the page); mark what identifies the design.");
        }

        var evidence = IdentificationRuleEvaluator.Evaluate(rule, new IdentificationContext(words, setting.Values, LayoutSettings.SourceOf(document)));
        var parsed = LayoutMapping.Parse(learned.Mapping.ToJsonString());
        return (new LayoutDraft(learned.Mapping, parsed, rule, proposed, learned.Fields, learned.Skipped, SimilarityProfile.Of(words, LayoutSettings.ItemKindOf(document)), evidence, notes), null);
    }

    /// <summary>The type's title keywords (classification keywords and names of every language) for the proposed rule.</summary>
    private static List<string> KeywordsOf(EffectiveType type)
    {
        var keywords = new List<string>();
        foreach (var list in type.Definition.Classification?.Keywords?.Values ?? [])
        {
            keywords.AddRange(list);
        }

        keywords.AddRange(type.Definition.Name.Values);
        keywords.Add(type.TypeKey);
        return [.. keywords.Where(k => !string.IsNullOrWhiteSpace(k)).SelectMany(k => k.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The labels the supplier's other layouts of the type use per path: the learner prefers them (mapping synonyms, §12.6).</summary>
    private async Task<Dictionary<string, IReadOnlyList<string>>> SynonymsAsync(LayoutSetting setting, string typeKey, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var ids = setting.Layouts.Where(l => l.DocumentTypeKey == typeKey).SelectMany(l => l.Versions.Where(v => v.Version == l.ActiveVersion)).Select(v => v.Id).ToList();
        var mappings = await db.SupplierLayoutVersions.AsNoTracking().Where(v => ids.Contains(v.Id)).Select(v => v.MappingJson).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var mapping in mappings.Select(m => LayoutMapping.TryParse(m, out _)).OfType<LayoutMapping>())
        {
            foreach (var field in mapping.Fields)
            {
                var labels = field.Rules.SelectMany(r => r.Labels ?? []).ToList();
                if (labels.Count > 0)
                {
                    result[field.Path] = [.. (result.GetValueOrDefault(field.Path) ?? []).Concat(labels).Distinct(StringComparer.Ordinal)];
                }
            }
        }

        return result;
    }

    /// <summary>The review table tool's specifications of the document's groups (<c>Correction.SourceJson.table.spec</c>), the latest per path.</summary>
    private async Task<Dictionary<string, JsonObject>> TableSpecificationsAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var rows = await db.Corrections.AsNoTracking()
            // SourceJson is a json column: SQL Server refuses LIKE on it, so the rows are filtered here, not in the query.
            .Where(c => c.DocumentId == documentId && c.Op == "set" && c.SourceJson != null)
            .OrderBy(c => c.Id)
            .Select(c => new { c.FieldPath, c.SourceJson })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var specs = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            try
            {
                if (JsonNode.Parse(row.SourceJson!)?["table"]?["spec"] is JsonObject spec)
                {
                    specs[row.FieldPath] = (JsonObject)spec.DeepClone();
                }
            }
            catch (JsonException)
            {
                // A source that does not read teaches nothing.
            }
        }

        return specs;
    }

    /// <summary>
    /// The confirmed values of a tree as the learner takes them: every field with a value (its place from its source, or
    /// found on the page by its text, as a composite's members are), every repeating group with its rows' places.
    /// </summary>
    public static (List<LearnSample> Samples, List<LearnTable> Tables) Samples(JsonObject tree, DescriptorIndex descriptors, DocumentWords words, IReadOnlyDictionary<string, JsonObject>? specs = null, IReadOnlyCollection<string>? onlyFields = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(words);
        var samples = new List<LearnSample>();
        var tables = new List<LearnTable>();
        bool Wanted(string path) => onlyFields is not { Count: > 0 } || onlyFields.Contains(path) || onlyFields.Any(f => path.StartsWith(f + ".", StringComparison.Ordinal));
        foreach (var root in descriptors.Roots)
        {
            if (root.Key == "kind" || !Wanted(root.Path))
            {
                continue;
            }

            var node = tree[root.Key];
            if (root.Kind == FieldKinds.Array)
            {
                if (LayoutTreeWriter.RowFieldsOf(descriptors, root.Path) is { } rowFields && node is JsonArray rows && rows.Count > 0)
                {
                    var learnRows = rows.OfType<JsonObject>().Select(row => (IReadOnlyList<LearnTableCell>)[.. rowFields.Keys
                        .Select(field => row[field] is { } cell && DefinitionTree.IsEnvelope(cell) && DefinitionTree.ValueOf(cell) is not null && SourceOf(cell) is { Page: { } page, BBox: { } box } source
                            ? new LearnTableCell(field, page, box, source.RawText)
                            : null)
                        .OfType<LearnTableCell>()]).Where(r => r.Count > 0).ToList();
                    if (learnRows.Count > 0 || specs?.ContainsKey(root.Path) == true)
                    {
                        tables.Add(new LearnTable(root.Path, learnRows, rowFields, null, specs?.GetValueOrDefault(root.Path)));
                    }
                }

                continue;
            }

            if (root.IsList && !root.Provenance && FieldKinds.IsComposite(root.Kind) && node is JsonArray { Count: > 0 } bare && bare[0] is JsonObject first)
            {
                // A list of bare composites (the invoice record's tax rows): the members of its first row, found by their text or by an equal learned value.
                foreach (var member in root.Members)
                {
                    var path = $"{root.Key}.{member.Key}";
                    if (!Wanted(path) || LayoutTreeWriter.KindOf(descriptors, path) is not { } memberKind || first[member.Key] is not { } memberValue)
                    {
                        continue;
                    }

                    var place = Text(memberValue) is { Length: > 0 } text ? LayoutPrefill.Find(words, text) : null;
                    samples.Add(new LearnSample(path, memberKind.Kind, place?.Text, memberValue, place?.Page, place?.Box, memberKind.Options, AllowConstant: memberKind.Kind is FieldKinds.Percent or FieldKinds.Decimal));
                }

                continue;
            }

            if (FieldKinds.IsComposite(root.Kind) && root.Members.Count > 0 && node is not null && DefinitionTree.IsEnvelope(node) && DefinitionTree.ValueOf(node) is JsonObject composite)
            {
                foreach (var member in root.Members)
                {
                    var path = $"{root.Key}.{member.Key}";
                    if (!Wanted(path) || LayoutTreeWriter.KindOf(descriptors, path) is not { } memberKind || composite[member.Key] is not JsonValue memberValue || Text(memberValue) is not { Length: > 0 } text)
                    {
                        continue;
                    }

                    var place = LayoutPrefill.Find(words, text);
                    samples.Add(new LearnSample(path, memberKind.Kind, place?.Text ?? text, memberValue, place?.Page, place?.Box, memberKind.Options, AllowConstant: member.Key == "name"));
                }

                continue;
            }

            if (LayoutTreeWriter.KindOf(descriptors, root.Path) is not { } kind || node is null || !DefinitionTree.IsEnvelope(node) || DefinitionTree.ValueOf(node) is not { } value)
            {
                continue;
            }

            var src = SourceOf(node);
            int? pageOf = src?.Page;
            BoundingBox? boxOf = src?.BBox;
            var printed = src?.RawText;
            if (pageOf is { } srcPage && boxOf is { } srcBox && !PrintedThere(words, srcPage, srcBox, printed ?? Text(value)))
            {
                // A place that does not hold the value's text (an estimate) is no place: the text is looked for instead.
                (pageOf, boxOf) = (null, null);
            }

            if (pageOf is null || boxOf is null)
            {
                // A value without a place (an LLM's, or one typed in) is looked for by its printed text, then by its value.
                foreach (var search in new[] { printed, Text(value) }.OfType<string>().Where(t => t.Length > 0).Distinct(StringComparer.Ordinal))
                {
                    if (LayoutPrefill.Find(words, search) is { } found)
                    {
                        (pageOf, boxOf, printed) = (found.Page, found.Box, found.Text);
                        break;
                    }
                }
            }

            samples.Add(new LearnSample(root.Path, kind.Kind, printed, value, pageOf, boxOf, kind.Options, AllowConstant: kind.Kind is FieldKinds.Currency or FieldKinds.Enum or FieldKinds.Boolean));
        }

        return (samples, tables);
    }

    /// <summary>Whether the words at a stored place spell the value's text (compared without spacing and case); true when there is no text to compare.</summary>
    private static bool PrintedThere(DocumentWords words, int page, BoundingBox box, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var hit = words.Page(page) is { } wordPage ? Mapping.Text.PageGeometry.Hit(wordPage.Words, new BoundingBox(box.X0 - 2, box.Y0 - 2, box.X1 + 2, box.Y1 + 2)) : [];
        static string Compact(string s) => string.Concat(s.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        return hit.Count > 0 && Compact(string.Concat(hit.Select(w => w.Text))).Contains(Compact(text), StringComparison.Ordinal);
    }

    private static string? Text(JsonNode value) => value switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonObject o when o["verbatim"] is JsonValue verbatim && verbatim.TryGetValue<string>(out var vs) => vs,
        _ => null,
    };

    /// <summary>A stored envelope's source: method, page, box (<c>{x0, y0, x1, y1}</c>) and printed text.</summary>
    public static FieldSource? SourceOf(JsonNode? envelope)
    {
        if (envelope?["source"] is not JsonObject source)
        {
            return null;
        }

        var method = source["method"] is JsonValue m && m.TryGetValue<string>(out var name) && Enum.TryParse<SourceMethod>(name, out var parsed) ? parsed : SourceMethod.Human;
        int? page = source["page"] is JsonValue p && p.TryGetValue<int>(out var n) ? n : null;
        BoundingBox? box = source["bBox"] switch
        {
            JsonObject b when Num(b["x0"]) is { } x0 && Num(b["y0"]) is { } y0 && Num(b["x1"]) is { } x1 && Num(b["y1"]) is { } y1 => new BoundingBox(x0, y0, x1, y1),
            JsonArray { Count: 4 } a when a.All(x => Num(x) is not null) => new BoundingBox(Num(a[0])!.Value, Num(a[1])!.Value, Num(a[2])!.Value, Num(a[3])!.Value),
            _ => null,
        };
        var raw = source["rawText"] is JsonValue r && r.TryGetValue<string>(out var text) ? text : null;
        return new FieldSource(method, page, box, raw);

        static double? Num(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    }

    /// <summary>The value of a path in a stored tree, as replay compares it: through envelopes, a composite's members and a list's first row.</summary>
    public static JsonNode? ValueAt(JsonObject tree, string path)
    {
        ArgumentNullException.ThrowIfNull(tree);
        JsonNode? node = tree;
        foreach (var part in path.Split('.'))
        {
            if (node is JsonArray list)
            {
                node = list.Count > 0 ? list[0] : null;
            }

            if (node is not null && DefinitionTree.IsEnvelope(node))
            {
                node = DefinitionTree.ValueOf(node);
            }

            node = (node as JsonObject)?[part];
        }

        if (node is JsonArray l2 && path.Contains('.', StringComparison.Ordinal))
        {
            node = l2.Count > 0 ? l2[0] : null;
        }

        return node is not null && DefinitionTree.IsEnvelope(node) ? DefinitionTree.ValueOf(node) : node;
    }

    /// <summary>The locale values are read in: the mapping's, the supplier's, else German (the demo tenants' country).</summary>
    private static ValueLocale LocaleOf(Supplier supplier, LayoutMapping? mapping)
    {
        if (mapping?.Culture is { } culture && ValueLocale.TryParse(culture, out var own))
        {
            return own;
        }

        return SupplierSettings.Parse(supplier.SettingsJson).Locale?.Culture is { } c && ValueLocale.TryParse(c, out var supplierLocale) ? supplierLocale : ValueLocale.German;
    }

    /// <summary>
    /// The replay of docs/11 §8.3 for a version: the identification test and the field replay over its replay set (the
    /// approved documents of the supplier and type, the last <paramref name="lastN"/>, the golden documents, the
    /// version's own documents and <paramref name="documentIds"/>, each only when the rule holds on it).
    /// </summary>
    public async Task<ReplayReport> ReplayAsync(SupplierLayout layout, SupplierLayoutVersion version, IReadOnlyCollection<Guid>? documentIds, int lastN, double threshold, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(version);
        var rule = IdentificationRule.Parse(version.IdentificationJson);
        var mapping = LayoutMapping.Parse(version.MappingJson);
        var sources = SourceIds(version);
        var supplierIds = await OwnerSuppliersAsync(layout, cancellationToken).ConfigureAwait(false);
        var values = new Dictionary<Guid, LayoutSetting?>();
        async Task<LayoutSetting?> SettingOf(Guid? supplierId)
        {
            if (supplierId is not { } id)
            {
                return null;
            }

            if (!values.TryGetValue(id, out var setting))
            {
                setting = await LayoutSettings.LoadAsync(db, layout.TenantId, id, cancellationToken).ConfigureAwait(false);
                values[id] = setting;
            }

            return setting;
        }

        async Task<(bool Holds, DocumentWords Words)> HoldsAsync(Document document)
        {
            var words = await WordsAsync(document, cancellationToken).ConfigureAwait(false);
            var setting = await SettingOf(document.SupplierId).ConfigureAwait(false);
            return (IdentificationRuleEvaluator.Evaluate(rule, new IdentificationContext(words, setting?.Values ?? SupplierValues.None, LayoutSettings.SourceOf(document))).Holds, words);
        }

        // 1. The identification test: the rule holds on the version's own documents ...
        var notMatched = new List<Guid>();
        foreach (var document in await LoadDocumentsAsync(sources, cancellationToken).ConfigureAwait(false))
        {
            if (!(await HoldsAsync(document).ConfigureAwait(false)).Holds)
            {
                notMatched.Add(document.Id);
            }
        }

        // ... and on no approved document of the owner's other layouts, nor a pending document of another Candidate.
        var otherVersions = await db.SupplierLayoutVersions.AsNoTracking()
            .Where(v => v.LayoutId != layout.Id && v.Layout!.OwnerId == layout.OwnerId && v.Layout.Status == SupplierLayoutStatus.Active)
            .Select(v => new { v.Id, v.LayoutId, v.Layout!.LayoutKey, v.Status })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var otherIds = otherVersions.Select(v => v.Id).ToList();
        var candidateIds = otherVersions.Where(v => v.Status == SupplierLayoutVersionStatus.Candidate).Select(v => v.Id).ToList();
        var others = await db.Documents.AsNoTracking().Include(d => d.IngestionItem).ThenInclude(i => i!.Ingestion)
            .Where(d => (d.SupplierLayoutVersionId != null && otherIds.Contains(d.SupplierLayoutVersionId.Value) && (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.AutoApproved))
                        || (d.PendingLayoutVersionId != null && candidateIds.Contains(d.PendingLayoutVersionId.Value) && d.Status == DocumentStatus.NeedsReview))
            .Take(MaxMatching)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var overlaps = new List<ReplayOverlap>();
        foreach (var document in others.Where(d => !sources.Contains(d.Id)))
        {
            if ((await HoldsAsync(document).ConfigureAwait(false)).Holds)
            {
                var of = otherVersions.FirstOrDefault(v => v.Id == (document.SupplierLayoutVersionId ?? document.PendingLayoutVersionId));
                overlaps.Add(new ReplayOverlap(document.Id, of?.LayoutId, of?.LayoutKey, document.PendingLayoutVersionId is not null && document.SupplierLayoutVersionId is null ? "pendingCandidate" : "approvedOtherLayout"));
            }
        }

        // 2. The replay set: approved documents of the supplier(s) and type, golden documents, the version's own and the named.
        var typeKey = DocumentTypeKey.Parse(layout.DocumentTypeKey);
        var approved = await db.Documents.AsNoTracking()
            .Where(d => d.SupplierId != null && supplierIds.Contains(d.SupplierId.Value) && d.Type == typeKey
                        && (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.AutoApproved))
            .Select(d => new { d.Id, d.CreatedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var golden = await db.GoldenDocuments.AsNoTracking()
            .Where(g => g.SupplierId != null && supplierIds.Contains(g.SupplierId.Value) && g.TypeKey == layout.DocumentTypeKey)
            .Select(g => g.DocumentId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var candidates = approved.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id).Take(lastN).Select(d => d.Id)
            .Concat(golden).Concat(sources).Concat(documentIds ?? []).Distinct().ToList();
        var stats = mapping.Coverage.ToDictionary(p => p, _ => (Hits: 0, Mismatches: 0, Missing: 0), StringComparer.Ordinal);
        var replayed = new List<ReplayDocument>();
        foreach (var document in await LoadDocumentsAsync(candidates, cancellationToken).ConfigureAwait(false))
        {
            var (holds, words) = await HoldsAsync(document).ConfigureAwait(false);
            if (!holds)
            {
                // A document of another design is not in the set: the field rules were never meant for it (§8.3).
                continue;
            }

            var fields = await CompareAsync(document, layout, version, mapping, words, await SettingOf(document.SupplierId).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            foreach (var (path, outcome) in fields)
            {
                if (!stats.TryGetValue(path, out var s))
                {
                    continue;
                }

                stats[path] = outcome switch
                {
                    "hit" => (s.Hits + 1, s.Mismatches, s.Missing),
                    "mismatch" => (s.Hits, s.Mismatches + 1, s.Missing),
                    "missing" => (s.Hits, s.Mismatches, s.Missing + 1),
                    _ => s,
                };
            }

            replayed.Add(new ReplayDocument(document.Id, document.IngestionItem?.FileName, document.Status.ToString(), true, fields));
        }

        var fieldStats = stats.Select(kv =>
        {
            var compared = kv.Value.Hits + kv.Value.Mismatches + kv.Value.Missing;
            return new ReplayFieldStat(kv.Key, kv.Value.Hits, kv.Value.Mismatches, kv.Value.Missing, compared == 0 ? 1.0 : Math.Round((double)kv.Value.Hits / compared, 4));
        }).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var fieldsPassed = replayed.Count > 0 && fieldStats.All(f => f.Agreement >= threshold);
        return new ReplayReport(notMatched.Count == 0 && overlaps.Count == 0, notMatched, overlaps, replayed, fieldStats, threshold, fieldsPassed, clock.GetUtcNow());
    }

    /// <summary>Per covered field: the version's mapping on the document against the document's confirmed values.</summary>
    private async Task<Dictionary<string, string>> CompareAsync(Document document, SupplierLayout layout, SupplierLayoutVersion version, LayoutMapping mapping, DocumentWords words, LayoutSetting? setting, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var type = await TypeOfAsync(document, layout.DocumentTypeKey, cancellationToken).ConfigureAwait(false);
        if (type?.Compiled is not { } compiled || document.CanonicalJson is null || JsonNode.Parse(document.CanonicalJson) is not JsonObject expected)
        {
            return result;
        }

        var descriptors = compiled.Descriptors;
        var locale = setting is null ? ValueLocale.German : LocaleOf(setting.Supplier, mapping);
        var extraction = LayoutEngine.Run(mapping, words, $"{layout.LayoutKey}@{version.Version}", p => LayoutTreeWriter.KindOf(descriptors, p), p => LayoutTreeWriter.RowFieldsOf(descriptors, p), locale, cancellationToken: cancellationToken);
        // An invoice is written as the coded record also when the tenant forked its type (as ExtractHandler writes it).
        var typedRecord = document.Type.IsInvoiceFamily;
        var (actual, _) = LayoutTreeWriter.Build(descriptors, extraction, mapping.Defaults, setting?.Attributes ?? new Dictionary<string, string>(), document.Currency, typedRecord, document.Type);
        foreach (var path in mapping.Coverage)
        {
            if (mapping.Tables.Any(t => t.Path == path))
            {
                var want = expected[path] as JsonArray;
                var got = actual[path] as JsonArray;
                var table = mapping.Tables.First(t => t.Path == path);
                if (want is not { Count: > 0 })
                {
                    result[path] = "skipped";
                }
                else if (got is not { Count: > 0 })
                {
                    result[path] = "missing";
                }
                else
                {
                    var anchorsAgree = want.Count == got.Count && want.Zip(got).All(pair => ValueComparison.Agree(ValueAt((JsonObject)pair.First!, table.RowAnchor), ValueAt((JsonObject)pair.Second!, table.RowAnchor)));
                    result[path] = anchorsAgree ? "hit" : "mismatch";
                }

                continue;
            }

            var expectedValue = ValueAt(expected, path);
            var actualValue = ValueAt(actual, path);
            result[path] = expectedValue is null ? "skipped" : actualValue is null ? "missing" : ValueComparison.Agree(expectedValue, actualValue) ? "hit" : "mismatch";
        }

        return result;
    }

    /// <summary>
    /// A version's identification rule and field mapping run on a document without writing anything (the sandbox and the
    /// regression runs of docs/11 §15.3): whether the rule holds and why not, and the tree the mapping fills with its drift.
    /// </summary>
    public async Task<(IdentificationResult Identification, JsonObject? Tree, DriftReport? Drift)> DryRunAsync(Document document, string typeKey, IdentificationRule rule, LayoutMapping mapping, string layoutRef, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(mapping);
        var words = await WordsAsync(document, cancellationToken).ConfigureAwait(false);
        var setting = document.SupplierId is { } supplierId ? await LayoutSettings.LoadAsync(db, document.TenantId, supplierId, cancellationToken).ConfigureAwait(false) : null;
        var identification = IdentificationRuleEvaluator.Evaluate(rule, new IdentificationContext(words, setting?.Values ?? SupplierValues.None, LayoutSettings.SourceOf(document)));
        if (!identification.Holds)
        {
            return (identification, null, null);
        }

        var type = await TypeOfAsync(document, typeKey, cancellationToken).ConfigureAwait(false);
        if (type?.Compiled is not { } compiled)
        {
            return (identification, null, null);
        }

        var descriptors = compiled.Descriptors;
        var locale = setting is null ? ValueLocale.German : LocaleOf(setting.Supplier, mapping);
        var required = descriptors.Roots.Where(f => f.Required).Select(f => f.Path).ToList();
        var extraction = LayoutEngine.Run(mapping, words, layoutRef, p => LayoutTreeWriter.KindOf(descriptors, p), p => LayoutTreeWriter.RowFieldsOf(descriptors, p), locale, required, cancellationToken);
        var typedRecord = document.Type.IsInvoiceFamily;
        var (tree, _) = LayoutTreeWriter.Build(descriptors, extraction, mapping.Defaults, setting?.Attributes ?? new Dictionary<string, string>(), document.Currency, typedRecord, DocumentTypeKey.Parse(typeKey));
        return (identification, tree, extraction.Drift);
    }

    /// <summary>
    /// A tree's values as a golden document keeps them (docs/11 §6.9 <c>ExpectedValuesJson</c>) and a regression run
    /// compares them: per field path the comparable value (<see cref="ValueComparison"/>), composite members by their
    /// path, a repeating group by its row count and each row's values (at most 500 rows).
    /// </summary>
    public static JsonObject Snapshot(JsonObject tree, DescriptorIndex descriptors)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(descriptors);
        var snapshot = new JsonObject();
        foreach (var root in descriptors.Roots.Where(r => r.Key != "kind"))
        {
            if (root.Kind == FieldKinds.Array)
            {
                if (tree[root.Key] is JsonArray rows && LayoutTreeWriter.RowFieldsOf(descriptors, root.Path) is { } rowFields)
                {
                    snapshot[$"{root.Key}.count"] = rows.Count;
                    for (var i = 0; i < Math.Min(rows.Count, 500); i++)
                    {
                        foreach (var field in rowFields.Keys)
                        {
                            if (rows[i] is JsonObject row && ValueComparison.Comparable(ValueAt(row, field)) is { } cell)
                            {
                                snapshot[$"{root.Key}[{i}].{field}"] = cell;
                            }
                        }
                    }
                }

                continue;
            }

            if (FieldKinds.IsComposite(root.Kind) && root.Members.Count > 0)
            {
                foreach (var member in root.Members)
                {
                    if (ValueComparison.Comparable(ValueAt(tree, $"{root.Key}.{member.Key}")) is { } value)
                    {
                        snapshot[$"{root.Key}.{member.Key}"] = value;
                    }
                }

                continue;
            }

            if (ValueComparison.Comparable(ValueAt(tree, root.Path)) is { } scalar)
            {
                snapshot[root.Path] = scalar;
            }
        }

        return snapshot;
    }

    /// <summary>The documents a version was built from (<c>SourceDocumentIdsJson</c>).</summary>
    public static List<Guid> SourceIds(SupplierLayoutVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (string.IsNullOrWhiteSpace(version.SourceDocumentIdsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(version.SourceDocumentIdsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private Task<List<Document>> LoadDocumentsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        db.Documents.AsNoTracking().Include(d => d.IngestionItem).ThenInclude(i => i!.Ingestion).Where(d => ids.Contains(d.Id)).ToListAsync(cancellationToken);

    /// <summary>The suppliers whose documents a layout is for: the supplier, or the group's members.</summary>
    private async Task<List<Guid>> OwnerSuppliersAsync(SupplierLayout layout, CancellationToken cancellationToken) =>
        layout.Scope == SupplierLayoutScope.Supplier
            ? [layout.OwnerId]
            : await db.Suppliers.AsNoTracking().Where(s => s.GroupId == layout.OwnerId).Select(s => s.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The documents of the layout's supplier(s) a rule holds on (docs/11 §18 <c>matching-documents</c>): <c>pending</c>,
    /// documents in review with an open <c>NEW_LAYOUT</c> (the sibling re-test of §12.10 step 7), or <c>approved</c>.
    /// With <paramref name="holds"/> false, the documents it does not hold on (the "new version of X" check).
    /// </summary>
    public async Task<List<(Document Document, IdentificationResult Result)>> MatchingAsync(SupplierLayout layout, IdentificationRule rule, string state, bool holds, Guid? onlyLayoutId, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(rule);
        var suppliers = await OwnerSuppliersAsync(layout, cancellationToken).ConfigureAwait(false);
        var type = DocumentTypeKey.Parse(layout.DocumentTypeKey);
        var query = db.Documents.AsNoTracking().Include(d => d.IngestionItem).ThenInclude(i => i!.Ingestion)
            .Where(d => d.SupplierId != null && suppliers.Contains(d.SupplierId.Value));
        if (state == "pending")
        {
            query = query.Where(d => d.Status == DocumentStatus.NeedsReview && d.Flags.Any(f => f.Code == FlagCodes.NewLayout && f.ResolvedAt == null));
        }
        else
        {
            query = query.Where(d => (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.AutoApproved) && d.Type == type);
            if (onlyLayoutId is { } layoutId)
            {
                var versions = db.SupplierLayoutVersions.Where(v => v.LayoutId == layoutId).Select(v => v.Id);
                query = query.Where(d => d.SupplierLayoutVersionId != null && versions.Contains(d.SupplierLayoutVersionId.Value));
            }
        }

        var documents = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<(Document, IdentificationResult)>();
        var settings = new Dictionary<Guid, LayoutSetting?>();
        foreach (var document in documents.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id).Take(MaxMatching))
        {
            if (!settings.TryGetValue(document.SupplierId!.Value, out var setting))
            {
                setting = await LayoutSettings.LoadAsync(db, layout.TenantId, document.SupplierId.Value, cancellationToken).ConfigureAwait(false);
                settings[document.SupplierId.Value] = setting;
            }

            var words = await WordsAsync(document, cancellationToken).ConfigureAwait(false);
            var evaluated = IdentificationRuleEvaluator.Evaluate(rule, new IdentificationContext(words, setting?.Values ?? SupplierValues.None, LayoutSettings.SourceOf(document)));
            if (evaluated.Holds == holds)
            {
                result.Add((document, evaluated));
                if (result.Count >= limit)
                {
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Attaches documents to a Candidate version (docs/11 §12.10 step 7: "the siblings are attached to it instead"):
    /// <c>PendingLayoutVersionId</c> on the document and <c>pendingLayoutVersionId</c>/<c>pendingLayoutKey</c> in its open
    /// <c>NEW_LAYOUT</c>. Saved by the caller.
    /// </summary>
    public async Task<int> AttachAsync(SupplierLayout layout, SupplierLayoutVersion version, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(version);
        var documents = await db.Documents.Include(d => d.Flags).Where(d => documentIds.Contains(d.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var document in documents)
        {
            document.PendingLayoutVersionId = version.Id;
            foreach (var flag in document.Flags.Where(f => f.Code == FlagCodes.NewLayout && f.ResolvedAt is null))
            {
                var parameters = new Dictionary<string, string>(flag.Params ?? new Dictionary<string, string>(), StringComparer.Ordinal)
                {
                    ["pendingLayoutVersionId"] = version.Id.ToString(),
                    ["pendingLayoutKey"] = layout.LayoutKey,
                };
                flag.Params = parameters;
            }
        }

        return documents.Count;
    }

    /// <summary>
    /// Tells the tenant's <c>MappingAuthor</c>s that documents wait for a Candidate (docs/11 §8.4 step 5, §12.10 step 6):
    /// one notification per version, its count the documents waiting. Saved by the caller.
    /// </summary>
    public async Task NotifyCandidateAsync(SupplierLayout layout, SupplierLayoutVersion version, string supplierName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(version);
        var waiting = await db.Documents.CountAsync(d => d.PendingLayoutVersionId == version.Id && d.Status == DocumentStatus.NeedsReview, cancellationToken).ConfigureAwait(false);
        await LayoutNotifications.UpsertAsync(db, layout, version, supplierName, waiting, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Activates a version under the one condition of docs/11 §8.3 (identification test passes, field agreement on a
    /// non-empty replay set at or above the tenant threshold), with <paramref name="warmup"/> or the tenant's warm-up;
    /// the layout's previous Active or Degraded version is retired, the replay is stored, the notifications about the
    /// version are resolved, and the documents waiting for it are returned for reprocessing. Saved by the caller.
    /// </summary>
    public async Task<ActivationOutcome> ActivateAsync(SupplierLayout layout, SupplierLayoutVersion version, string actor, int? warmup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(version);
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == layout.TenantId, cancellationToken).ConfigureAwait(false);
        var replay = await ReplayAsync(layout, version, null, DefaultReplayLastN, tenant.Settings.EffectiveLayoutAgreement(), cancellationToken).ConfigureAwait(false);
        version.ReplayJson = replay.ToJson();
        if (!replay.Passed)
        {
            var reason = !replay.IdentificationPassed
                ? replay.Overlaps.Count > 0
                    ? $"The rule also holds on {replay.Overlaps.Count} document(s) of the supplier's other layouts or a pending Candidate; add an identifier that tells them apart."
                    : $"The rule does not hold on {replay.SourceNotMatched.Count} of the documents the version was built from."
                : replay.Documents.Count == 0
                    ? "The replay set is empty: no approved document of the supplier and type, and none of the version's own, holds the rule."
                    : $"Field agreement below {Math.Round(replay.Threshold * 100)} %: {string.Join(", ", replay.Fields.Where(f => f.Agreement < replay.Threshold).Select(f => $"{f.Path} {Math.Round(f.Agreement * 100)} %"))}.";
            return new ActivationOutcome(false, replay, [], reason);
        }

        var now = clock.GetUtcNow();
        foreach (var previous in layout.Versions.Where(v => v.Id != version.Id && v.Status is SupplierLayoutVersionStatus.Active or SupplierLayoutVersionStatus.Degraded))
        {
            previous.Status = SupplierLayoutVersionStatus.Retired;
        }

        version.Status = SupplierLayoutVersionStatus.Active;
        version.ReviewWarmup = warmup ?? tenant.Settings.EffectiveLayoutReviewWarmup();
        version.WarmupSeen = 0;
        version.ConsecutiveFailures = 0;
        version.ApprovedBy = actor;
        version.ApprovedAt = now;
        layout.ActiveVersion = version.Version;
        layout.UpdatedAt = now;
        await LayoutNotifications.ResolveAsync(db, version.Id, actor, now, cancellationToken).ConfigureAwait(false);
        var waiting = await db.Documents.AsNoTracking().Where(d => d.PendingLayoutVersionId == version.Id && d.Status == DocumentStatus.NeedsReview).Select(d => d.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new ActivationOutcome(true, replay, waiting, null);
    }
}

/// <summary>The notifications of docs/11 §12.10 (Candidates with documents waiting), written by the pipeline and the API.</summary>
public static class LayoutNotifications
{
    public static string KeyOf(Guid versionId) => $"layout-candidate:{versionId}";

    /// <summary>Raises or counts up the open notification of a Candidate with <paramref name="waiting"/> documents; saved by the caller.</summary>
    public static async Task UpsertAsync(DocProcDbContext db, SupplierLayout layout, SupplierLayoutVersion version, string supplierName, int waiting, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(version);
        var key = KeyOf(version.Id);
        var existing = db.Notifications.Local.FirstOrDefault(n => n.DedupeKey == key)
                       ?? await db.Notifications.SingleOrDefaultAsync(n => n.DedupeKey == key, cancellationToken).ConfigureAwait(false);
        var message = waiting == 1
            ? $"1 document of {supplierName} waits for the activation of layout {layout.LayoutKey} (version {version.Version})."
            : $"{waiting} documents of {supplierName} wait for the activation of layout {layout.LayoutKey} (version {version.Version}).";
        var payload = JsonSerializer.Serialize(new { layoutId = layout.Id, layoutKey = layout.LayoutKey, versionId = version.Id, version = version.Version, supplierId = layout.SupplierId, supplierName, waiting });
        if (existing is null)
        {
            db.Notifications.Add(new Notification
            {
                Id = Guid.CreateVersion7(),
                TenantId = layout.TenantId,
                Audience = LayoutLearning.NotificationAudience,
                Kind = LayoutLearning.CandidateWaiting,
                Title = $"Layout {layout.LayoutKey} waits for activation",
                Message = message,
                EntityType = nameof(SupplierLayoutVersion),
                EntityId = version.Id,
                DedupeKey = key,
                Count = Math.Max(1, waiting),
                PayloadJson = payload,
                CreatedAt = now,
                UpdatedAt = now,
            });
            return;
        }

        existing.Message = message;
        existing.Count = Math.Max(1, waiting);
        existing.PayloadJson = payload;
        existing.UpdatedAt = now;
        existing.ResolvedAt = null;
        existing.ResolvedBy = null;
    }

    /// <summary>Resolves the notification of a version (it was activated or retired); saved by the caller.</summary>
    public static async Task ResolveAsync(DocProcDbContext db, Guid versionId, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var key = KeyOf(versionId);
        var existing = db.Notifications.Local.FirstOrDefault(n => n.DedupeKey == key)
                       ?? await db.Notifications.SingleOrDefaultAsync(n => n.DedupeKey == key, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.ResolvedAt is null)
        {
            existing.ResolvedAt = now;
            existing.ResolvedBy = actor;
            existing.UpdatedAt = now;
        }
    }
}
