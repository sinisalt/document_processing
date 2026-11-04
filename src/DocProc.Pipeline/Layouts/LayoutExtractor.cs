using System.Text.Json.Nodes;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Suppliers;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Mapping.Identification;
using DocProc.Mapping.Layouts;
using DocProc.Mapping.Tables;
using DocProc.Mapping.Values;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocProc.Pipeline.Layouts;

/// <summary>What the <c>supplier-mapping</c> step made of a document: the tree, what the mapping found and how it applies.</summary>
internal sealed record LayoutApplication(
    ExtractedDocument Document,
    LayoutExtraction Extraction,
    SupplierLayout Layout,
    SupplierLayoutVersion Version,
    IReadOnlyDictionary<string, string> Written,
    IReadOnlyList<DocumentFlag> Flags,
    IReadOnlyList<IdentifierEvidence> Evidence)
{
    /// <summary>Whether fields the mapping covers, or required fields of the type, stayed Missing: the LLM fills them when configured (docs/11 §8.2).</summary>
    public bool NeedsFill { get; init; }

    /// <summary>The <c>AppliedConfigJson.layout</c> block (docs/11 §6.2): which layout version, its evidence, which rule filled which field, the drift check.</summary>
    public JsonObject AppliedConfig(bool filledByLlm) => new()
    {
        ["layoutId"] = Layout.Id.ToString(),
        ["layoutKey"] = Layout.LayoutKey,
        ["versionId"] = Version.Id.ToString(),
        ["version"] = Version.Version,
        ["identificationHash"] = Version.IdentificationHash,
        ["hits"] = new JsonObject([.. Written.OrderBy(w => w.Key, StringComparer.Ordinal).Select(w => KeyValuePair.Create(w.Key, (JsonNode?)JsonValue.Create(w.Value)))]),
        ["drift"] = new JsonObject
        {
            ["covered"] = Extraction.Drift.Covered,
            ["found"] = Extraction.Drift.Found,
            ["missing"] = new JsonArray([.. Extraction.Drift.Missing.Select(m => (JsonNode?)JsonValue.Create(m))]),
            ["isDrift"] = Extraction.Drift.IsDrift,
            ["timedOut"] = Extraction.TimedOut.Count == 0 ? null : new JsonArray([.. Extraction.TimedOut.Select(m => (JsonNode?)JsonValue.Create(m))]),
        },
        ["identification"] = new JsonArray([.. Evidence.Where(e => e.Positive).Select(e => (JsonNode?)new JsonObject
        {
            ["path"] = e.Path,
            ["kind"] = e.Kind,
            ["description"] = e.Description,
            ["page"] = e.Page,
            ["text"] = e.Text,
            ["bBox"] = e.Box is { } b ? new JsonArray(Math.Round(b.X0, 2), Math.Round(b.Y0, 2), Math.Round(b.X1, 2), Math.Round(b.Y1, 2)) : null,
        })]),
        ["filledByLlm"] = filledByLlm,
    };
}

/// <summary>
/// The <c>supplier-mapping</c> step (docs/11 §8.2, §9.3): runs the field mapping of the layout version the identification
/// rule selected (<c>Documents.SupplierLayoutVersionId</c>) over the document's words with <see cref="LayoutEngine"/>, and
/// writes the result as the type version's tree (<see cref="LayoutTreeWriter"/>). It yields nothing for a document no
/// layout identified, for a Degraded version (the LLM fills everything), and for a tree the coded invoice record cannot
/// read. A drift (§8.2) raises <c>MAPPING_DRIFT</c>; consecutive drifts make the version Degraded.
/// </summary>
internal sealed class LayoutExtractor(DocProcDbContext db, PipelineBlobs blobs, LayoutIdentification identification, DocumentTimeline timeline, ILogger<LayoutExtractor> logger)
{
    /// <summary>Consecutive drifting documents after which a version is Degraded (docs/11 §8.2).</summary>
    public const int DegradeAfter = 3;

    public const string StepId = "supplier-mapping";

    public async Task<LayoutApplication?> ApplyAsync(Document document, EffectiveType type, bool typedRecord, string tenantCurrency, string tenantCountry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(type);
        if (document.SupplierLayoutVersionId is not { } versionId || document.SupplierId is not { } supplierId || type.Compiled is not { } compiled
            || !await identification.EnabledAsync(document.TenantId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var version = await db.SupplierLayoutVersions.AsNoTracking().Include(v => v.Layout).SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken).ConfigureAwait(false);
        if (version?.Layout is not { } layout || version.Status != SupplierLayoutVersionStatus.Active
            || !string.Equals(layout.DocumentTypeKey, type.TypeKey, StringComparison.Ordinal))
        {
            // A Degraded version identifies, but its positions no longer hold: the LLM fills everything (§8.2).
            return null;
        }

        var mapping = LayoutMapping.TryParse(version.MappingJson, out _);
        var setting = await identification.LoadAsync(document.TenantId, supplierId, cancellationToken).ConfigureAwait(false);
        if (mapping is null || setting is null)
        {
            return null;
        }

        var layer = await blobs.ReadTextLayerAsync(document, cancellationToken).ConfigureAwait(false);
        var words = DocumentWords.From(layer);
        var locale = LocaleOf(mapping, setting.Supplier, tenantCountry);
        var descriptors = compiled.Descriptors;
        var required = descriptors.Roots.Where(f => f.Required).Select(f => f.Path).ToList();
        var extraction = LayoutEngine.Run(
            mapping,
            words,
            $"{layout.LayoutKey}@{version.Version}",
            path => LayoutTreeWriter.KindOf(descriptors, path),
            path => LayoutTreeWriter.RowFieldsOf(descriptors, path),
            locale,
            required,
            cancellationToken);

        // Confidence is scaled by the version's success rate (§8.2): corrections on mapped fields lower it.
        var scale = version.Applied > 0 ? Math.Clamp(1.0 - ((double)version.Corrected / version.Applied), 0.5, 1.0) : 1.0;
        var currency = SupplierSettings.Parse(setting.Supplier.SettingsJson).Defaults?.Currency ?? setting.Supplier.DefaultCurrency ?? tenantCurrency;
        var (tree, written) = LayoutTreeWriter.Build(descriptors, extraction, mapping.Defaults, setting.Attributes, currency, typedRecord, document.Type, scale);
        var extracted = new ExtractedDocument(tree);
        if (typedRecord)
        {
            try
            {
                _ = extracted.ToInvoice();
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        var evidence = IdentificationRule.TryParse(version.IdentificationJson, out _) is { } rule
            ? IdentificationRuleEvaluator.Evaluate(rule, new IdentificationContext(words, setting.Values, LayoutIdentification.SourceOf(document))).Evidence
            : [];
        var flags = new List<DocumentFlag>();
        if (extraction.Drift.IsDrift)
        {
            flags.Add(FlagCodes.Create(
                FlagCodes.MappingDrift,
                $"The layout {layout.LayoutKey} recognised the document, but its field rules found {extraction.Drift.Found} of {extraction.Drift.Covered} fields" +
                (extraction.Drift.RequiredMissing.Count > 0 ? $" and not the required {string.Join(", ", extraction.Drift.RequiredMissing)}" : string.Empty) + ".",
                parameters: FlagParams.Of(
                    ("layoutKey", layout.LayoutKey),
                    ("version", FlagParams.Number(version.Version)),
                    ("found", FlagParams.Number(extraction.Drift.Found)),
                    ("covered", FlagParams.Number(extraction.Drift.Covered)),
                    ("missing", string.Join(",", extraction.Drift.Missing)))));
        }

        var needsFill = extraction.Drift.Missing.Count > 0
                        || required.Any(p => DocProc.Application.Documents.DefinitionTree.ValueOf(tree[p]) is null && tree[p] is not JsonArray);
        return new LayoutApplication(extracted, extraction, layout, version, written, flags, evidence) { NeedsFill = needsFill };
    }

    /// <summary>
    /// Saves the stage's work (the context's pending changes) together with the version's counters after an application
    /// (docs/11 §6.6 stats): applied, and consecutive drifting documents, which make the version Degraded after
    /// <see cref="DegradeAfter"/>. A document counts once per version, on its first extraction under it (a
    /// <c>document.layout_applied</c> event keyed by the version id, as the warm-up does): reprocessing a document does
    /// not count it again, so one drifting document run three times does not degrade its layout. The counters are
    /// written with <c>ExecuteUpdate</c> (atomic against other documents of the version) in one transaction with the
    /// stage's save, under the context's execution strategy, so they commit with the <c>layout_applied</c> event or not
    /// at all: a save that fails, or is dropped as a stale twin's (<see cref="DbUpdateConcurrencyException"/>, thrown on
    /// to the stage), takes its count back with it. A drift caused by a pattern that ran out of time
    /// (<see cref="LayoutExtraction.TimedOut"/>) counts as applied, not as a failure, and is logged. A version that
    /// degrades is recorded on the timeline.
    /// </summary>
    public async Task SaveCountedAsync(Document document, LayoutApplication application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(application);
        var id = application.Version.Id;
        var timedOut = application.Extraction.TimedOut;
        if (timedOut.Count > 0)
        {
            logger.LogWarning(
                "Document {DocumentId}: a pattern of layout {LayoutKey}@{Version} ran out of time on {Paths}; those were not read, and the drift check does not count against the layout.",
                document.Id,
                application.Layout.LayoutKey,
                application.Version.Version,
                string.Join(", ", timedOut));
        }

        // DetailsJson is a json column, on which SQL Server refuses LIKE: the document's few events of the kind are
        // read and the version is looked for here.
        var marker = $"\"versionId\":\"{id}\"";
        var counted = (await db.DocumentEvents.AsNoTracking()
                .Where(e => e.DocumentId == document.Id && e.EventType == LayoutEventTypes.Applied && e.DetailsJson != null)
                .Select(e => e.DetailsJson)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Any(d => d!.Replace(" ", string.Empty, StringComparison.Ordinal).Contains(marker, StringComparison.OrdinalIgnoreCase));
        var drift = application.Extraction.Drift.IsDrift;
        if (!counted)
        {
            timeline.Record(document, LayoutEventTypes.Applied, null, null, new { versionId = id, layoutKey = application.Layout.LayoutKey, version = application.Version.Version, drift, timedOut = timedOut.Count > 0 ? timedOut : null });
        }

        DocumentEvent? degradedEvent = null;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(
            async ct =>
            {
                // A retry after a transient failure starts again: the degrade event of the failed attempt goes.
                if (degradedEvent is not null)
                {
                    db.Entry(degradedEvent).State = EntityState.Detached;
                    degradedEvent = null;
                }

                await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                if (!counted)
                {
                    degradedEvent = await CountAsync(document, application, drift && timedOut.Count == 0, drift, ct).ConfigureAwait(false);
                }

                // The changes are accepted only after the commit, so a retried attempt saves them again.
                await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.AcceptAllChanges();
    }

    /// <summary>The counter updates of <see cref="SaveCountedAsync"/>, in its transaction; the degrade event when the version degraded.</summary>
    private async Task<DocumentEvent?> CountAsync(Document document, LayoutApplication application, bool failure, bool drift, CancellationToken cancellationToken)
    {
        var id = application.Version.Id;
        if (failure)
        {
            await db.SupplierLayoutVersions.Where(v => v.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Applied, v => v.Applied + 1).SetProperty(v => v.ConsecutiveFailures, v => v.ConsecutiveFailures + 1), cancellationToken).ConfigureAwait(false);
            var degraded = await db.SupplierLayoutVersions
                .Where(v => v.Id == id && v.Status == SupplierLayoutVersionStatus.Active && v.ConsecutiveFailures >= DegradeAfter)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, SupplierLayoutVersionStatus.Degraded), cancellationToken).ConfigureAwait(false);
            return degraded > 0
                ? timeline.Record(document, LayoutEventTypes.Degraded, null, null, new { layoutKey = application.Layout.LayoutKey, version = application.Version.Version, after = DegradeAfter })
                : null;
        }

        if (drift)
        {
            // A drift from a pattern that ran out of time: the document was not read, so it neither fails the version
            // nor clears its run of failures.
            await db.SupplierLayoutVersions.Where(v => v.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Applied, v => v.Applied + 1), cancellationToken).ConfigureAwait(false);
            return null;
        }

        await db.SupplierLayoutVersions.Where(v => v.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.Applied, v => v.Applied + 1).SetProperty(v => v.ConsecutiveFailures, 0), cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>The locale printed values are read in: the mapping's, the supplier's, or the tenant country's.</summary>
    private static ValueLocale LocaleOf(LayoutMapping mapping, Supplier supplier, string tenantCountry)
    {
        if (mapping.Culture is { } culture && ValueLocale.TryParse(culture, out var own))
        {
            return own;
        }

        if (SupplierSettings.Parse(supplier.SettingsJson).Locale?.Culture is { } supplierCulture && ValueLocale.TryParse(supplierCulture, out var supplierLocale))
        {
            return supplierLocale;
        }

        return ValueLocale.ForCountry(tenantCountry);
    }

    /// <summary>
    /// <paramref name="filled"/> (the LLM's tree) into <paramref name="layout"/>'s: every top-level field the layout left
    /// Missing (or a repeating group it left empty) takes the LLM's value; what the layout found stays (docs/11 §9.2:
    /// a step writes only fields that are still Missing).
    /// </summary>
    public static JsonObject Merge(JsonObject layout, JsonObject filled)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(filled);
        var merged = (JsonObject)layout.DeepClone();
        foreach (var (key, value) in filled)
        {
            var current = merged[key];
            var missing = current is null
                          || (DocProc.Application.Documents.DefinitionTree.IsEnvelope(current) && DocProc.Application.Documents.DefinitionTree.ValueOf(current) is null)
                          || current is JsonArray { Count: 0 };
            if (missing && value is not null)
            {
                merged[key] = value.DeepClone();
            }
        }

        return merged;
    }

    /// <summary>The engine's table report in its stored shape (<c>DocumentTableReport.ReportJson</c>, the API's <c>TableReportDto</c>).</summary>
    public static string ReportJson(TableReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        static JsonObject Box(DocProc.Domain.Fields.BoundingBox b) => new() { ["x0"] = Math.Round(b.X0, 2), ["y0"] = Math.Round(b.Y0, 2), ["x1"] = Math.Round(b.X1, 2), ["y1"] = Math.Round(b.Y1, 2) };
        static JsonArray Texts(IEnumerable<string>? texts) => new([.. (texts ?? []).Select(t => (JsonNode?)JsonValue.Create(t))]);
        var json = new JsonObject
        {
            ["rows"] = report.Rows,
            ["rejectedRows"] = report.RejectedRows,
            ["pages"] = new JsonArray([.. report.Pages.Select(p => (JsonNode?)JsonValue.Create(p))]),
            ["pagesWithoutRows"] = new JsonArray([.. report.PagesWithoutRows.Select(p => (JsonNode?)JsonValue.Create(p))]),
            ["stoppedOnPage"] = report.StoppedOnPage,
            ["perPage"] = new JsonArray([.. report.PerPage.Select(p => (JsonNode?)new JsonObject
            {
                ["page"] = p.Page,
                ["headerFound"] = p.HeaderFound,
                ["rows"] = p.Rows,
                ["rejected"] = p.Rejected,
                ["rejectedLines"] = new JsonArray([.. p.RejectedLines.Select(r => (JsonNode?)new JsonObject
                {
                    ["page"] = r.Page,
                    ["text"] = r.Text.Length <= 2000 ? r.Text : r.Text[..2000],
                    ["bBox"] = Box(r.Box),
                    ["reason"] = r.Reason,
                    ["message"] = r.Message,
                    ["field"] = r.Field,
                })]),
                ["skippedLines"] = p.SkippedLines,
                ["groupHeadings"] = p.GroupHeadings,
                ["continuationLines"] = p.ContinuationLines,
                ["columns"] = new JsonArray([.. p.Columns.Select(c => (JsonNode?)new JsonObject { ["x0"] = c.X0, ["x1"] = c.X1 })]),
                ["columnsFrom"] = p.ColumnsFrom,
                ["firstRows"] = new JsonArray([.. p.FirstRows.Select(r => (JsonNode?)new JsonObject([.. r.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))]))]),
                ["top"] = p.Top,
                ["bottom"] = p.Bottom,
                ["headings"] = Texts(p.Headings),
                ["repeatedLines"] = p.RepeatedLines,
                ["leftOutLines"] = p.LeftOutLines,
                ["repeatedTexts"] = Texts(p.RepeatedTexts),
            })]),
        };
        return json.ToJsonString();
    }
}
