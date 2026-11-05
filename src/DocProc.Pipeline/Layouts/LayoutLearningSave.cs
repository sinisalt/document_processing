using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Mapping.Identification;
using DocProc.Mapping.Layouts;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Pipeline.Layouts;

/// <summary>How a reviewed document is saved as a layout (docs/11 §12.6, §12.10, §18 <c>:learn-layout</c>).</summary>
public static class LearnSaveAs
{
    public const string NewLayout = "newLayout";
    public const string NewVersion = "newVersion";
}

/// <summary>
/// What <c>:learn-layout</c> (or <c>:approve {learnLayout}</c>) asks for: a new layout of the supplier or a new version
/// of <see cref="TargetLayoutId"/>, its key and name, the identification rule the reviewer marked (else one is
/// proposed, or for a new version the target's), the fields to learn (all with a value by default), the confirmation
/// that the target's current design will no longer be recognised, a pending Candidate to confirm against instead, and
/// whether the actor may activate (a <c>MappingAuthor</c>).
/// </summary>
public sealed record LearnLayoutCommand(
    string SaveAs,
    Guid? TargetLayoutId = null,
    string? LayoutKey = null,
    string? Name = null,
    string? IdentificationJson = null,
    IReadOnlyCollection<string>? Fields = null,
    bool ConfirmNotRecognised = false,
    Guid? PendingLayoutVersionId = null,
    bool MayActivate = false,
    int? Priority = null);

/// <summary>
/// What saving produced: the layout and version (added to the context, saved by the caller), the draft, the activation
/// (when the actor may activate), the pending siblings attached to a Candidate or matching an Active version, and the
/// documents of the target that its new rule no longer recognises. <see cref="Code"/> names a refusal.
/// </summary>
public sealed record LearnLayoutResult(
    string? Code,
    string? Problem,
    SupplierLayout? Layout = null,
    SupplierLayoutVersion? Version = null,
    LayoutDraft? Draft = null,
    ActivationOutcome? Activation = null,
    IReadOnlyList<Guid>? Siblings = null,
    IReadOnlyList<Guid>? NoLongerRecognised = null,
    IReadOnlyList<string>? Failed = null)
{
    public bool Saved => Code is null;

    public static LearnLayoutResult Refused(string code, string problem, IReadOnlyList<Guid>? noLonger = null, IReadOnlyList<string>? failed = null) =>
        new(code, problem, NoLongerRecognised: noLonger, Failed: failed);
}

/// <summary>The refusal codes of <see cref="LearnLayoutResult"/>.</summary>
public static class LearnCodes
{
    public const string NotLearnable = "NOT_LEARNABLE";
    public const string RuleInvalid = "RULE_INVALID";
    public const string RuleDoesNotHold = "RULE_DOES_NOT_HOLD";
    public const string NothingLearned = "NOTHING_LEARNED";
    public const string LayoutExists = "LAYOUT_EXISTS";
    public const string TargetNotFound = "TARGET_NOT_FOUND";
    public const string NotRecognised = "LAYOUT_NOT_RECOGNISED";
    public const string PendingNotFound = "PENDING_NOT_FOUND";
}

public sealed partial class LayoutLearningSave(LayoutLearning learning, Infrastructure.Persistence.DocProcDbContext db, TimeProvider clock)
{
    /// <summary>
    /// Saves the reviewed document as a layout (docs/11 §12.6, §12.10 step 6): version 1 of a new layout (the supplier's
    /// other layouts stay Active), or a new version of a layout that merges the learned fields into its mapping (and
    /// takes the rule, if edited; a rule that no longer holds on the layout's approved documents needs
    /// <see cref="LearnLayoutCommand.ConfirmNotRecognised"/>), or the document confirmed against a pending Candidate. The
    /// version is a Candidate; when the actor may activate, it is activated under the condition of §8.3 with the tenant's
    /// warm-up, else the pending siblings are attached and the <c>MappingAuthor</c>s notified. Nothing is saved here.
    /// </summary>
    public async Task<LearnLayoutResult> SaveAsync(Document document, LearnLayoutCommand command, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(command);
        if (document.SupplierId is not { } supplierId || await LayoutSettings.LoadAsync(db, document.TenantId, supplierId, cancellationToken).ConfigureAwait(false) is not { } setting)
        {
            return LearnLayoutResult.Refused(LearnCodes.NotLearnable, "The document has no supplier; assign one first: a layout is a supplier's.");
        }

        if (document.Type.IsUnknown)
        {
            return LearnLayoutResult.Refused(LearnCodes.NotLearnable, "The document's type is Unknown; set it first (:match-layout or :set-type).");
        }

        var now = clock.GetUtcNow();
        if (command.PendingLayoutVersionId is { } pendingId)
        {
            return await ConfirmPendingAsync(document, setting, pendingId, command, actor, cancellationToken).ConfigureAwait(false);
        }

        SupplierLayout? target = null;
        string? identification = command.IdentificationJson;
        if (command.SaveAs == LearnSaveAs.NewVersion)
        {
            target = command.TargetLayoutId is { } targetId
                ? await db.SupplierLayouts.Include(l => l.Versions).SingleOrDefaultAsync(l => l.Id == targetId && l.Scope == SupplierLayoutScope.Supplier && l.SupplierId == supplierId && l.Status == SupplierLayoutStatus.Active, cancellationToken).ConfigureAwait(false)
                : null;
            if (target is null)
            {
                return LearnLayoutResult.Refused(LearnCodes.TargetNotFound, "A new version names targetLayoutId, an Active layout of the document's supplier.");
            }

            if (target.DocumentTypeKey != document.Type.Value)
            {
                return LearnLayoutResult.Refused(LearnCodes.TargetNotFound, $"Layout {target.LayoutKey} is a {target.DocumentTypeKey} layout; the document is {document.Type}.");
            }

            identification ??= Current(target)?.IdentificationJson;
        }

        if (identification is not null)
        {
            var checkedRule = IdentificationRuleChecks.Check(identification, IdentificationScope.Layout, out var problems);
            if (checkedRule is null || problems.Count > 0)
            {
                return LearnLayoutResult.Refused(LearnCodes.RuleInvalid, string.Join(" ", problems.Select(p => $"{p.Path}: {p.Message}")));
            }

            identification = checkedRule.ToJson();
        }

        var (draft, problem) = await learning.DraftAsync(document, setting, identification, command.Fields, cancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            return LearnLayoutResult.Refused(LearnCodes.NotLearnable, problem!);
        }

        if (!draft.Evidence.Holds)
        {
            return LearnLayoutResult.Refused(LearnCodes.RuleDoesNotHold, $"The identification rule ({draft.Rule.Describe()}) does not hold on this document; a layout must recognise the document it is learned from.", failed: draft.Evidence.Failed);
        }

        var mappingJson = draft.Mapping;
        if (target is not null && Current(target) is { } current && JsonNode.Parse(current.MappingJson) is JsonObject merged)
        {
            // A new version merges only the learned fields into the layout's mapping (docs/11 §12.6).
            var fields = merged["fields"] as JsonObject ?? [];
            foreach (var (path, rule) in draft.Mapping["fields"]!.AsObject())
            {
                fields[path] = rule?.DeepClone();
            }

            merged["fields"] = fields;
            mappingJson = merged;
        }

        if (mappingJson["fields"] is not JsonObject { Count: > 0 })
        {
            return LearnLayoutResult.Refused(LearnCodes.NothingLearned, $"No field could be learned from the document: {string.Join("; ", draft.Skipped.Select(s => $"{s.Path}: {s.Reason}"))}");
        }

        var parsed = LayoutMapping.TryParse(mappingJson.ToJsonString(), out var mappingError);
        if (parsed is null)
        {
            return LearnLayoutResult.Refused(LearnCodes.NothingLearned, $"The learned mapping does not read: {mappingError}");
        }

        // "New version of X": the rule must still recognise X's documents, or the reviewer confirms it will not (§12.10 step 2).
        List<Guid> noLonger = [];
        if (target is not null)
        {
            noLonger = [.. (await learning.MatchingAsync(target, draft.Rule, "approved", holds: false, target.Id, LayoutLearning.DefaultReplayLastN, cancellationToken).ConfigureAwait(false)).Select(m => m.Document.Id)];
            if (noLonger.Count > 0 && !command.ConfirmNotRecognised)
            {
                return LearnLayoutResult.Refused(LearnCodes.NotRecognised, $"{target.LayoutKey}'s current design ({noLonger.Count} of its recent approved documents) would no longer be recognised; those documents would get NEW_LAYOUT. Save a new layout instead, or confirm with confirmNotRecognised.", noLonger);
            }
        }

        var origin = document.Flags.Any(f => f.Code == FlagCodes.NewLayout && f.ResolvedAt is null) ? SupplierLayoutOrigin.NewLayoutReview : SupplierLayoutOrigin.Review;
        var layout = target;
        if (layout is null)
        {
            var key = command.LayoutKey?.Trim() is { Length: > 0 } given ? given : await NextKeyAsync(setting.Supplier, document.Type.Value, cancellationToken).ConfigureAwait(false);
            if (!LayoutKeyPattern().IsMatch(key))
            {
                return LearnLayoutResult.Refused(LearnCodes.LayoutExists, $"'{key}' is not a layout key: lowercase letters, digits and dashes, at most {SupplierLayout.MaxKeyLength} characters.");
            }

            if (await db.SupplierLayouts.AnyAsync(l => l.Scope == SupplierLayoutScope.Supplier && l.OwnerId == supplierId && l.DocumentTypeKey == document.Type.Value && l.LayoutKey == key, cancellationToken).ConfigureAwait(false))
            {
                return LearnLayoutResult.Refused(LearnCodes.LayoutExists, $"The supplier already has a {document.Type} layout '{key}'; save a new version of it, or choose another key.");
            }

            layout = new SupplierLayout
            {
                Id = Guid.CreateVersion7(),
                TenantId = document.TenantId,
                Scope = SupplierLayoutScope.Supplier,
                SupplierId = supplierId,
                OwnerId = supplierId,
                DocumentTypeKey = document.Type.Value,
                LayoutKey = key,
                Name = Truncate(command.Name?.Trim() is { Length: > 0 } name ? name : $"{setting.Supplier.Name} {document.Type} ({key})", SupplierLayout.MaxNameLength),
                Priority = command.Priority ?? SupplierLayout.DefaultPriority,
                CreatedFromDocumentId = document.Id,
                CreatedBy = actor,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.SupplierLayouts.Add(layout);
        }

        var type = await learning.TypeOfAsync(document, null, cancellationToken).ConfigureAwait(false);
        var version = new SupplierLayoutVersion
        {
            Id = Guid.CreateVersion7(),
            TenantId = document.TenantId,
            LayoutId = layout.Id,
            Version = layout.Versions.Count == 0 ? 1 : layout.Versions.Max(v => v.Version) + 1,
            BuiltAgainstTypeVersionId = type?.VersionId,
            Status = SupplierLayoutVersionStatus.Candidate,
            Origin = target is null ? origin : SupplierLayoutOrigin.Review,
            IdentificationJson = draft.Rule.ToJson(),
            IdentificationHash = draft.Rule.Hash,
            MappingJson = mappingJson.ToJsonString(),
            CoverageJson = JsonSerializer.Serialize(parsed.Coverage),
            SourceDocumentIdsJson = JsonSerializer.Serialize(new[] { document.Id }),
            SimilarityProfileJson = draft.Profile.ToJson(),
            ReplayJson = noLonger.Count > 0 ? JsonSerializer.Serialize(new { confirmedNotRecognised = noLonger, confirmedBy = actor, at = now }) : null,
            CreatedBy = actor,
            CreatedAt = now,
        };
        layout.Versions.Add(version);
        if (target is not null)
        {
            db.SupplierLayoutVersions.Add(version);
            target.UpdatedAt = now;
        }

        return await FinishAsync(document, setting, layout, version, draft, command, actor, noLonger, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LearnLayoutResult> FinishAsync(Document document, LayoutSetting setting, SupplierLayout layout, SupplierLayoutVersion version, LayoutDraft? draft, LearnLayoutCommand command, string actor, IReadOnlyList<Guid> noLonger, CancellationToken cancellationToken)
    {
        var rule = IdentificationRule.Parse(version.IdentificationJson);
        var siblings = (await learning.MatchingAsync(layout, rule, "pending", holds: true, null, LayoutLearning.MaxMatching, cancellationToken).ConfigureAwait(false))
            .Select(m => m.Document.Id).Where(id => id != document.Id).ToList();
        ActivationOutcome? activation = null;
        if (command.MayActivate)
        {
            activation = await learning.ActivateAsync(layout, version, actor, null, cancellationToken).ConfigureAwait(false);
        }

        if (activation is not { Activated: true })
        {
            // A Candidate: the siblings wait for it (never reprocessed against it), and the MappingAuthors are told.
            document.PendingLayoutVersionId = version.Id;
            await learning.AttachAsync(layout, version, [.. siblings], cancellationToken).ConfigureAwait(false);
            await LayoutNotifications.UpsertAsync(db, layout, version, setting.Supplier.Name, siblings.Count + 1, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            document.PendingLayoutVersionId = null;
        }

        return new LearnLayoutResult(null, null, layout, version, draft, activation, siblings, noLonger);
    }

    /// <summary>"Confirm against pending layout" (docs/11 §12.10 step 2): the document joins the Candidate's replay set; no second layout is made.</summary>
    private async Task<LearnLayoutResult> ConfirmPendingAsync(Document document, LayoutSetting setting, Guid pendingId, LearnLayoutCommand command, string actor, CancellationToken cancellationToken)
    {
        var version = await db.SupplierLayoutVersions.Include(v => v.Layout).ThenInclude(l => l!.Versions)
            .SingleOrDefaultAsync(v => v.Id == pendingId && v.Status == SupplierLayoutVersionStatus.Candidate, cancellationToken).ConfigureAwait(false);
        if (version?.Layout is not { } layout || layout.OwnerId != setting.Supplier.Id || layout.DocumentTypeKey != document.Type.Value)
        {
            return LearnLayoutResult.Refused(LearnCodes.PendingNotFound, $"No Candidate layout version {pendingId} of the document's supplier and type.");
        }

        var rule = IdentificationRule.Parse(version.IdentificationJson);
        var words = await learning.WordsAsync(document, cancellationToken).ConfigureAwait(false);
        var holds = IdentificationRuleEvaluator.Evaluate(rule, new IdentificationContext(words, setting.Values, LayoutSettings.SourceOf(document)));
        if (!holds.Holds)
        {
            return LearnLayoutResult.Refused(LearnCodes.RuleDoesNotHold, $"The pending layout {layout.LayoutKey}'s rule does not hold on this document.", failed: holds.Failed);
        }

        var sources = LayoutLearning.SourceIds(version);
        if (!sources.Contains(document.Id))
        {
            sources.Add(document.Id);
            version.SourceDocumentIdsJson = JsonSerializer.Serialize(sources);
        }

        return await FinishAsync(document, setting, layout, version, null, command, actor, [], cancellationToken).ConfigureAwait(false);
    }

    private static SupplierLayoutVersion? Current(SupplierLayout layout) =>
        layout.Versions.FirstOrDefault(v => v.Version == layout.ActiveVersion) ?? layout.Versions.OrderByDescending(v => v.Version).FirstOrDefault();

    /// <summary>A free key for the supplier's next layout of the type: <c>nordwind-invoice-2</c>, <c>-3</c>, ….</summary>
    private async Task<string> NextKeyAsync(Supplier supplier, string type, CancellationToken cancellationToken)
    {
        var stem = Slug($"{supplier.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "supplier"}-{type}");
        var taken = await db.SupplierLayouts.AsNoTracking().Where(l => l.OwnerId == supplier.Id && l.DocumentTypeKey == type).Select(l => l.LayoutKey).ToListAsync(cancellationToken).ConfigureAwait(false);
        for (var n = taken.Count + 1; ; n++)
        {
            var key = $"{stem}-{n}";
            if (!taken.Contains(key, StringComparer.Ordinal))
            {
                return key;
            }
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static string Slug(string text)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var c in text.Normalize(System.Text.NormalizationForm.FormD).ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-' && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "layout" : slug[..Math.Min(slug.Length, 50)].Trim('-');
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex LayoutKeyPattern();
}

/// <summary>
/// The tenant's layout warm-up applied to the versions it set (docs/11 §8.3, the settled P4 demo): when a tenant admin
/// changes <c>layoutReviewWarmup</c>, every Active version whose warm-up is still the tenant's old one takes the new one,
/// so lowering it to 0 lets the next document of a version inside its warm-up auto-approve; a version an author raised
/// keeps its own.
/// </summary>
public static class LayoutWarmups
{
    public static Task<int> ApplyTenantWarmupAsync(Infrastructure.Persistence.DocProcDbContext db, Guid tenantId, int previous, int next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return previous == next
            ? Task.FromResult(0)
            : db.SupplierLayoutVersions
                .Where(v => v.TenantId == tenantId && v.Status == SupplierLayoutVersionStatus.Active && v.ReviewWarmup == previous)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.ReviewWarmup, next), cancellationToken);
    }
}
