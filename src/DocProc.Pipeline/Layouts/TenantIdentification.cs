using System.Text.Json;
using DocProc.Application.Configuration;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Mapping.Identification;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Pipeline.Layouts;

/// <summary>How the stored columns of a tenant fallback rule version read.</summary>
public static class TenantIdentificationJson
{
    /// <summary>The suppliers a version excludes (<c>ExcludedSupplierIdsJson</c>, a JSON array of ids); empty when none or unreadable.</summary>
    public static IReadOnlyCollection<Guid> Excluded(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// The tenant fallback identification rules in the pipeline (docs/11 §8.5, §9.4 step 2, P5): the Active rule versions
/// of the tenant (with <c>supplierLayouts</c> on), evaluated by <see cref="TypeIdentificationRuleSet"/> on a document's
/// words and source facts, in <c>classify.identification</c> after the supplier's own layouts and in the split
/// prefilter. A rule that decides sets the type (<see cref="TypeSource.Identification"/>) and pins its version on the
/// document (<c>IdentificationRuleVersionId</c>), with a timeline event naming the rule and its evidence, and counts in
/// the rule's <c>Matched</c>.
/// </summary>
internal sealed class TenantIdentification(DocProcDbContext db, ITenantFeatures features, DocumentTimeline timeline)
{
    /// <summary>A tenant fallback rule decided (or, under <c>NEW_LAYOUT</c>, proposed) the document's type.</summary>
    public const string IdentifiedEvent = LayoutEventTypes.TypeIdentifiedByRule;

    /// <summary>The Active rule versions of the tenant; empty when <c>supplierLayouts</c> is off or there are none.</summary>
    public async Task<IReadOnlyList<TenantRuleCandidate>> LoadAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (!await features.IsEnabledAsync(tenantId, TenantFeature.SupplierLayouts, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var rows = await db.TypeIdentificationRuleVersions.AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.State == TypeIdentificationRuleVersionState.Active
                        && v.Rule!.Status == TypeIdentificationRuleStatus.Active && v.Rule.ActiveVersion == v.Version)
            .Select(v => new { v.Id, v.RuleId, v.Version, v.RuleJson, v.ExcludedSupplierIdsJson, v.Rule!.Name, v.Rule.DocumentTypeKey, v.Rule.Priority })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var candidates = new List<TenantRuleCandidate>();
        foreach (var row in rows.OrderBy(r => r.DocumentTypeKey, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            // A version saved outside the API that does not read (or reads master data) is not run.
            if (IdentificationRuleChecks.Check(row.RuleJson, IdentificationScope.Tenant, out _) is not { } rule)
            {
                continue;
            }

            candidates.Add(new TenantRuleCandidate(row.RuleId, row.Id, row.Version, row.Name, row.DocumentTypeKey, row.Priority, rule, ExcludedOf(row.ExcludedSupplierIdsJson)));
        }

        return candidates;
    }

    /// <summary>The suppliers a version excludes, from its JSON array of ids.</summary>
    public static IReadOnlyCollection<Guid> ExcludedOf(string? json) => TenantIdentificationJson.Excluded(json);

    /// <summary>The tenant rules evaluated on the document; null when the tenant has none.</summary>
    public Task<TenantRuleResult?> ResolveAsync(Document document, DocumentWords words, CancellationToken cancellationToken) =>
        ResolveAsync(document, () => Task.FromResult(words), cancellationToken);

    /// <summary>As above, reading the document's words only when the tenant has rules.</summary>
    public async Task<TenantRuleResult?> ResolveAsync(Document document, Func<Task<DocumentWords>> words, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(words);
        var rules = await LoadAsync(document.TenantId, cancellationToken).ConfigureAwait(false);
        return rules.Count == 0 ? null : TypeIdentificationRuleSet.Resolve(rules, await words().ConfigureAwait(false), LayoutSettings.SourceOf(document), document.SupplierId);
    }

    /// <summary>
    /// Undoes a tenant rule's decision that something better placed took over (docs/11 §6.2, §8.5, Correction (P5)
    /// fix1): the supplier Match found has layouts of its own that identified the document or raised <c>NEW_LAYOUT</c>,
    /// or the supplier the rule decided for was replaced. The pin is cleared and the rule's <c>Matched</c> count taken
    /// back, so the rule neither names a document its supplier's layout decided nor counts it. True when a pin was cleared.
    /// </summary>
    public async Task<bool> ReleaseAsync(Document document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.IdentificationRuleVersionId is not { } pinned)
        {
            return false;
        }

        document.IdentificationRuleVersionId = null;
        var ruleId = await db.TypeIdentificationRuleVersions.AsNoTracking().Where(v => v.Id == pinned).Select(v => (Guid?)v.RuleId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (ruleId is { } id)
        {
            await db.TypeIdentificationRules.Where(r => r.Id == id && r.Matched > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Matched, r => r.Matched - 1), cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Records a tenant rule's decision (<paramref name="decided"/>: the version is pinned and counted) or proposal (under
    /// <c>NEW_LAYOUT</c>: an event only), with the rule, its version and the evidence; rules of two types that held are
    /// recorded as a conflict that decided nothing.
    /// </summary>
    public async Task RecordAsync(Document document, TenantRuleResult result, bool decided, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Resolution == TenantRuleResolution.Conflicting)
        {
            timeline.Record(document, IdentifiedEvent, null, null, new
            {
                stepId = LayoutIdentification.ClassifyStep,
                scope = "Tenant",
                resolution = "conflicting",
                rules = result.Holding.Select(h => new { h.Candidate.RuleId, h.Candidate.Name, type = h.Candidate.DocumentTypeKey }).ToList(),
            });
            return;
        }

        if (result.Selected is not { } selected)
        {
            return;
        }

        if (decided)
        {
            document.IdentificationRuleVersionId = selected.Candidate.VersionId;
            await db.TypeIdentificationRules.Where(r => r.Id == selected.Candidate.RuleId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Matched, r => r.Matched + 1), cancellationToken).ConfigureAwait(false);
        }

        timeline.Record(document, IdentifiedEvent, null, null, new
        {
            stepId = LayoutIdentification.ClassifyStep,
            scope = "Tenant",
            resolution = decided ? "identified" : "proposed",
            ruleId = selected.Candidate.RuleId,
            ruleVersionId = selected.Candidate.VersionId,
            version = selected.Candidate.Version,
            name = selected.Candidate.Name,
            ruleHash = selected.Candidate.Rule.Hash,
            type = selected.Candidate.DocumentTypeKey,
            evidence = selected.Result.Evidence.Where(e => e.Positive).Select(e => new { e.Path, e.Kind, e.Description, e.Page, box = e.Box is { } b ? new[] { b.X0, b.Y0, b.X1, b.Y1 } : null, e.Text }).ToList(),
        });
    }
}
