using System.Text.Json.Nodes;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Suppliers;
using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Mapping.Identification;
using DocProc.Mapping.Layouts;
using DocProc.Mapping.Learning;
using DocProc.Mapping.Values;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Pipeline.Layouts;

/// <summary>What the <c>layout-prefill</c> step made of a <c>NEW_LAYOUT</c> document: the tree, and where each value came from (the chip on it).</summary>
internal sealed record PrefillApplication(ExtractedDocument Document, IReadOnlyDictionary<string, string> Sources, LayoutExtraction Extraction, Guid? BasisVersionId)
{
    /// <summary>The <c>AppliedConfigJson.prefill</c> block: which source filled which path, the basis layout, and whether the LLM filled the rest.</summary>
    public JsonObject AppliedConfig(bool filledByLlm) => new()
    {
        ["step"] = LayoutPrefiller.StepId,
        ["basisVersionId"] = BasisVersionId?.ToString(),
        ["sources"] = new JsonObject([.. Sources.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => KeyValuePair.Create(s.Key, (JsonNode?)JsonValue.Create(s.Value)))]),
        ["filledByLlm"] = filledByLlm,
    };
}

/// <summary>
/// The <c>layout-prefill</c> step (docs/11 §9.3, §12.10 step 4), run in Extract in place of <c>supplier-mapping</c> for a
/// document of a known supplier held with <c>NEW_LAYOUT</c> against its proposed (or reviewer-confirmed) type: the
/// pending Candidate's field mapping as a basis when its rule holds (<c>pendingLayout</c>), the labels and patterns of
/// the supplier's other layouts searched anywhere (positions ignored; a basis layout chosen in review first), the
/// supplier's master data and the values that were the same on every approved document of the supplier located by
/// their text. Every value is Inferred with the place it was found; the LLM fills what is left (fill-missing) only where a
/// provider is configured. Nothing here applies another layout's positions.
/// </summary>
internal sealed class LayoutPrefiller(DocProcDbContext db, PipelineBlobs blobs, LayoutIdentification identification)
{
    public const string StepId = "layout-prefill";

    /// <summary>The approved documents of the supplier and type compared for known values, and how many must agree.</summary>
    public const int KnownValueDocuments = 20;
    public const int KnownValueMinimum = 2;

    public async Task<PrefillApplication?> PrefillAsync(Document document, EffectiveType type, bool typedRecord, string tenantCurrency, string tenantCountry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(type);
        if (document.SupplierLayoutVersionId is not null || document.SupplierId is not { } supplierId || type.Compiled is not { } compiled
            || !await identification.EnabledAsync(document.TenantId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var flag = await db.DocumentFlags.AsNoTracking()
            .Where(f => f.DocumentId == document.Id && f.Code == FlagCodes.NewLayout && f.ResolvedAt == null)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (flag is null)
        {
            return null;
        }

        var setting = await identification.LoadAsync(document.TenantId, supplierId, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            return null;
        }

        var words = DocumentWords.From(await blobs.ReadTextLayerAsync(document, cancellationToken).ConfigureAwait(false));
        var descriptors = compiled.Descriptors;
        var locale = SupplierSettings.Parse(setting.Supplier.SettingsJson).Locale?.Culture is { } culture && ValueLocale.TryParse(culture, out var own) ? own : ValueLocale.ForCountry(tenantCountry);
        LayoutFieldKind? KindOf(string path) => LayoutTreeWriter.KindOf(descriptors, path);
        var hits = new List<LayoutHit>();
        var tables = new Dictionary<string, Mapping.Tables.TableResult>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        void Take(IEnumerable<LayoutHit> found, string source)
        {
            foreach (var hit in found)
            {
                if (!sources.ContainsKey(hit.Path))
                {
                    hits.Add(hit with { Inferred = true, RuleRef = source });
                    sources[hit.Path] = source;
                }
            }
        }

        // 1. A pending Candidate whose rule holds (or the one a reviewer confirmed): its mapping is the basis, values Inferred.
        var parameters = flag.Params ?? new Dictionary<string, string>();
        Guid? basis = Guid.TryParse(parameters.GetValueOrDefault("pendingLayoutVersionId"), out var pending) ? pending : document.PendingLayoutVersionId;
        if (basis is { } basisId && await db.SupplierLayoutVersions.AsNoTracking().Include(v => v.Layout).SingleOrDefaultAsync(v => v.Id == basisId, cancellationToken).ConfigureAwait(false) is { } candidate
            && candidate.Layout?.DocumentTypeKey == type.TypeKey && LayoutMapping.TryParse(candidate.MappingJson, out _) is { } candidateMapping)
        {
            var run = LayoutEngine.Run(candidateMapping, words, $"{candidate.Layout.LayoutKey}@{candidate.Version}", KindOf, p => LayoutTreeWriter.RowFieldsOf(descriptors, p), locale, cancellationToken: cancellationToken);
            Take(run.Hits.Select(h => h with { Confidence = Math.Min(h.Confidence, LayoutPrefill.LabelConfidence) }), PrefillSources.PendingLayout);
            foreach (var (path, table) in run.Tables.Where(t => t.Value.Rows.Count > 0))
            {
                tables[path] = table;
                sources[path] = PrefillSources.PendingLayout;
            }
        }
        else
        {
            basis = null;
        }

        // 2. The labels and patterns of the supplier's other layouts, anywhere: a layout chosen in review first, then the type's, then the rest.
        var basedOn = Guid.TryParse(parameters.GetValueOrDefault("basedOnLayoutId"), out var chosen) ? chosen : (Guid?)null;
        var activeIds = setting.Layouts.SelectMany(l => l.Versions.Where(v => v.Version == l.ActiveVersion).Select(v => (Layout: l, VersionId: v.Id))).ToList();
        var ordered = activeIds
            .OrderBy(x => x.Layout.Id == basedOn ? 0 : x.Layout.DocumentTypeKey == type.TypeKey ? 1 : 2)
            .ThenBy(x => x.Layout.LayoutKey, StringComparer.Ordinal)
            .Select(x => x.VersionId)
            .ToList();
        var mappings = await db.SupplierLayoutVersions.AsNoTracking().Where(v => ordered.Contains(v.Id)).Select(v => new { v.Id, v.MappingJson }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var others = ordered.Select(id => mappings.FirstOrDefault(m => m.Id == id)?.MappingJson).Select(m => LayoutMapping.TryParse(m, out _)).OfType<LayoutMapping>().ToList();
        if (others.Count > 0)
        {
            Take(LayoutPrefill.Search(LayoutPrefill.FromLayouts(others), words, KindOf, locale, cancellationToken), PrefillSources.OtherLayout);
        }

        // 3. Master data and known values, located on the page by their text.
        var located = new List<PrefillValue>();
        var party = descriptors.Find("supplier") is { Kind: FieldKinds.Party } ? "supplier" : null;
        if (party is not null)
        {
            void Master(string member, string? value, bool unplaced)
            {
                if (!string.IsNullOrWhiteSpace(value) && KindOf($"{party}.{member}") is { } kind)
                {
                    located.Add(new PrefillValue($"{party}.{member}", kind.Kind, value, PrefillSources.MasterData, unplaced));
                }
            }

            Master("name", setting.Supplier.Name, unplaced: true);
            Master("vatId", setting.Supplier.VatId, unplaced: true);
            Master("taxId", setting.Supplier.TaxId, unplaced: false);
            Master("iban", setting.Supplier.Ibans.FirstOrDefault(), unplaced: false);
        }

        foreach (var (path, value) in await KnownValuesAsync(document, supplierId, descriptors, cancellationToken).ConfigureAwait(false))
        {
            if (KindOf(path) is { } kind)
            {
                located.Add(new PrefillValue(path, kind.Kind, value, PrefillSources.KnownValue));
            }
        }

        Take(LayoutPrefill.Locate(words, located, locale), PrefillSources.MasterData);
        foreach (var value in located.Where(v => v.Source == PrefillSources.KnownValue && sources.GetValueOrDefault(v.Path) == PrefillSources.MasterData))
        {
            sources[value.Path] = PrefillSources.KnownValue;
        }

        if (hits.Count == 0 && tables.Count == 0)
        {
            return null;
        }

        var extraction = new LayoutExtraction(hits, tables, new DriftReport(0, 0, [], [], false));
        var currency = SupplierSettings.Parse(setting.Supplier.SettingsJson).Defaults?.Currency ?? setting.Supplier.DefaultCurrency ?? tenantCurrency;
        var (tree, _) = LayoutTreeWriter.Build(descriptors, extraction, [], setting.Attributes, currency, typedRecord, document.Type);
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

        return new PrefillApplication(extracted, sources, extraction, basis);
    }

    /// <summary>
    /// The values that were the same on every approved document of the supplier and type (at least
    /// <see cref="KnownValueMinimum"/>, of the last <see cref="KnownValueDocuments"/>): top-level text and identifier fields
    /// other than the document's number, as printed.
    /// </summary>
    private async Task<Dictionary<string, string>> KnownValuesAsync(Document document, Guid supplierId, DescriptorIndex descriptors, CancellationToken cancellationToken)
    {
        var type = document.Type;
        var trees = await db.Documents.AsNoTracking()
            .Where(d => d.SupplierId == supplierId && d.Type == type && d.Id != document.Id && (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.AutoApproved) && d.CanonicalJson != null)
            .Select(d => new { d.CreatedAt, d.Id, d.CanonicalJson })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var recent = trees.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Take(KnownValueDocuments).Select(t => JsonNode.Parse(t.CanonicalJson!) as JsonObject).OfType<JsonObject>().ToList();
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        if (recent.Count < KnownValueMinimum)
        {
            return known;
        }

        foreach (var field in descriptors.Roots.Where(f => f.Kind is FieldKinds.String or FieldKinds.Identifier && f.Key is not ("invoiceNumber" or "documentNumber" or "kind")))
        {
            var values = recent.Select(t => LayoutLearning.ValueAt(t, field.Path)).Select(v => v switch
            {
                JsonValue s when s.TryGetValue<string>(out var text) => text,
                JsonObject o when o["verbatim"] is JsonValue verbatim && verbatim.TryGetValue<string>(out var vs) => vs,
                _ => null,
            }).ToList();
            if (values.All(v => !string.IsNullOrWhiteSpace(v)) && values.Distinct(StringComparer.Ordinal).Count() == 1)
            {
                known[field.Path] = values[0]!;
            }
        }

        return known;
    }
}
