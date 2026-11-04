using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Configuration;
using DocProc.Application.Suppliers;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Mapping.Identification;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Pipeline.Layouts;

/// <summary>Event names of layout identification (docs/11 §8.4); timeline only, never published.</summary>
public static class LayoutEventTypes
{
    /// <summary>A layout's identification rule held: its layout, version and evidence.</summary>
    public const string Identified = "document.layout_identified";

    /// <summary>No layout identified a layout-managed supplier's document (<c>NEW_LAYOUT</c>), or several did (<c>LAYOUT_AMBIGUOUS</c>).</summary>
    public const string NotIdentified = "document.layout_not_identified";

    /// <summary>The layout found in Match had another type than the one Classify gave; Match corrected it before Extract.</summary>
    public const string TypeCorrected = "document.type_corrected_by_layout";

    /// <summary>The document counted towards a new layout version's review warm-up.</summary>
    public const string Warmup = "document.layout_warmup";
}

/// <summary>Everything the resolver was asked with, for the handlers and the test endpoint.</summary>
public sealed record LayoutSetting(
    Supplier Supplier,
    IReadOnlyList<SupplierLayout> Layouts,
    IReadOnlyList<LayoutCandidate> Candidates,
    SupplierValues Values,
    IReadOnlyDictionary<string, string> Attributes,
    bool LayoutsRequired,
    IReadOnlyDictionary<Guid, SimilarityProfile?> Profiles);

/// <summary>
/// What the layout steps and the layout endpoints read about a supplier and a document: its layouts as resolver
/// candidates (the Active or Degraded version of each layout, Shadow and Candidate versions), its master data for
/// <c>supplierValue</c> identifiers (VAT id, tax id, IBANs, the group's attributes under its own), whether
/// <c>layouts.required</c> resolves to true, and a document's item kind and source facts.
/// </summary>
public static class LayoutSettings
{
    /// <summary>The item kind as layouts name it: <c>einvoice-xml</c> for an e-invoice (its XML carries the type), else the item's kind.</summary>
    public static string ItemKindOf(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.EmbeddedXmlBlob is not null || document.TypeSource == TypeSource.Structured)
        {
            return LayoutResolver.EInvoiceItemKind;
        }

        return document.IngestionItem?.Kind switch
        {
            ItemKind.Pdf => "pdf",
            ItemKind.Image => "image",
            ItemKind.Xml => LayoutResolver.EInvoiceItemKind,
            { } other => other.ToString().ToLowerInvariant(),
            null => "pdf",
        };
    }

    /// <summary>The source facts of the document's ingestion (docs/11 §8.5 <c>source</c> identifiers).</summary>
    public static SourceFacts SourceOf(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var ingestion = document.IngestionItem?.Ingestion;
        return new SourceFacts(ItemKindOf(document), document.IngestionItem?.FileName ?? ingestion?.SourceFileName, ingestion?.SourceSubject, ingestion?.SourceMailbox, ingestion?.SourceFolderPath);
    }

    /// <summary>The supplier's layouts and master data, or null when the supplier does not exist.</summary>
    public static async Task<LayoutSetting?> LoadAsync(DocProcDbContext db, Guid tenantId, Guid supplierId, CancellationToken cancellationToken)
    {
        var supplier = await db.Suppliers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == supplierId, cancellationToken).ConfigureAwait(false);
        if (supplier is null)
        {
            return null;
        }

        var group = supplier.GroupId is { } groupId
            ? await db.SupplierGroups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false)
            : null;
        var tenantDefault = (await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId, cancellationToken).ConfigureAwait(false)).Settings.LayoutsRequiredByDefault;
        var settings = SupplierSettings.Parse(supplier.SettingsJson).Over(SupplierSettings.Parse(group?.SettingsJson));

        var layouts = await db.SupplierLayouts.AsNoTracking()
            .Include(l => l.Versions)
            .Where(l => l.Status == SupplierLayoutStatus.Active
                        && ((l.Scope == SupplierLayoutScope.Supplier && l.SupplierId == supplierId)
                            || (l.Scope == SupplierLayoutScope.Group && supplier.GroupId != null && l.GroupId == supplier.GroupId)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var candidates = new List<LayoutCandidate>();
        var profiles = new Dictionary<Guid, SimilarityProfile?>();
        foreach (var layout in layouts.OrderBy(l => l.LayoutKey, StringComparer.Ordinal))
        {
            foreach (var version in layout.Versions.OrderBy(v => v.Version))
            {
                LayoutCandidateState? state = version.Status switch
                {
                    SupplierLayoutVersionStatus.Active when layout.ActiveVersion == version.Version => LayoutCandidateState.Active,
                    SupplierLayoutVersionStatus.Degraded when layout.ActiveVersion == version.Version => LayoutCandidateState.Degraded,
                    SupplierLayoutVersionStatus.Shadow => LayoutCandidateState.Shadow,
                    SupplierLayoutVersionStatus.Candidate => LayoutCandidateState.Candidate,
                    _ => null,
                };
                if (state is null || IdentificationRule.TryParse(version.IdentificationJson, out _) is not { } rule)
                {
                    continue;
                }

                candidates.Add(new LayoutCandidate(
                    layout.Id,
                    layout.LayoutKey,
                    version.Id,
                    version.Version,
                    layout.DocumentTypeKey,
                    layout.Scope == SupplierLayoutScope.Group ? LayoutScope.Group : LayoutScope.Supplier,
                    state.Value,
                    rule,
                    layout.Priority,
                    layout.ItemKinds.Count > 0 ? layout.ItemKinds : null,
                    layout.ReceivingEntityId));
                profiles[version.Id] = SimilarityProfile.Parse(version.SimilarityProfileJson);
            }
        }

        var attributes = Scalars(group?.AttributesJson);
        foreach (var (key, value) in Scalars(supplier.AttributesJson))
        {
            attributes[key] = value;
        }

        var values = new SupplierValues(supplier.VatId, supplier.TaxId, supplier.Ibans, attributes);
        return new LayoutSetting(supplier, layouts, candidates, values, attributes, settings.LayoutsRequired(tenantDefault), profiles);
    }

    /// <summary>The scalars of a JSON object of attributes, as strings.</summary>
    public static Dictionary<string, string> Scalars(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            if (JsonNode.Parse(json) is JsonObject obj)
            {
                foreach (var (key, value) in obj)
                {
                    if (value is JsonValue v && v.GetValueKind() is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    {
                        result[key] = v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Attributes written outside the API that do not read are no master data.
        }

        return result;
    }

}

/// <summary>
/// The <c>classify.identification</c> and <c>match.layout</c> steps (docs/11 §8.4, §9.4): load the supplier's and its
/// group's layouts, run <see cref="LayoutResolver"/> over the document's words, source facts and the supplier's master
/// data, and record the outcome: the identified layout version on the document with a timeline event and its counters,
/// or <c>NEW_LAYOUT</c> (with the proposed type, the suggestions of <see cref="LayoutSuggester"/> and a pending Candidate)
/// or <c>LAYOUT_AMBIGUOUS</c>, each instance owned by the step that raised it (<c>stepId</c> in its parameters). It runs
/// only for tenants with <c>supplierLayouts</c> on and for documents whose supplier is known.
/// </summary>
internal sealed class LayoutIdentification(DocProcDbContext db, ITenantFeatures features, PipelineBlobs blobs, DocumentFlagWriter flags, DocumentTimeline timeline)
{
    public const string ClassifyStep = "classify.identification";
    public const string MatchStep = "match.layout";

    /// <summary>The flag codes the two steps raise; each step owns its own instances.</summary>
    public static readonly string[] Codes = [FlagCodes.NewLayout, FlagCodes.LayoutAmbiguous];

    public Task<bool> EnabledAsync(Guid tenantId, CancellationToken cancellationToken) =>
        features.IsEnabledAsync(tenantId, TenantFeature.SupplierLayouts, cancellationToken);

    /// <summary>The supplier's layouts and master data, or null when the supplier does not exist.</summary>
    public Task<LayoutSetting?> LoadAsync(Guid tenantId, Guid supplierId, CancellationToken cancellationToken) =>
        LayoutSettings.LoadAsync(db, tenantId, supplierId, cancellationToken);

    /// <inheritdoc cref="LayoutSettings.ItemKindOf"/>
    public static string ItemKindOf(Document document) => LayoutSettings.ItemKindOf(document);

    /// <inheritdoc cref="LayoutSettings.SourceOf"/>
    public static SourceFacts SourceOf(Document document) => LayoutSettings.SourceOf(document);

    /// <summary>Resolves the document's layout among its supplier's (docs/11 §8.4); null when the step does not run.</summary>
    public async Task<(LayoutResolverResult Result, LayoutSetting Setting, DocumentWords Words)?> ResolveAsync(Document document, string? fixedType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SupplierId is not { } supplierId || !await EnabledAsync(document.TenantId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var setting = await LoadAsync(document.TenantId, supplierId, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            return null;
        }

        var layer = await blobs.ReadTextLayerAsync(document, cancellationToken).ConfigureAwait(false);
        var words = DocumentWords.From(layer);
        var input = new LayoutResolverInput(
            new IdentificationContext(words, setting.Values, SourceOf(document)),
            setting.Candidates,
            ItemKindOf(document),
            fixedType,
            setting.LayoutsRequired,
            document.ReceivingEntityId);
        return (LayoutResolver.Instance.Resolve(input), setting, words);
    }

    /// <summary>
    /// Records what the step found: the identified version (or none) on the document, the step's flags, a timeline event
    /// and the version's and layout's counters. <paramref name="proposedType"/> is the type proposed for a new layout.
    /// </summary>
    public async Task RecordAsync(Document document, string stepId, LayoutResolverResult result, LayoutSetting setting, DocumentWords words, string? proposedType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(setting);
        var raised = new List<DocumentFlag>();
        switch (result.Resolution)
        {
            case LayoutResolution.Identified:
                var selected = result.Selected!;
                document.SupplierLayoutVersionId = selected.Candidate.VersionId;
                document.PendingLayoutVersionId = null;
                timeline.Record(document, LayoutEventTypes.Identified, null, null, new
                {
                    stepId,
                    layoutId = selected.Candidate.LayoutId,
                    layoutKey = selected.Candidate.LayoutKey,
                    versionId = selected.Candidate.VersionId,
                    version = selected.Candidate.Version,
                    type = selected.Candidate.DocumentTypeKey,
                    state = selected.Candidate.State.ToString(),
                    evidence = selected.Result.Evidence.Where(e => e.Positive).Select(e => new { e.Path, e.Kind, e.Description, e.Page, box = e.Box is { } b ? new[] { b.X0, b.Y0, b.X1, b.Y1 } : null, e.Text }).ToList(),
                });
                var now = timeline.Now;
                await db.SupplierLayoutVersions.Where(v => v.Id == selected.Candidate.VersionId)
                    .ExecuteUpdateAsync(s => s.SetProperty(v => v.Identified, v => v.Identified + 1), cancellationToken).ConfigureAwait(false);
                await db.SupplierLayouts.Where(l => l.Id == selected.Candidate.LayoutId)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.LastMatchedAt, now), cancellationToken).ConfigureAwait(false);
                break;

            case LayoutResolution.Ambiguous:
                document.SupplierLayoutVersionId = null;
                document.PendingLayoutVersionId = null;
                var keys = string.Join(", ", result.Tied.Select(t => $"{t.Candidate.LayoutKey}@{t.Candidate.Version}"));
                raised.Add(FlagCodes.Create(
                    FlagCodes.LayoutAmbiguous,
                    $"Several layouts of {setting.Supplier.Name} recognise the document equally ({keys}); none was applied.",
                    parameters: FlagParams.Of(
                        ("stepId", stepId),
                        ("supplierId", setting.Supplier.Id.ToString()),
                        ("layouts", keys),
                        ("layoutIds", string.Join(",", result.Tied.Select(t => t.Candidate.LayoutId))))));
                timeline.Record(document, LayoutEventTypes.NotIdentified, null, null, new { stepId, resolution = "ambiguous", layouts = result.Tied.Select(t => t.Candidate.LayoutKey).ToList() });
                break;

            case LayoutResolution.NewLayout:
                document.SupplierLayoutVersionId = null;

                // A Candidate whose rule holds (docs/11 §8.4 step 5): the document waits for it, and the MappingAuthors hear of it.
                document.PendingLayoutVersionId = result.Pending?.Candidate.VersionId;
                if (result.Pending is { } waiting && setting.Layouts.FirstOrDefault(l => l.Id == waiting.Candidate.LayoutId) is { } pendingLayout
                    && pendingLayout.Versions.FirstOrDefault(v => v.Id == waiting.Candidate.VersionId) is { } pendingVersion)
                {
                    var others = await db.Documents.CountAsync(d => d.PendingLayoutVersionId == pendingVersion.Id && d.Id != document.Id && d.Status == DocumentStatus.NeedsReview, cancellationToken).ConfigureAwait(false);
                    await LayoutNotifications.UpsertAsync(db, pendingLayout, pendingVersion, setting.Supplier.Name, others + 1, timeline.Now, cancellationToken).ConfigureAwait(false);
                }

                var suggestions = LayoutSuggester.Suggest(
                    SimilarityProfile.Of(words, ItemKindOf(document)),
                    result.Evaluated.Where(e => e.Candidate.State is LayoutCandidateState.Active or LayoutCandidateState.Degraded)
                        .Select(e => (e, setting.Profiles.GetValueOrDefault(e.Candidate.VersionId))));
                var suggestionJson = JsonSerializer.Serialize(suggestions.Select(s => new { s.LayoutId, s.LayoutKey, s.DocumentTypeKey, s.Similarity, s.Differences }), PipelineJson.Options);
                raised.Add(FlagCodes.Create(
                    FlagCodes.NewLayout,
                    result.Pending is { } p
                        ? $"{setting.Supplier.Name} sent a design none of its active layouts recognises; it matches the pending layout {p.Candidate.LayoutKey}, which waits for activation."
                        : $"{setting.Supplier.Name} sent a design none of its layouts recognises; the document is held for review and not processed with another layout.",
                    parameters: FlagParams.Of(
                        ("stepId", stepId),
                        ("supplierId", setting.Supplier.Id.ToString()),
                        ("supplierName", setting.Supplier.Name),
                        ("proposedType", proposedType),
                        ("suggestions", suggestionJson),
                        ("pendingLayoutVersionId", result.Pending?.Candidate.VersionId.ToString()),
                        ("pendingLayoutKey", result.Pending?.Candidate.LayoutKey))));
                timeline.Record(document, LayoutEventTypes.NotIdentified, null, null, new
                {
                    stepId,
                    resolution = "newLayout",
                    proposedType,
                    pendingLayoutVersionId = result.Pending?.Candidate.VersionId,
                    suggestions = suggestions.Select(s => new { s.LayoutKey, s.Similarity }).ToList(),
                    evaluated = result.Evaluated.Select(e => new { e.Candidate.LayoutKey, e.Candidate.Version, state = e.Candidate.State.ToString(), failed = e.Result.Failed.Take(5).ToList() }).ToList(),
                });
                break;

            default:
                document.SupplierLayoutVersionId = null;
                document.PendingLayoutVersionId = null;
                break;
        }

        foreach (var shadow in result.ShadowHits)
        {
            await db.SupplierLayoutVersions.Where(v => v.Id == shadow.Candidate.VersionId)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Identified, v => v.Identified + 1), cancellationToken).ConfigureAwait(false);
        }

        flags.ReplaceForStep(document, stepId, Codes, raised);
    }

    /// <summary>Clears what a step decided (its flags and the layout it identified): the supplier changed, or the step does not apply any more.</summary>
    public void Clear(Document document, string stepId)
    {
        ArgumentNullException.ThrowIfNull(document);
        flags.ReplaceForStep(document, stepId, Codes, []);
    }

    /// <summary>Whether the type is fixed for identification (docs/11 §8.4 step 1): a human's, the source's (or a hint's) or the e-invoice XML's.</summary>
    public static bool IsTypeFixed(Document document) =>
        document.TypeSource is TypeSource.Human or TypeSource.Supplier or TypeSource.Structured && !document.Type.IsUnknown;
}
