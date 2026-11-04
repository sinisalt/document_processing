using System.Security.Claims;
using System.Text.Json;
using DocProc.Application.Routing;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using DocProc.Mapping.Identification;
using DocProc.Mapping.Layouts;
using DocProc.Mapping.Learning;
using DocProc.Pipeline.Layouts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

/// <summary>A field the learner turned into rules: its strategies in order, the label and value pattern, and whether the rules read the confirmed value back.</summary>
public sealed record LearnedFieldDto(string Path, IReadOnlyList<string> Strategies, string? Label, string? Pattern, bool Verified);

/// <summary>A field the learner left out, and why.</summary>
public sealed record LearnSkipDto(string Path, string Reason);

/// <summary>A source rule <c>:learn-layout</c> proposes from the document's ingestion (docs/11 §12.6): the conditions (as <c>remember</c> names them), a sentence, and whether the supplier has it already.</summary>
public sealed record ProposedSourceRuleDto(IReadOnlyList<string> Conditions, string Describe, bool Exists);

/// <summary>
/// The answer of <c>:learn-layout</c> (docs/11 §12.6, §12.10): the layout with the version saved and its status
/// (Active when a <c>MappingAuthor</c> saved it and the activation condition held, else Candidate), the rule (proposed or
/// marked) and its sentence, the learned fields and the ones left out, the replay, why the version was not activated,
/// the pending siblings (attached to a Candidate, or to reprocess for an Active version), the documents of the target
/// layout its new rule no longer recognises, the proposed source rules and the source rule saved with it.
/// </summary>
public sealed record LearnLayoutResponse(
    SupplierLayoutResponse Layout,
    int Version,
    string Status,
    bool RuleProposed,
    string Describe,
    JsonElement Identification,
    IReadOnlyList<LearnedFieldDto> Fields,
    IReadOnlyList<LearnSkipDto> Skipped,
    ReplayReport? Replay,
    string? NotActivated,
    IReadOnlyList<Guid> Siblings,
    IReadOnlyList<Guid> NoLongerRecognised,
    IReadOnlyList<ProposedSourceRuleDto> ProposedSourceRules,
    Guid? SourceRuleId,
    IReadOnlyList<string> Notes);

/// <summary><c>POST /documents/{id}/identifiers:propose</c> (docs/11 §12.9 step 2): the page and either the box drawn or the words picked (indexes in the page's text layer), and the scope.</summary>
public sealed record ProposeIdentifierRequest(int Page, SourceBox? BBox = null, IReadOnlyList<int>? Words = null, string? Scope = null);

/// <summary>The proposed identifier, its sentence, whether it holds on the document, and warnings for the Identification card.</summary>
public sealed record ProposeIdentifierResponse(JsonElement Identifier, string Describe, bool Holds, IReadOnlyList<string> Warnings);

/// <summary>
/// <c>POST /documents/{id}:match-layout</c> (docs/11 §12.10 step 3): the type of a <c>NEW_LAYOUT</c> document, a layout
/// whose labels seed the pre-fill, how it will be saved, and a pending Candidate to confirm against.
/// </summary>
public sealed record MatchLayoutRequest(string TypeKey, Guid? BasedOnLayoutId = null, string? SaveAs = null, Guid? TargetLayoutId = null, Guid? PendingLayoutVersionId = null);

/// <summary><c>POST /supplier-layouts/{id}/versions</c>: a new version written by hand, its rule and field mapping (saved as a Candidate).</summary>
public sealed record CreateLayoutVersionRequest(JsonElement Identification, JsonElement Mapping);

/// <summary><c>…/versions/{v}:replay</c>: more documents for the replay set, and how many of the supplier's approved documents of the type (50 by default).</summary>
public sealed record ReplayLayoutRequest(IReadOnlyList<Guid>? DocumentIds = null, int? LastN = null);

/// <summary>A document a version's rule holds on (or, for the "no longer recognised" check, does not hold on).</summary>
public sealed record MatchingDocumentDto(Guid DocumentId, string? FileName, string Status, string TypeKey, Guid? SupplierLayoutVersionId, Guid? PendingLayoutVersionId, bool Holds, IReadOnlyList<string> Failed);

/// <summary><c>POST /documents:bulk-reprocess</c> (docs/11 §15.5, §12.10 step 7): the documents, the stage and the configuration mode.</summary>
public sealed record BulkReprocessRequest(IReadOnlyList<Guid> DocumentIds, string? FromStage = null, string? ConfigMode = null);

/// <summary>One document of a bulk reprocess: queued, or why not.</summary>
public sealed record BulkReprocessItem(Guid DocumentId, bool Queued, string? Problem);

/// <summary>A notification (docs/11 §12.10 step 6): documents waiting for a Candidate layout.</summary>
public sealed record NotificationDto(Guid Id, string Audience, string Kind, string Title, string Message, string? EntityType, Guid? EntityId, int Count, JsonElement? Payload, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ResolvedAt);

/// <summary>
/// Learning layouts in review and their lifecycle (docs/11 §8.3, §12.6, §12.9, §12.10, §18), behind
/// <c>supplierLayouts</c>: <c>:learn-layout</c> (also part of <c>:approve</c>), <c>identifiers:propose</c>,
/// <c>:match-layout</c>, new versions, the replay, the documents a version's rule holds on (the sibling re-test), bulk
/// reprocessing and the notifications of the <c>MappingAuthor</c>s.
/// </summary>
public static class LayoutLearningEndpoints
{
    public const int MaxBulkDocuments = 200;

    public static RouteGroupBuilder MapLayoutLearning(this RouteGroupBuilder group)
    {
        group.MapPost("/documents/{id:guid}:learn-layout", LearnLayoutAsync).RequireAuthorization(Policies.Review).WithTags("Layouts").WithName("LearnLayout")
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024))
            .WithSummary("Saves the reviewed document as a layout (docs/11 §12.6): its identification rule (marked, or proposed) and the field mapping AnchorLearner derives from its confirmed values. A MappingAuthor's save is activated under the condition of §8.3 with the tenant's warm-up; anyone else's is a Candidate the MappingAuthors are notified of.");
        group.MapPost("/documents/{id:guid}/identifiers:propose", ProposeIdentifierAsync).RequireAuthorization(Policies.Review).WithTags("Layouts").WithName("ProposeIdentifier")
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .WithSummary("Turns the words picked or the box drawn on a page into an identifier (docs/11 §12.9): a title-area word becomes {text, page, region: title}, other words a bBox, the supplier's VAT id or IBAN supplierValue (never in tenant scope).");
        group.MapPost("/documents/{id:guid}:match-layout", MatchLayoutAsync).RequireAuthorization(Policies.Review).WithTags("Layouts").WithName("MatchLayout")
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .WithSummary("Sets the type of a NEW_LAYOUT document (TypeSource Human, a setType correction) and records the basis of its pre-fill (docs/11 §12.10 step 3); the layout-prefill step runs again only when the type or the basis changes (202).");
        group.MapPost("/supplier-layouts/{layoutId:guid}/versions", CreateVersionAsync).RequireAuthorization(Policies.ConfigAdmin).WithTags("Layouts").WithName("CreateSupplierLayoutVersion")
            .WithMetadata(new RequestSizeLimitAttribute(256 * 1024))
            .WithSummary("Adds a version written by hand to a layout: its identification rule and field mapping, saved as a Candidate.");
        group.MapPost("/supplier-layouts/{layoutId:guid}/versions/{version:int}:replay", ReplayAsync).RequireAuthorization(Policies.Review).WithTags("Layouts").WithName("ReplaySupplierLayoutVersion")
            .WithSummary("Runs the dry-run replay of docs/11 §8.3: the identification test and the field replay over the replay set, stored on the version.");
        group.MapGet("/supplier-layouts/{layoutId:guid}/versions/{version:int}/matching-documents", MatchingDocumentsAsync).RequireAuthorization(Policies.Read).WithTags("Layouts").WithName("GetLayoutMatchingDocuments")
            .WithSummary("The supplier's documents the version's rule holds on (?state=pending: in review with NEW_LAYOUT, the sibling re-test; approved: approved ones; ?holds=false: those it does not hold on).");
        group.MapPost("/documents:bulk-reprocess", BulkReprocessAsync).RequireAuthorization(Policies.Review).WithTags("Documents").WithName("BulkReprocessDocuments")
            .WithMetadata(new RequestSizeLimitAttribute(32 * 1024))
            .WithSummary("Sends up to 200 documents back through the pipeline from a stage (docs/11 §15.5), for example the siblings of a new layout from Classify with configMode current.");
        group.MapGet("/notifications", ListNotificationsAsync).RequireAuthorization(Policies.Read).WithTags("Notifications").WithName("ListNotifications")
            .WithSummary("The open notifications for the permissions the caller holds (MappingAuthor: Candidate layouts with documents waiting); ?all=true includes resolved ones.");
        group.MapPost("/notifications/{id:guid}:dismiss", DismissNotificationAsync).RequireAuthorization(Policies.Read).WithTags("Notifications").WithName("DismissNotification")
            .WithSummary("Resolves a notification the caller may see.");
        return group;
    }

    /// <summary>
    /// <c>:learn-layout</c>, and the <c>learnLayout</c> of <c>:approve</c>: loads the document tracked, saves the layout
    /// (<see cref="LayoutLearningSave"/>), the source rule asked for, an event and, with <paramref name="resolveFlag"/>,
    /// the resolution of <c>NEW_LAYOUT</c> as learned. Saves the context; the caller's transaction commits it.
    /// </summary>
    internal static async Task<(LearnLayoutResponse? Response, ProblemHttpResult? Problem)> LearnAsync(
        DocProcDbContext db,
        IContainerBlobStore blobs,
        IEffectiveConfigResolver configs,
        TimeProvider clock,
        Guid id,
        LearnLayoutRequest request,
        ClaimsPrincipal user,
        string actor,
        CancellationToken ct,
        bool resolveFlag = true)
    {
        var document = await db.Documents.Include(d => d.Flags).Include(d => d.IngestionItem).ThenInclude(i => i!.Ingestion).SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
        {
            return (null, TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: $"Document {id} does not exist."));
        }

        if (document.Status is not (DocumentStatus.NeedsReview or DocumentStatus.Validated or DocumentStatus.Approved or DocumentStatus.AutoApproved))
        {
            return (null, TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Document not reviewed", detail: $"Document {id} is {document.Status}; a layout is learned from a document in review or approved."));
        }

        var saveAs = string.IsNullOrWhiteSpace(request.SaveAs) ? LearnSaveAs.NewLayout : request.SaveAs.Trim();
        if (saveAs is not (LearnSaveAs.NewLayout or LearnSaveAs.NewVersion))
        {
            return (null, TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid saveAs", detail: "saveAs is newLayout or newVersion."));
        }

        if (request.Identification is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) })
        {
            return (null, TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid identification", detail: "identification is an identification rule object (all, any, none)."));
        }

        if (request.Fields is { Count: > LayoutMapping.MaxFields })
        {
            return (null, TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Too many fields", detail: $"At most {LayoutMapping.MaxFields} fields."));
        }

        var learning = new LayoutLearning(db, blobs, configs, clock);
        var command = new LearnLayoutCommand(
            saveAs,
            request.TargetLayoutId,
            request.LayoutKey,
            request.Name,
            request.Identification is { ValueKind: JsonValueKind.Object } rule ? rule.GetRawText() : null,
            request.Fields,
            request.ConfirmNotRecognised == true,
            request.PendingLayoutVersionId,
            MayActivate: Policies.Satisfies(user, Policies.MappingAuthor),
            request.Priority);
        var result = await new LayoutLearningSave(learning, db, clock).SaveAsync(document, command, actor, ct);
        if (!result.Saved)
        {
            var status = result.Code switch
            {
                LearnCodes.RuleInvalid => StatusCodes.Status400BadRequest,
                LearnCodes.TargetNotFound or LearnCodes.PendingNotFound => StatusCodes.Status404NotFound,
                LearnCodes.RuleDoesNotHold or LearnCodes.NothingLearned => StatusCodes.Status422UnprocessableEntity,
                _ => StatusCodes.Status409Conflict,
            };
            return (null, TypedResults.Problem(
                statusCode: status,
                title: "Layout not saved",
                detail: result.Problem,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = result.Code, ["documentIds"] = result.NoLongerRecognised, ["failed"] = result.Failed }));
        }

        var now = clock.GetUtcNow();
        Guid? sourceRuleId = null;
        if (request.SourceRules is { } remember && await db.Suppliers.SingleOrDefaultAsync(s => s.Id == document.SupplierId, ct) is { } supplier)
        {
            var remembered = await DocumentEndpoints.RememberAsync(db, document, supplier, remember, user, actor, now, ct);
            if (remembered.Problem is { } problem)
            {
                return (null, problem);
            }

            if (remembered.Invalid is not null)
            {
                return (null, TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid source rule", detail: "sourceRules names conditions the document's ingestion does not have (sender, senderDomain, recipient, folder, channel)."));
            }

            sourceRuleId = remembered.Rule?.Id;
        }

        if (resolveFlag)
        {
            Resolve(document, "learned", actor, now, result.Layout!.LayoutKey);
        }

        DocumentQueries.AddEvent(db, document, "document.layout_learned", null, actor, new
        {
            layoutId = result.Layout!.Id,
            layoutKey = result.Layout.LayoutKey,
            version = result.Version!.Version,
            status = result.Version.Status.ToString(),
            origin = result.Version.Origin.ToString(),
            confirmedNotRecognised = result.NoLongerRecognised?.Count > 0 ? result.NoLongerRecognised : null,
        }, now);
        await db.SaveChangesAsync(ct);

        var proposed = await ProposedSourceRulesAsync(db, document, ct);
        var draft = result.Draft;
        var response = new LearnLayoutResponse(
            LayoutEndpoints.Response(result.Layout),
            result.Version.Version,
            result.Version.Status.ToString(),
            draft?.RuleProposed ?? false,
            IdentificationRule.Parse(result.Version.IdentificationJson).Describe(),
            LayoutEndpoints.Element(result.Version.IdentificationJson) ?? default,
            [.. (draft?.Fields ?? []).Select(f => new LearnedFieldDto(f.Path, f.Strategies, f.Label, f.Pattern, f.Verified))],
            [.. (draft?.Skipped ?? []).Select(s => new LearnSkipDto(s.Path, s.Reason))],
            result.Activation?.Replay,
            result.Activation is { Activated: false } refused ? refused.Refusal : result.Activation is null ? "Saved as a Candidate: a MappingAuthor activates it." : null,
            result.Siblings ?? [],
            result.NoLongerRecognised ?? [],
            proposed,
            sourceRuleId,
            draft?.Notes ?? []);
        return (response, null);
    }

    /// <summary>Resolves the open <c>NEW_LAYOUT</c> of a document, with how (<c>learned</c>, <c>approvedWithoutLayout</c>); saved by the caller.</summary>
    internal static async Task ResolveNewLayoutAsync(DocProcDbContext db, Guid documentId, string resolution, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var flags = await db.DocumentFlags.Where(f => f.DocumentId == documentId && f.Code == FlagCodes.NewLayout && f.ResolvedAt == null).ToListAsync(ct);
        foreach (var flag in flags)
        {
            ResolveFlag(flag, resolution, actor, now, null);
        }
    }

    private static void Resolve(Document document, string resolution, string actor, DateTimeOffset now, string? layoutKey)
    {
        foreach (var flag in document.Flags.Where(f => f.Code == FlagCodes.NewLayout && f.ResolvedAt is null))
        {
            ResolveFlag(flag, resolution, actor, now, layoutKey);
        }
    }

    private static void ResolveFlag(DocumentFlagRecord flag, string resolution, string actor, DateTimeOffset now, string? layoutKey)
    {
        flag.ResolvedAt = now;
        flag.ResolvedBy = actor;
        var parameters = new Dictionary<string, string>(flag.Params ?? new Dictionary<string, string>(), StringComparer.Ordinal) { ["resolution"] = resolution };
        if (layoutKey is not null)
        {
            parameters["layoutKey"] = layoutKey;
        }

        flag.Params = parameters;
    }

    /// <summary>The source rules the document's ingestion offers (docs/11 §12.6 "it also proposes source rules"), and whether the supplier has each.</summary>
    private static async Task<List<ProposedSourceRuleDto>> ProposedSourceRulesAsync(DocProcDbContext db, Document document, CancellationToken ct)
    {
        var ingestion = document.IngestionItem?.Ingestion;
        var supplierName = await db.Suppliers.AsNoTracking().Where(s => s.Id == document.SupplierId).Select(s => s.Name).SingleOrDefaultAsync(ct) ?? "the supplier";
        var rules = await db.SupplierSourceRules.AsNoTracking().Where(r => r.SupplierId == document.SupplierId).ToListAsync(ct);
        var proposals = new List<ProposedSourceRuleDto>();
        var sender = SourceConditions.Address(ingestion?.SourceSender);
        if (sender is not null)
        {
            proposals.Add(new ProposedSourceRuleDto(["sender"], $"Mails from {sender} are {supplierName}.", rules.Any(r => r.Sender == sender)));
            if (SourceConditions.DomainOf(ingestion?.SourceSender) is { } domain)
            {
                proposals.Add(new ProposedSourceRuleDto(["senderDomain"], $"Mails from anyone at {domain} are {supplierName}.", rules.Any(r => r.SenderDomain == domain)));
            }
        }

        if (!string.IsNullOrEmpty(ingestion?.SourceFolderPath))
        {
            var folder = SourceConditions.Folder(ingestion.SourceFolderPath);
            proposals.Add(new ProposedSourceRuleDto(["folder"], $"Files in {ingestion.SourceFolderPath} are {supplierName}'s {document.Type}.", rules.Any(r => r.FolderPrefix == folder)));
        }

        if (ingestion?.SourceMailbox is { Length: > 0 } mailbox)
        {
            proposals.Add(new ProposedSourceRuleDto(["recipient"], $"Mails to {mailbox} are {supplierName}.", rules.Any(r => r.Recipient == SourceConditions.Address(mailbox))));
        }

        return proposals;
    }

    private static async Task<Results<Ok<LearnLayoutResponse>, ProblemHttpResult>> LearnLayoutAsync(
        Guid id,
        LearnLayoutRequest request,
        ITenantContext tenant,
        ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IContainerBlobStore blobs,
        [FromServices] IEffectiveConfigResolver configs,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await LayoutEndpoints.GuardAsync(tenant, features, "Layouts", ct) is { } problem)
        {
            return problem;
        }

        var (response, refused) = await LearnAsync(db, blobs, configs, clock, id, request, user, DocumentQueries.ActorName(user), ct, resolveFlag: false);
        return refused is not null ? refused : TypedResults.Ok(response!);
    }

    private static async Task<Results<Ok<ProposeIdentifierResponse>, ProblemHttpResult>> ProposeIdentifierAsync(
        Guid id,
        ProposeIdentifierRequest request,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IContainerBlobStore blobs,
        CancellationToken ct)
    {
        if (await LayoutEndpoints.GuardAsync(tenant, features, "Identifier proposals", ct) is { } problem)
        {
            return problem;
        }

        var scope = string.IsNullOrWhiteSpace(request.Scope) ? "layout" : request.Scope.Trim();
        if (scope is not ("layout" or "tenant") || (request.BBox is null && request.Words is not { Count: > 0 }) || request.Words is { Count: > 200 })
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid selection", detail: "Name a bBox or up to 200 word indexes on the page; scope is layout or tenant.");
        }

        var document = await db.Documents.AsNoTracking().Include(d => d.IngestionItem).ThenInclude(i => i!.Ingestion).SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
        {
            return SupplierGroupEndpoints.NotFound("Document", id);
        }

        var words = await LayoutEndpoints.WordsAsync(blobs, document, ct);
        var values = scope == "layout" && document.SupplierId is { } supplierId ? (await LayoutSettings.LoadAsync(db, tenant.TenantId!.Value, supplierId, ct))?.Values : null;
        var box = request.BBox is { } b ? new BoundingBox(b.X0, b.Y0, b.X1, b.Y1) : (BoundingBox?)null;
        var proposal = IdentifierProposer.Propose(words, request.Page, box, request.Words, scope == "tenant", values);
        if (proposal is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Nothing selected", detail: $"No word of page {request.Page}'s text layer lies in the selection.");
        }

        return TypedResults.Ok(new ProposeIdentifierResponse(JsonSerializer.SerializeToElement(proposal.Identifier), proposal.Describe, proposal.Holds, proposal.Warnings));
    }

    private static async Task<Results<Accepted<DocumentDetail>, ValidationProblem, ProblemHttpResult>> MatchLayoutAsync(
        Guid id,
        MatchLayoutRequest request,
        ITenantContext tenant,
        ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IEffectiveConfigResolver configs,
        IPipelineQueue queue,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await LayoutEndpoints.GuardAsync(tenant, features, "Layouts", ct) is { } guard)
        {
            return guard;
        }

        var errors = new Errors();
        if (string.IsNullOrWhiteSpace(request.TypeKey) || !DocumentTypeKey.TryParse(request.TypeKey.Trim(), out var key) || key.IsUnknown)
        {
            errors.Add("typeKey", "typeKey names the document's type.");
            key = DocumentTypeKey.Unknown;
        }

        if (request.SaveAs is { } saveAs && saveAs is not (LearnSaveAs.NewLayout or LearnSaveAs.NewVersion))
        {
            errors.Add("saveAs", "saveAs is newLayout or newVersion.");
        }

        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        var document = await db.Documents.Include(d => d.Flags).SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
        {
            return SupplierGroupEndpoints.NotFound("Document", id);
        }

        if (DocumentEndpoints.NotInReview(document, "match its layout") is { } notInReview)
        {
            return notInReview;
        }

        var flag = document.Flags.FirstOrDefault(f => f.Code == FlagCodes.NewLayout && f.ResolvedAt is null);
        if (flag is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "No new layout", detail: $"Document {id} has no open NEW_LAYOUT; :match-layout is for a design no layout of its supplier recognises.");
        }

        var config = await configs.ResolveAsync(new EffectiveConfigRequest(document.TenantId, key, document.SupplierId), ct);
        if (config.Type is not { Compiled: not null } type)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["typeKey"] = [$"{key} has no published definition this tenant can use."] });
        }

        var layoutIds = await db.SupplierLayouts.AsNoTracking().Where(l => l.OwnerId == document.SupplierId).Select(l => l.Id).ToListAsync(ct);
        if (request.BasedOnLayoutId is { } basis && !layoutIds.Contains(basis))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["basedOnLayoutId"] = ["basedOnLayoutId names a layout of the document's supplier."] });
        }

        if (request.PendingLayoutVersionId is { } pending && !await db.SupplierLayoutVersions.AsNoTracking().AnyAsync(v => v.Id == pending && v.Status == SupplierLayoutVersionStatus.Candidate && layoutIds.Contains(v.LayoutId), ct))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["pendingLayoutVersionId"] = ["pendingLayoutVersionId names a Candidate version of the supplier's layouts."] });
        }

        var now = clock.GetUtcNow();
        var actor = DocumentQueries.ActorName(user);
        var parameters = new Dictionary<string, string>(flag.Params ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        var basisChanged = false;
        void Set(string name, string? value)
        {
            if (value is null)
            {
                return;
            }

            basisChanged |= !string.Equals(parameters.GetValueOrDefault(name), value, StringComparison.Ordinal) && name is "basedOnLayoutId" or "pendingLayoutVersionId";
            parameters[name] = value;
        }

        Set("basedOnLayoutId", request.BasedOnLayoutId?.ToString());
        Set("pendingLayoutVersionId", request.PendingLayoutVersionId?.ToString());
        Set("saveAs", request.SaveAs);
        Set("targetLayoutId", request.TargetLayoutId?.ToString());
        flag.Params = parameters;
        if (request.PendingLayoutVersionId is { } chosen)
        {
            document.PendingLayoutVersionId = chosen;
        }

        // The reviewer confirms or changes the type (TypeSource Human); the pre-fill runs again only for another type or basis.
        var typeChanged = document.Type != key;
        var previous = new { typeKey = document.Type.Value, typeVersionId = document.DocumentTypeVersionId, typeSource = document.TypeSource };
        DocumentEndpoints.ApplyType(document, key, type, now);
        DocumentEndpoints.AddSetTypeCorrection(db, document, previous, new { typeKey = key.Value, typeVersionId = type.VersionId, typeVersion = type.Version, matchLayout = true }, actor, now);
        DocumentQueries.AddEvent(db, document, "document.layout_matched", null, actor, new { typeKey = key.Value, request.BasedOnLayoutId, request.PendingLayoutVersionId, request.SaveAs, request.TargetLayoutId, rerun = typeChanged || basisChanged }, now);
        await db.SaveChangesAsync(ct);
        if (typeChanged || basisChanged)
        {
            if (await DocumentEndpoints.ReprocessCoreAsync(db, queue, document, PipelineStage.Extract, unpin: false, actor, now, ReprocessConfigModes.Pinned, ct) is { } refused)
            {
                return refused;
            }
        }

        return TypedResults.Accepted($"/api/v1/documents/{id}", await DocumentEndpoints.FreshDetailAsync(db, id, configs, features, ct));
    }

    private static async Task<Results<Created<SupplierLayoutResponse>, ValidationProblem, ProblemHttpResult>> CreateVersionAsync(
        Guid layoutId,
        CreateLayoutVersionRequest request,
        ITenantContext tenant,
        ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await LayoutEndpoints.GuardAsync(tenant, features, "Layouts", ct) is { } problem)
        {
            return problem;
        }

        var layout = await db.SupplierLayouts.Include(l => l.Versions).SingleOrDefaultAsync(l => l.Id == layoutId, ct);
        if (layout is null)
        {
            return SupplierGroupEndpoints.NotFound("Layout", layoutId);
        }

        var errors = new Errors();
        IdentificationRule? rule = null;
        if (request.Identification.ValueKind != JsonValueKind.Object)
        {
            errors.Add("identification", "identification is an identification rule object (all, any, none).");
        }
        else
        {
            rule = IdentificationRuleChecks.Check(request.Identification.GetRawText(), layout.Scope == SupplierLayoutScope.Group ? IdentificationScope.Layout : IdentificationScope.Layout, out var problems);
            foreach (var group in problems.GroupBy(p => p.Path))
            {
                errors.Add($"identification.{group.Key}", string.Join(" ", group.Select(p => p.Message).Distinct()));
            }
        }

        LayoutMapping? mapping = null;
        if (request.Mapping.ValueKind != JsonValueKind.Object)
        {
            errors.Add("mapping", "mapping is a field mapping object (fields, defaults, requiredFields).");
        }
        else if (LayoutMapping.TryParse(request.Mapping.GetRawText(), out var mappingError) is { } parsed)
        {
            mapping = parsed;
        }
        else
        {
            errors.Add("mapping", mappingError!);
        }

        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        var now = clock.GetUtcNow();
        var version = new SupplierLayoutVersion
        {
            Id = Guid.CreateVersion7(),
            TenantId = layout.TenantId,
            LayoutId = layout.Id,
            Version = layout.Versions.Count == 0 ? 1 : layout.Versions.Max(v => v.Version) + 1,
            Status = SupplierLayoutVersionStatus.Candidate,
            Origin = SupplierLayoutOrigin.Human,
            IdentificationJson = rule!.ToJson(),
            IdentificationHash = rule.Hash,
            MappingJson = request.Mapping.GetRawText(),
            CoverageJson = JsonSerializer.Serialize(mapping!.Coverage),
            CreatedBy = DocumentQueries.ActorName(user),
            CreatedAt = now,
        };
        db.SupplierLayoutVersions.Add(version);
        layout.Versions.Add(version);
        layout.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/supplier-layouts/{layout.Id}/versions/{version.Version}", LayoutEndpoints.Response(layout));
    }

    private static async Task<Results<Ok<ReplayReport>, ProblemHttpResult>> ReplayAsync(
        Guid layoutId,
        int version,
        ReplayLayoutRequest? request,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IContainerBlobStore blobs,
        [FromServices] IEffectiveConfigResolver configs,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await LayoutEndpoints.GuardAsync(tenant, features, "Layouts", ct) is { } problem)
        {
            return problem;
        }

        if (request?.LastN is { } n && n is < 1 or > LayoutEndpoints.MaxLastN || request?.DocumentIds is { Count: > LayoutEndpoints.MaxDocumentIds })
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid replay", detail: $"lastN is 1 to {LayoutEndpoints.MaxLastN}; at most {LayoutEndpoints.MaxDocumentIds} documentIds.");
        }

        var layout = await db.SupplierLayouts.Include(l => l.Versions).SingleOrDefaultAsync(l => l.Id == layoutId, ct);
        var target = layout?.Versions.FirstOrDefault(v => v.Version == version);
        if (layout is null || target is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: $"Layout {layoutId} has no version {version}.");
        }

        var settings = (await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenant.TenantId!.Value, ct)).Settings;
        var report = await new LayoutLearning(db, blobs, configs, clock).ReplayAsync(layout, target, request?.DocumentIds, request?.LastN ?? LayoutLearning.DefaultReplayLastN, settings.EffectiveLayoutAgreement(), ct);
        target.ReplayJson = report.ToJson();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(report);
    }

    private static async Task<Results<Ok<IReadOnlyList<MatchingDocumentDto>>, ProblemHttpResult>> MatchingDocumentsAsync(
        Guid layoutId,
        int version,
        string? state,
        bool? holds,
        ITenantContext tenant,
        [FromServices] DocProcDbContext db,
        [FromServices] ITenantFeatures features,
        [FromServices] IContainerBlobStore blobs,
        [FromServices] IEffectiveConfigResolver configs,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await LayoutEndpoints.GuardAsync(tenant, features, "Layouts", ct) is { } problem)
        {
            return problem;
        }

        var which = string.IsNullOrWhiteSpace(state) ? "pending" : state.Trim();
        if (which is not ("pending" or "approved"))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid state", detail: "state is pending or approved.");
        }

        var layout = await db.SupplierLayouts.AsNoTracking().Include(l => l.Versions).SingleOrDefaultAsync(l => l.Id == layoutId, ct);
        var target = layout?.Versions.FirstOrDefault(v => v.Version == version);
        if (layout is null || target is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: $"Layout {layoutId} has no version {version}.");
        }

        var rule = IdentificationRule.Parse(target.IdentificationJson);
        var found = await new LayoutLearning(db, blobs, configs, clock).MatchingAsync(layout, rule, which, holds ?? true, which == "approved" && holds == false ? layout.Id : null, LayoutLearning.MaxMatching, ct);
        return TypedResults.Ok<IReadOnlyList<MatchingDocumentDto>>([.. found.Select(m => new MatchingDocumentDto(m.Document.Id, m.Document.IngestionItem?.FileName, m.Document.Status.ToString(), m.Document.Type.Value, m.Document.SupplierLayoutVersionId, m.Document.PendingLayoutVersionId, m.Result.Holds, m.Result.Failed))]);
    }

    private static async Task<Results<Ok<IReadOnlyList<BulkReprocessItem>>, ValidationProblem, ProblemHttpResult>> BulkReprocessAsync(
        BulkReprocessRequest request,
        ITenantContext tenant,
        ClaimsPrincipal user,
        [FromServices] DocProcDbContext db,
        IPipelineQueue queue,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (RequireTenant(tenant, "Bulk reprocessing") is { } scope)
        {
            return scope;
        }

        var errors = new Errors();
        if (request.DocumentIds is not { Count: > 0 } ids || ids.Count > MaxBulkDocuments)
        {
            errors.Add("documentIds", $"1 to {MaxBulkDocuments} documents.");
        }

        var stage = PipelineStage.Classify;
        if (!string.IsNullOrWhiteSpace(request.FromStage) && (!Enum.TryParse(request.FromStage.Trim(), ignoreCase: true, out stage) || !DocumentEndpoints.ReprocessStages.Contains(stage)))
        {
            errors.Add("fromStage", $"Must be one of {string.Join(", ", DocumentEndpoints.ReprocessStages)}.");
        }

        var configMode = string.IsNullOrWhiteSpace(request.ConfigMode) ? ReprocessConfigModes.Current : request.ConfigMode.Trim();
        if (configMode is not (ReprocessConfigModes.Pinned or ReprocessConfigModes.Current))
        {
            errors.Add("configMode", $"Must be {ReprocessConfigModes.Pinned} or {ReprocessConfigModes.Current}.");
        }

        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        var actor = DocumentQueries.ActorName(user);
        var results = new List<BulkReprocessItem>();
        foreach (var id in request.DocumentIds!.Distinct())
        {
            var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
            if (document is null)
            {
                results.Add(new BulkReprocessItem(id, false, "not found"));
                continue;
            }

            var refused = await DocumentEndpoints.ReprocessCoreAsync(db, queue, document, stage, configMode == ReprocessConfigModes.Current, actor, clock.GetUtcNow(), configMode, ct);
            results.Add(new BulkReprocessItem(id, refused is null, refused?.ProblemDetails.Detail));
        }

        return TypedResults.Ok<IReadOnlyList<BulkReprocessItem>>(results);
    }

    private static async Task<Results<Ok<IReadOnlyList<NotificationDto>>, ProblemHttpResult>> ListNotificationsAsync(bool? all, ITenantContext tenant, ClaimsPrincipal user, [FromServices] DocProcDbContext db, CancellationToken ct)
    {
        if (RequireTenant(tenant, "Notifications") is { } scope)
        {
            return scope;
        }

        var audiences = Policies.All.Where(p => Policies.Satisfies(user, p)).ToList();
        var rows = await db.Notifications.AsNoTracking().Where(n => audiences.Contains(n.Audience) && (all == true || n.ResolvedAt == null)).ToListAsync(ct);
        return TypedResults.Ok<IReadOnlyList<NotificationDto>>([.. rows.OrderByDescending(n => n.UpdatedAt).Take(200).Select(ToDto)]);
    }

    private static async Task<Results<Ok<NotificationDto>, ProblemHttpResult>> DismissNotificationAsync(Guid id, ITenantContext tenant, ClaimsPrincipal user, [FromServices] DocProcDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (RequireTenant(tenant, "Notifications") is { } scope)
        {
            return scope;
        }

        var row = await db.Notifications.SingleOrDefaultAsync(n => n.Id == id, ct);
        if (row is null || !Policies.RolesByPolicy.ContainsKey(row.Audience) || !Policies.Satisfies(user, row.Audience))
        {
            return SupplierGroupEndpoints.NotFound("Notification", id);
        }

        var now = clock.GetUtcNow();
        row.ResolvedAt ??= now;
        row.ResolvedBy ??= DocumentQueries.ActorName(user);
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(row));
    }

    private static NotificationDto ToDto(Notification n) => new(n.Id, n.Audience, n.Kind, n.Title, n.Message, n.EntityType, n.EntityId, n.Count, LayoutEndpoints.Element(n.PayloadJson), n.CreatedAt, n.UpdatedAt, n.ResolvedAt);
}
