using DocProc.Application.Suppliers;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Mapping.Identification;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Pipeline.Layouts;

/// <summary>What the layout condition of the review gate decided (docs/11 §11 layer 3).</summary>
/// <param name="Passes">Whether the condition lets the document auto-approve.</param>
/// <param name="Reason">Why not, for the review task (<c>LAYOUT_WARMUP</c>, <c>LAYOUT_NOT_ACTIVE</c>, ...).</param>
/// <param name="Warmup">The flag that says the document is inside the version's warm-up, or null.</param>
internal sealed record LayoutGateResult(bool Passes, string? Reason = null, DocumentFlag? Warmup = null)
{
    public static LayoutGateResult Pass { get; } = new(true);
}

/// <summary>
/// The layout condition of the review gate (docs/11 §11): for a layout-managed supplier (an Active layout of its own or
/// of its group, or <c>layouts.required</c>), a document auto-approves only when an identification rule selected its
/// layout (<c>SupplierLayoutVersionId</c>), the version is Active (not Degraded or Retired), and the document is past the
/// version's review warm-up. The first <c>ReviewWarmup</c> documents under a version (5 by default, decision 2 of §24,
/// <c>TenantSettings.LayoutReviewWarmup</c>) go to review with <c>LAYOUT_WARMUP</c> ("warm-up 1 of 5"); each document is
/// counted once, also when it is validated again. An e-invoice needs no layout; a layout restricted to e-invoices that
/// identified one must be Active and past its warm-up. A tenant without <c>supplierLayouts</c> is never held here.
/// </summary>
internal sealed class LayoutReviewGate(DocProcDbContext db, LayoutIdentification identification, DocumentTimeline timeline)
{
    public async Task<LayoutGateResult> EvaluateAsync(Document document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SupplierId is not { } supplierId || !await identification.EnabledAsync(document.TenantId, cancellationToken).ConfigureAwait(false))
        {
            return LayoutGateResult.Pass;
        }

        if (document.SupplierLayoutVersionId is not { } versionId)
        {
            if (string.Equals(LayoutIdentification.ItemKindOf(document), LayoutResolver.EInvoiceItemKind, StringComparison.Ordinal))
            {
                return LayoutGateResult.Pass;
            }

            return await LayoutManagedAsync(document.TenantId, supplierId, cancellationToken).ConfigureAwait(false)
                ? new LayoutGateResult(false, "LAYOUT_NOT_IDENTIFIED")
                : LayoutGateResult.Pass;
        }

        var version = await db.SupplierLayoutVersions.AsNoTracking().Include(v => v.Layout).SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken).ConfigureAwait(false);
        if (version is null || version.Status != SupplierLayoutVersionStatus.Active || version.Layout?.Status != SupplierLayoutStatus.Active)
        {
            return new LayoutGateResult(false, "LAYOUT_NOT_ACTIVE");
        }

        var ordinal = await WarmupOrdinalAsync(document, version, cancellationToken).ConfigureAwait(false);
        if (ordinal is not { } n)
        {
            return LayoutGateResult.Pass;
        }

        var flag = FlagCodes.Create(
            FlagCodes.LayoutWarmup,
            $"Document {n} of the first {version.ReviewWarmup} under layout {version.Layout.LayoutKey} version {version.Version}: it goes to review (the layout's warm-up), already filled by the layout.",
            parameters: FlagParams.Of(
                ("layoutKey", version.Layout.LayoutKey),
                ("version", FlagParams.Number(version.Version)),
                ("ordinal", FlagParams.Number(n)),
                ("warmup", FlagParams.Number(version.ReviewWarmup))));
        return new LayoutGateResult(false, FlagCodes.LayoutWarmup, flag);
    }

    /// <summary>
    /// The document's place in the version's warm-up (1-based), or null when it is past it: a document counted before
    /// keeps its place; otherwise it takes the next one while places are left (an atomic increment, so two documents
    /// validated at once never take the same place, and none over the warm-up).
    /// </summary>
    private async Task<int?> WarmupOrdinalAsync(Document document, SupplierLayoutVersion version, CancellationToken cancellationToken)
    {
        // DetailsJson is a json column, on which SQL Server refuses LIKE: the document's few warm-up events are read and
        // the version is looked for here.
        var marker = $"\"versionId\":\"{version.Id}\"";
        var warmups = await db.DocumentEvents.AsNoTracking()
            .Where(e => e.DocumentId == document.Id && e.EventType == LayoutEventTypes.Warmup && e.DetailsJson != null)
            .Select(e => e.DetailsJson)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var counted = warmups.FirstOrDefault(d => d!.Replace(" ", string.Empty, StringComparison.Ordinal).Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (counted is not null)
        {
            return ReadOrdinal(counted);
        }

        if (version.ReviewWarmup <= 0)
        {
            return null;
        }

        var taken = await db.SupplierLayoutVersions
            .Where(v => v.Id == version.Id && v.WarmupSeen < v.ReviewWarmup)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.WarmupSeen, v => v.WarmupSeen + 1), cancellationToken)
            .ConfigureAwait(false);
        if (taken == 0)
        {
            return null;
        }

        var seen = await db.SupplierLayoutVersions.AsNoTracking().Where(v => v.Id == version.Id).Select(v => v.WarmupSeen).SingleAsync(cancellationToken).ConfigureAwait(false);
        var ordinal = Math.Min(seen, version.ReviewWarmup);
        timeline.Record(document, LayoutEventTypes.Warmup, null, null, new { versionId = version.Id, layoutKey = version.Layout!.LayoutKey, version = version.Version, ordinal, warmup = version.ReviewWarmup });
        return ordinal;
    }

    private static int? ReadOrdinal(string details)
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(details);
            return json.RootElement.TryGetProperty("ordinal", out var o) && o.TryGetInt32(out var n) ? n : 1;
        }
        catch (System.Text.Json.JsonException)
        {
            return 1;
        }
    }

    /// <summary>Whether the supplier is layout-managed (docs/11 §8.4 step 5): an Active layout of its own or of its group, or <c>layouts.required</c>.</summary>
    private async Task<bool> LayoutManagedAsync(Guid tenantId, Guid supplierId, CancellationToken cancellationToken)
    {
        var supplier = await db.Suppliers.AsNoTracking().Where(s => s.Id == supplierId).Select(s => new { s.GroupId, s.SettingsJson }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (supplier is null)
        {
            return false;
        }

        var active = await db.SupplierLayouts.AsNoTracking()
            .AnyAsync(l => l.Status == SupplierLayoutStatus.Active && l.ActiveVersion != null
                           && ((l.Scope == SupplierLayoutScope.Supplier && l.SupplierId == supplierId) || (l.Scope == SupplierLayoutScope.Group && supplier.GroupId != null && l.GroupId == supplier.GroupId)), cancellationToken)
            .ConfigureAwait(false);
        if (active)
        {
            return true;
        }

        var group = supplier.GroupId is { } groupId
            ? await db.SupplierGroups.AsNoTracking().Where(g => g.Id == groupId).Select(g => g.SettingsJson).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var tenantDefault = (await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId, cancellationToken).ConfigureAwait(false)).Settings.LayoutsRequiredByDefault;
        return SupplierSettings.Parse(supplier.SettingsJson).Over(SupplierSettings.Parse(group)).LayoutsRequired(tenantDefault);
    }
}
