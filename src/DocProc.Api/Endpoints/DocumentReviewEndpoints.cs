using System.Security.Claims;
using System.Text.Json.Nodes;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Documents;
using DocProc.Application.Pipeline;
using DocProc.Application.Routing;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;
using DocProc.Domain.Validation;
using DocProc.Infrastructure.Pdf;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using DocProc.Pipeline.Review;
using DocProc.Pipeline.Routing;
using DocProc.Pipeline.Support;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocProc.Api.Endpoints;

/// <summary>
/// The review actions of docs/11 §12 (P2, track A): give a document a type (<c>:set-type</c>), assign its supplier or
/// create one from it (<c>:assign-supplier</c>, <c>:create-supplier</c>), split its pages into several documents or join
/// parts back (<c>:split</c>, <c>:join</c>), and the approval gate that refuses a document without a definition or data.
/// Each acts on a document a reviewer may correct (NeedsReview, Validated, Failed) and writes its timeline event; the
/// ones that change the document's data write a Correction row too. A tracked save carries the document's version
/// token, so a reviewer or a pipeline stage that wrote the document meanwhile makes the action answer 409.
/// </summary>
public static partial class DocumentEndpoints
{
    /// <summary>The <c>prefill</c> values of <c>:set-type</c>.</summary>
    public static class SetTypePrefill
    {
        public const string None = "none";
        public const string Auto = "auto";
    }

    /// <summary>The codes of <see cref="ApprovalBlock"/>.</summary>
    public static class ApprovalBlockCodes
    {
        public const string TypeRequired = "TYPE_REQUIRED";
        public const string NoDefinition = "NO_DEFINITION";
        public const string NoData = "NO_DATA";
    }

    /// <summary>The most parts one split may make, and the most documents one join may take.</summary>
    public const int MaxParts = 100;

    /// <summary>
    /// The body of a review action (<c>:set-type</c>, <c>:assign-supplier</c>, <c>:create-supplier</c>, <c>:split</c>,
    /// <c>:join</c>); a split of <see cref="MaxParts"/> parts or a supplier with every list at its cap takes a few KB.
    /// </summary>
    public const int MaxReviewActionRequestBytes = SupplierEndpoints.MaxRequestBytes;

    private static void MapReviewActions(RouteGroupBuilder documents)
    {
        documents.MapPost("/{id:guid}:set-type", SetTypeAsync)
            .RequireAuthorization(Policies.Review)
            .WithMetadata(new RequestSizeLimitAttribute(MaxReviewActionRequestBytes))
            .WithName("SetDocumentType")
            .WithSummary("Gives a document in review a type (docs/11 §12.1): pinned to the tenant's published version of it (or the version named), with prefill none (the empty fields of the type at once, 200) or auto (extracted again for that type, 202). A setType correction records it.");

        documents.MapPost("/{id:guid}:assign-supplier", AssignSupplierAsync)
            .RequireAuthorization(Policies.Review)
            .WithMetadata(new RequestSizeLimitAttribute(MaxReviewActionRequestBytes))
            .WithName("AssignDocumentSupplier")
            .WithSummary("Assigns a supplier of the document's tenant to a document in review (docs/11 §12.1); the Match stage keeps it from then on, and the document is checked again with it.");

        documents.MapPost("/{id:guid}:create-supplier", CreateSupplierAsync)
            .RequireAuthorization(Policies.Review)
            .WithMetadata(new RequestSizeLimitAttribute(MaxReviewActionRequestBytes))
            .WithName("CreateSupplierFromDocument")
            .WithSummary("Creates a supplier from a document in review, with the checks of POST /suppliers, and assigns it to the document (docs/11 §14). A reviewer gives the name, VAT id, tax id, IBANs and currency; emailDomains, aliases, alwaysReview and attributes need a tenant admin (403).");

        documents.MapPost("/{id:guid}:split", SplitAsync)
            .RequireAuthorization(Policies.Review)
            .WithMetadata(new RequestSizeLimitAttribute(MaxReviewActionRequestBytes))
            .WithName("SplitDocument")
            .WithSummary("Splits a document in review into several documents by page (docs/11 §12.7): the first part keeps the id, the others are new documents of the same attachment; each is classified again, or extracted for the type a part names. 202.");

        documents.MapGet("/{id:guid}/parts", PartsAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("GetDocumentParts")
            .WithSummary("The documents of the same attachment as this one, in page order (docs/11 §12.7): what a reviewer can join it with, and the parts a split or a join left, cancelled ones with the document they were joined into.");

        documents.MapPost("/{id:guid}:join", JoinAsync)
            .RequireAuthorization(Policies.Review)
            .WithMetadata(new RequestSizeLimitAttribute(MaxReviewActionRequestBytes))
            .WithName("JoinDocuments")
            .WithSummary("Joins other parts of the same attachment into this document, pages in attachment order (docs/11 §12.7); the others are cancelled with a link to it, and the joined document is classified again. 202.");
    }

    private static async Task<Results<Ok<DocumentDetail>, Accepted<DocumentDetail>, ValidationProblem, ProblemHttpResult>> SetTypeAsync(
        Guid id,
        SetDocumentTypeRequest request,
        [FromServices] DocProcDbContext db,
        IPipelineQueue queue,
        TimeProvider clock,
        ClaimsPrincipal user,
        [FromServices] IEffectiveConfigResolver configs,
        [FromServices] ITenantFeatures features,
        [FromServices] DocumentValidator validator,
        [FromServices] IPipelineConfigResolver pipelines,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        DocumentTypeKey key = default;
        if (string.IsNullOrWhiteSpace(request.TypeKey) || !DocumentTypeKey.TryParse(request.TypeKey.Trim(), out key))
        {
            errors["typeKey"] = ["A document type key is required (a letter, then letters, digits, '_', '.' or '-')."];
        }
        else if (key.IsUnknown)
        {
            errors["typeKey"] = ["Unknown is what the document is now; name the type it has."];
        }

        var prefill = string.IsNullOrWhiteSpace(request.Prefill) ? SetTypePrefill.None : request.Prefill.Trim();
        if (prefill is not (SetTypePrefill.None or SetTypePrefill.Auto))
        {
            errors["prefill"] = [$"Must be {SetTypePrefill.None} or {SetTypePrefill.Auto}."];
        }

        if (request.Version is < 1)
        {
            errors["version"] = ["A version is 1 or more."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var row = await DocumentQueries.FindForDetailAsync(db, id, track: true, ct);
        if (row is null)
        {
            return NotFound(id);
        }

        var document = row.Document;
        if (NotInReview(document, "set its type") is { } notInReview)
        {
            return notInReview;
        }

        Guid? pinned = null;
        if (request.Version is { } version)
        {
            var tenantId = document.TenantId;
            var keyValue = key.Value;
            pinned = await db.DocumentTypeVersions.AsNoTracking()
                .Where(v => v.TenantId == tenantId && v.Definition!.TenantId == tenantId && v.Definition.Key == keyValue && v.Version == version
                            && (v.State == DocumentTypeVersionState.Published || v.State == DocumentTypeVersionState.Deprecated))
                .Select(v => (Guid?)v.Id)
                .FirstOrDefaultAsync(ct);
            if (pinned is null)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["version"] = [$"{key} has no published version {version}."] });
            }
        }

        var config = await configs.ResolveAsync(new EffectiveConfigRequest(document.TenantId, key, document.SupplierId, pinned), ct);
        if (config.Type is not { Compiled: { } compiled } type || (pinned is not null && type.VersionId != pinned))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["typeKey"] = [$"{key} has no published definition this tenant can use; adopt or publish it under Settings > Document types first."],
            });
        }

        if (prefill == SetTypePrefill.None && document.Type == key && document.DocumentTypeVersionId == type.VersionId && !string.IsNullOrWhiteSpace(document.CanonicalJson))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Type unchanged",
                detail: $"Document {document.Id} already is {key} v{type.Version}; correct its fields, or set the type with prefill auto to extract it again.");
        }

        var now = clock.GetUtcNow();
        var actor = DocumentQueries.ActorName(user);
        var previous = new { typeKey = document.Type.Value, typeVersionId = document.DocumentTypeVersionId, typeSource = document.TypeSource };
        var next = new { typeKey = key.Value, typeVersionId = type.VersionId, typeVersion = type.Version, prefill };
        if (prefill == SetTypePrefill.Auto)
        {
            // The existing reprocess path from Extract (docs/11 §12.1 step 3): the document goes back to Matched and the
            // Extract stage fills what it can for the type, pinned to the chosen version.
            var reached = await FurthestPipelineStatusAsync(db, document, ct);
            if (Array.IndexOf(PipelineOrder, reached) < Array.IndexOf(PipelineOrder, DocumentStatus.Matched))
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Stage not reached",
                    detail: $"Document {document.Id} never got past {reached}, so it cannot be extracted yet; reprocess it from the start, or set the type with prefill none.");
            }

            var change = new DocumentStatusChange(document.Status, DocumentStatus.Matched);
            await CountRuleOverrideAsync(db, document, key, ct);
            ApplyType(document, key, type, now);
            ClearCaptured(document);
            await DocumentFieldIndexWriter.RefreshAsync(db, document, [], ct);
            await DocumentTableReports.RemoveAllAsync(db, document.Id, ct);
            ResolveOpenFlags(document, [FlagCodes.TypeUnknown], actor, now);
            document.Status = DocumentStatus.Matched;
            document.FailureReason = null;
            await CloseOpenTasksAsync(db, document.Id, ReviewTaskStatus.Skipped, now, ct);
            AddSetTypeCorrection(db, document, previous, next, actor, now);
            DocumentQueries.AddEvent(db, document, DocumentEventTypes.TypeSet, change, actor, new { from = previous.typeKey, to = key.Value, version = type.Version, versionId = type.VersionId, prefill, fromStage = nameof(PipelineStage.Extract) }, now);
            if (!await TrySaveAsync(db, ct))
            {
                return ConcurrentChange(id);
            }

            await queue.EnqueueAsync(new ProcessDocument(document.TenantId, document.Id, PipelineStage.Extract), ct);
            return TypedResults.Accepted($"/api/v1/documents/{id}", await FreshDetailAsync(db, id, configs, features, ct));
        }

        // prefill none: the tree of the type version with every field Missing, at once (DefinitionTree.CreateEmpty), as
        // the Extract stage starts a definition-driven type; an adopted invoice or credit note of the coded record gets
        // the empty record of its kind; a type the tenant has no data path for (a purchase order without
        // configurableTypes) gets none, as Extract leaves it.
        var configurable = await features.IsEnabledAsync(document.TenantId, TenantFeature.ConfigurableTypes, ct);
        await CountRuleOverrideAsync(db, document, key, ct);
        ApplyType(document, key, type, now);
        ClearCaptured(document);
        await DocumentTableReports.RemoveAllAsync(db, document.Id, ct);
        string? flagMessage = null;
        if (CanonicalStorage.IsDefinitionDriven(type, configurable))
        {
            document.CanonicalJson = DefinitionTree.CreateEmpty(compiled.Descriptors).ToJsonString(CanonicalJson.Options);
            document.CanonicalSchemaVersion = DefinitionTree.SchemaVersion;
        }
        else if (key.IsInvoiceFamily)
        {
            var kind = key == DocumentTypeKey.CreditNote ? InvoiceKind.CreditNote : InvoiceKind.Invoice;
            document.CanonicalJson = CanonicalDocumentSerializer.Serialize(new Invoice { Kind = FieldValue<InvoiceKind>.Inferred(kind, FieldSource.Human, 1) });
            document.CanonicalSchemaVersion = CanonicalDocumentSerializer.SchemaVersion;
        }
        else
        {
            flagMessage = $"No extraction path exists for {key} documents yet.";
        }

        document.ExtractionMethod = document.CanonicalJson is null ? null : ExtractionMethod.Manual;
        DocumentRoleColumns.Refresh(document, type.Roles);
        await DocumentFieldIndexWriter.RefreshAsync(db, document, type.IndexedFields, ct);

        // The type's flags of Classify and Extract described the old type; the checks below raise the new type's.
        ResolveOpenFlags(document, [FlagCodes.TypeUnknown], actor, now);
        DropOpenPipelineFlags(db, document, [FlagCodes.UnsupportedType, FlagCodes.ProfileFallback, FlagCodes.CurrencyInferred]);
        if (flagMessage is not null)
        {
            DocumentChecks.ReplaceFlags(db, document, [], [FlagCodes.Create(FlagCodes.UnsupportedType, flagMessage, parameters: FlagParams.Of(("type", key.Value)))], now);
        }

        var taskTenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == document.TenantId, ct);
        foreach (var task in await db.ReviewTasks.Where(t => t.DocumentId == document.Id && t.Kind == ReviewTaskKind.Document && (t.Status == ReviewTaskStatus.Open || t.Status == ReviewTaskStatus.InProgress)).ToListAsync(ct))
        {
            task.Priority = DocumentChecks.PriorityOf(type);
            task.SlaDueAt = taskTenant is null ? null : DocumentChecks.DueAtOf(type, document, taskTenant);
        }

        AddSetTypeCorrection(db, document, previous, next, actor, now);
        DocumentQueries.AddEvent(db, document, DocumentEventTypes.TypeSet, null, actor, new { from = previous.typeKey, to = key.Value, version = type.Version, versionId = type.VersionId, prefill }, now);
        if (!await TrySaveAsync(db, ct))
        {
            return ConcurrentChange(id);
        }

        await RecheckAsync(db, document, type, validator, pipelines, now, ct);
        return TypedResults.Ok(await FreshDetailAsync(db, id, configs, features, ct));
    }

    private static async Task<Results<Ok<DocumentDetail>, Accepted<DocumentDetail>, ValidationProblem, ProblemHttpResult>> AssignSupplierAsync(
        Guid id,
        AssignSupplierRequest request,
        [FromServices] DocProcDbContext db,
        IPipelineQueue queue,
        TimeProvider clock,
        ClaimsPrincipal user,
        [FromServices] IEffectiveConfigResolver configs,
        [FromServices] ITenantFeatures features,
        [FromServices] DocumentValidator validator,
        [FromServices] IPipelineConfigResolver pipelines,
        CancellationToken ct)
    {
        var row = await DocumentQueries.FindForDetailAsync(db, id, track: true, ct);
        if (row is null)
        {
            return NotFound(id);
        }

        var document = row.Document;
        if (NotInReview(document, "assign its supplier") is { } notInReview)
        {
            return notInReview;
        }

        // The supplier must be the document's tenant's, whatever the caller's scope (an operator in platform scope sees
        // every tenant's suppliers).
        var tenantId = document.TenantId;
        var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.SupplierId && s.TenantId == tenantId, ct);
        if (supplier is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["supplierId"] = [$"No supplier {request.SupplierId} of the document's tenant."] });
        }

        var now = clock.GetUtcNow();
        var actor = DocumentQueries.ActorName(user);
        SupplierSourceRule? remembered = null;
        if (request.Remember is { } remember)
        {
            if (!await features.IsEnabledAsync(tenantId, TenantFeature.SourceRouting, ct))
            {
                return SupplierConfigSupport.FeatureOff();
            }

            var made = await RememberAsync(db, document, supplier, remember, user, actor, now, ct);
            if (made.Invalid is { } invalid)
            {
                return invalid;
            }

            if (made.Problem is { } refused)
            {
                return refused;
            }

            remembered = made.Rule;
        }

        Assign(db, document, supplier, created: false, actor, now, remembered);
        if (!await TrySaveAsync(db, ct))
        {
            return ConcurrentChange(id);
        }

        // docs/11 §12.1: with supplierLayouts, the assignment re-runs classify.identification for the supplier through the
        // reprocess path (from Classify, 202), so the document takes the same identification path as a routed one: one
        // of its layouts applied with evidence, NEW_LAYOUT, or the Unknown panel. A document that never reached
        // Classify (or an e-invoice, whose XML decides) is rechecked inline as before.
        if (await features.IsEnabledAsync(tenantId, TenantFeature.SupplierLayouts, ct) && document.TypeSource != TypeSource.Structured && document.EmbeddedXmlBlob is null)
        {
            var current = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == id, ct);
            if (await ReprocessCoreAsync(db, queue, current, PipelineStage.Classify, unpin: false, actor, now, ReprocessConfigModes.Pinned, ct) is null)
            {
                return TypedResults.Accepted($"/api/v1/documents/{id}", await FreshDetailAsync(db, id, configs, features, ct));
            }
        }

        await RecheckAsync(db, document, configs, validator, pipelines, now, ct);
        return TypedResults.Ok(await FreshDetailAsync(db, id, configs, features, ct));
    }

    /// <summary>What <see cref="RememberAsync"/> made: the rule (added, not saved; an existing rule of the same supplier is reused), or why not.</summary>
    internal sealed record RememberedRule(SupplierSourceRule? Rule, ValidationProblem? Invalid, ProblemHttpResult? Problem);

    /// <summary>
    /// The source rule an assignment remembers (docs/11 §12.1, §18 <c>remember</c>): the named facts of the document's
    /// ingestion as conditions, checked like <c>POST /suppliers/{id}/source-rules</c>. A reviewer without
    /// <c>ConfigAdmin</c> proposes it (saved disabled), as a reviewer's learned layout is a Candidate (decision 2).
    /// </summary>
    internal static async Task<RememberedRule> RememberAsync(DocProcDbContext db, Document document, Supplier supplier, AssignSupplierRemember remember, ClaimsPrincipal user, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var ingestion = await db.IngestionItems.AsNoTracking().Where(i => i.Id == document.IngestionItemId).Select(i => i.Ingestion!).SingleAsync(ct);
        var conditions = (remember.Conditions ?? []).Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string? Fact(string condition, string? value, string missing)
        {
            if (value is null)
            {
                errors[$"remember.conditions.{condition}"] = [missing];
            }

            return value;
        }

        var unknown = conditions.Where(c => c is not ("sender" or "senderDomain" or "recipient" or "folder" or "channel")).ToList();
        if (conditions.Count == 0 || unknown.Count > 0)
        {
            errors["remember.conditions"] = [$"conditions are some of sender, senderDomain, recipient, folder and channel{(unknown.Count > 0 ? $"; not {string.Join(", ", unknown)}" : string.Empty)}."];
        }

        var recipients = SourceRuleCandidates.ContextOf(ingestion, null);
        var request = new SourceRuleRequest(
            ChannelId: conditions.Contains("channel") || conditions.Contains("folder") ? ingestion.ChannelId : null,
            Recipient: conditions.Contains("recipient") ? Fact("recipient", recipients.Mailbox ?? (recipients.Recipients.Count > 0 ? recipients.Recipients[0] : null), "The document's ingestion has no mailbox or recipient.") : null,
            Sender: conditions.Contains("sender") ? Fact("sender", ingestion.SourceSender, "The document's ingestion has no sender.") : null,
            SenderDomain: conditions.Contains("senderDomain") ? Fact("senderDomain", SourceConditions.DomainOf(ingestion.SourceSender), "The document's ingestion has no sender.") : null,
            FolderPrefix: conditions.Contains("folder") ? Fact("folder", string.IsNullOrEmpty(ingestion.SourceFolderPath) ? null : ingestion.SourceFolderPath, "The document did not come from a folder below its channel's root.") : null,
            Mode: remember.Mode,
            RequireSenderAuth: remember.RequireSenderAuth,
            PinTypeKey: remember.PinType == true ? (document.Type == DocumentTypeKey.Unknown ? Fact("pinType", null, "The document's type is Unknown; set it first.") : document.Type.Value) : null,
            Enabled: Policies.Satisfies(user, Policies.ConfigAdmin));
        if (errors.Count > 0)
        {
            return new RememberedRule(null, TypedResults.ValidationProblem(errors), null);
        }

        var rule = new SupplierSourceRule
        {
            Id = Guid.CreateVersion7(),
            TenantId = document.TenantId,
            SupplierId = supplier.Id,
            ConditionHash = string.Empty,
            CreatedFromDocumentId = document.Id,
            CreatedBy = actor,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var refusal = await SourceRuleEndpoints.ApplyAsync(db, rule, request, supplier, ct);
        if (refusal?.Invalid is { } invalid)
        {
            return new RememberedRule(null, invalid, null);
        }

        if (refusal is not null)
        {
            // The same conditions remembered again for the same supplier are the rule that exists; for another supplier, a conflict.
            var hash = SourceConditions.Hash(request.ChannelId, SourceConditions.Address(request.Recipient), SourceConditions.Address(request.Sender), SourceConditions.Domain(request.SenderDomain), request.FolderPrefix is null ? null : SourceConditions.Folder(request.FolderPrefix), null, null);
            var existing = await db.SupplierSourceRules.SingleOrDefaultAsync(r => r.ConditionHash == hash, ct);
            return existing?.SupplierId == supplier.Id ? new RememberedRule(existing, null, null) : new RememberedRule(null, null, refusal.Problem);
        }

        db.SupplierSourceRules.Add(rule);
        return new RememberedRule(rule, null, null);
    }

    private static async Task<Results<Created<SupplierFromReviewResponse>, ValidationProblem, ProblemHttpResult>> CreateSupplierAsync(
        Guid id,
        CreateSupplierRequest request,
        [FromServices] DocProcDbContext db,
        ITenantContext tenant,
        TimeProvider clock,
        ClaimsPrincipal user,
        [FromServices] IEffectiveConfigResolver configs,
        [FromServices] ITenantFeatures features,
        [FromServices] DocumentValidator validator,
        [FromServices] IPipelineConfigResolver pipelines,
        CancellationToken ct)
    {
        if (EndpointSupport.RequireTenant(tenant, "Suppliers") is { } scope)
        {
            return scope;
        }

        // Separation of duties (docs/11 §14): a reviewer creates the supplier the document names, with the name and the
        // identifiers printed on it; email domains, aliases and alwaysReview, which steer the Match stage and the review
        // of every later document of the tenant, stay master data of a tenant admin (POST /suppliers, PATCH /suppliers/{id}).
        if (!Policies.Satisfies(user, Policies.TenantAdmin) && AdminOnlySupplierFields(request) is { Count: > 0 } adminOnly)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Tenant admin only",
                detail: $"A reviewer creates a supplier with its name, VAT id, tax id, IBANs and currency; {string.Join(", ", adminOnly)} are set by a tenant admin under Suppliers.");
        }

        var row = await DocumentQueries.FindForDetailAsync(db, id, track: true, ct);
        if (row is null)
        {
            return NotFound(id);
        }

        var document = row.Document;
        if (NotInReview(document, "create its supplier") is { } notInReview)
        {
            return notInReview;
        }

        var now = clock.GetUtcNow();
        var creation = await SupplierEndpoints.AddAsync(db, features, document.TenantId, request, now, ct);
        if (creation.Invalid is { } invalid)
        {
            return invalid;
        }

        if (creation.Problem is { } refused)
        {
            return refused;
        }

        var supplier = creation.Supplier!;
        var actor = DocumentQueries.ActorName(user);
        Assign(db, document, supplier, created: true, actor, now);
        if (!await TrySaveAsync(db, ct))
        {
            return ConcurrentChange(id);
        }

        await RecheckAsync(db, document, configs, validator, pipelines, now, ct);
        var detail = await FreshDetailAsync(db, id, configs, features, ct);
        return TypedResults.Created($"/api/v1/suppliers/{supplier.Id}", new SupplierFromReviewResponse(SupplierResponse.From(supplier), detail));
    }

    /// <summary>The members of a <c>:create-supplier</c> body only a tenant admin may set.</summary>
    private static List<string> AdminOnlySupplierFields(CreateSupplierRequest request)
    {
        var fields = new List<string>();
        if (request.EmailDomains is { Count: > 0 })
        {
            fields.Add("emailDomains");
        }

        if (request.Aliases is { Count: > 0 })
        {
            fields.Add("aliases");
        }

        if (request.AlwaysReview is not null)
        {
            fields.Add("alwaysReview");
        }

        if (request.GroupId is not null)
        {
            fields.Add("groupId");
        }

        if (request.Settings is not null)
        {
            fields.Add("settings");
        }

        // Attributes (the ERP vendor number) decide where outputs go and under which account (docs/11 §6.4).
        if (request.Attributes is { Count: > 0 })
        {
            fields.Add("attributes");
        }

        return fields;
    }

    private static async Task<Results<Accepted<SplitDocumentResponse>, ValidationProblem, ProblemHttpResult>> SplitAsync(
        Guid id,
        SplitDocumentRequest request,
        [FromServices] DocProcDbContext db,
        [FromServices] IContainerBlobStore blobs,
        [FromServices] IOptions<PdfProcessingOptions> pdfOptions,
        IPipelineQueue queue,
        TimeProvider clock,
        ClaimsPrincipal user,
        [FromServices] IEffectiveConfigResolver configs,
        CancellationToken ct)
    {
        if (request.Parts is not { Count: >= 2 } requested || requested.Count > MaxParts)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["parts"] = [$"A split has 2 to {MaxParts} parts."] });
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var keys = new DocumentTypeKey?[requested.Count];
        for (var i = 0; i < requested.Count; i++)
        {
            if (requested[i]?.Pages is not { Count: > 0 })
            {
                errors[$"parts[{i}].pages"] = ["Every part needs at least one page."];
            }

            if (requested[i]?.TypeKey is { } typeKey && !string.IsNullOrWhiteSpace(typeKey))
            {
                if (!DocumentTypeKey.TryParse(typeKey.Trim(), out var parsed) || parsed.IsUnknown)
                {
                    errors[$"parts[{i}].typeKey"] = ["Not a document type key; leave it out to classify the part."];
                }
                else
                {
                    keys[i] = parsed;
                }
            }
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var row = await DocumentQueries.FindForDetailAsync(db, id, track: true, ct);
        if (row is null)
        {
            return NotFound(id);
        }

        var document = row.Document;
        if (NotInReview(document, "split it") is { } notInReview)
        {
            return notInReview;
        }

        if (document.EmbeddedXmlBlob is not null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Cannot split", detail: $"Document {document.Id} carries an e-invoice, which is one document; it cannot be split.");
        }

        var parts = new List<ReviewSplitPart>(requested.Count);
        for (var i = 0; i < requested.Count; i++)
        {
            Guid? versionId = null;
            if (keys[i] is { } typed)
            {
                var config = await configs.ResolveAsync(new EffectiveConfigRequest(document.TenantId, typed, document.SupplierId), ct);
                if (config.Type is not { Compiled: not null } type)
                {
                    errors[$"parts[{i}].typeKey"] = [$"{typed} has no published definition this tenant can use."];
                    continue;
                }

                versionId = type.VersionId;
            }

            parts.Add(new ReviewSplitPart(requested[i].Pages, keys[i], versionId));
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var now = clock.GetUtcNow();
        IReadOnlyList<ReviewPartOutcome> outcomes;
        try
        {
            outcomes = await ReviewPartitioning.SplitAsync(db, blobs, pdfOptions.Value, document, parts, DocumentQueries.ActorName(user), now, ct);
        }
        catch (ReviewActionException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["parts"] = [ex.Message] });
        }

        if (!await TrySaveAsync(db, ct))
        {
            return ConcurrentChange(id);
        }

        foreach (var outcome in outcomes)
        {
            await queue.EnqueueAsync(new ProcessDocument(document.TenantId, outcome.DocumentId, outcome.Stage), ct);
        }

        return TypedResults.Accepted($"/api/v1/documents/{id}", new SplitDocumentResponse([.. outcomes.Select(o => new SplitPartResult(o.DocumentId, o.Pages, o.Type?.Value, o.Status))]));
    }

    private static async Task<Results<Ok<IReadOnlyList<DocumentPartDto>>, ProblemHttpResult>> PartsAsync(Guid id, [FromServices] DocProcDbContext db, CancellationToken ct)
    {
        var document = await db.Documents.AsNoTracking().Where(d => d.Id == id).Select(d => new { d.TenantId, d.IngestionItemId }).SingleOrDefaultAsync(ct);
        if (document is null)
        {
            return NotFound(id);
        }

        var parts = await db.Documents.AsNoTracking()
            .Where(d => d.TenantId == document.TenantId && d.IngestionItemId == document.IngestionItemId)
            .OrderBy(d => d.PageStart).ThenBy(d => d.CreatedAt)
            .Select(d => new { d.Id, d.PageStart, d.PageEnd, d.PageCount, d.Status, d.Type, d.DocumentNumber, d.JoinedIntoDocumentId })
            .ToListAsync(ct);
        IReadOnlyList<DocumentPartDto> result = [.. parts.Select(p => new DocumentPartDto(p.Id, p.PageStart, p.PageEnd, p.PageCount, p.Status, p.Type.ToString(), p.DocumentNumber, p.JoinedIntoDocumentId))];
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Accepted<DocumentDetail>, ValidationProblem, ProblemHttpResult>> JoinAsync(
        Guid id,
        JoinDocumentsRequest request,
        [FromServices] DocProcDbContext db,
        [FromServices] IContainerBlobStore blobs,
        [FromServices] IOptions<PdfProcessingOptions> pdfOptions,
        IPipelineQueue queue,
        TimeProvider clock,
        ClaimsPrincipal user,
        [FromServices] IEffectiveConfigResolver configs,
        [FromServices] ITenantFeatures features,
        CancellationToken ct)
    {
        var ids = request.DocumentIds ?? [];
        if (ids.Count == 0 || ids.Count >= MaxParts || ids.Distinct().Count() != ids.Count || ids.Contains(id))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["documentIds"] = [$"Name 1 to {MaxParts - 1} other parts of the same attachment, each once, not this document."],
            });
        }

        var row = await DocumentQueries.FindForDetailAsync(db, id, track: true, ct);
        if (row is null)
        {
            return NotFound(id);
        }

        var target = row.Document;
        if (NotInReview(target, "join it") is { } notInReview)
        {
            return notInReview;
        }

        var others = await db.Documents.Include(d => d.Flags).Where(d => ids.Contains(d.Id)).ToListAsync(ct);
        if (others.Count != ids.Count || others.Any(d => d.TenantId != target.TenantId))
        {
            var missing = ids.Where(i => others.All(d => d.Id != i || d.TenantId != target.TenantId)).ToList();
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["documentIds"] = [$"Not visible in the current scope: {string.Join(", ", missing)}."] });
        }

        foreach (var other in others)
        {
            if (!Correctable.Contains(other.Status))
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Cannot join",
                    detail: $"Document {other.Id} is {other.Status}; only parts that need review, are validated or have failed can be joined (reprocess a settled part first).");
            }
        }

        var now = clock.GetUtcNow();
        ReviewPartOutcome outcome;
        try
        {
            outcome = await ReviewPartitioning.JoinAsync(db, blobs, pdfOptions.Value, target, others, DocumentQueries.ActorName(user), now, ct);
        }
        catch (ReviewActionException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["documentIds"] = [ex.Message] });
        }

        if (!await TrySaveAsync(db, ct))
        {
            return ConcurrentChange(id);
        }

        await queue.EnqueueAsync(new ProcessDocument(target.TenantId, outcome.DocumentId, outcome.Stage), ct);
        return TypedResults.Accepted($"/api/v1/documents/{id}", await FreshDetailAsync(db, id, configs, features, ct));
    }

    /// <summary>
    /// Why <c>:approve</c> refuses the document (docs/11 §11: a document without a definition or without captured data
    /// would be published empty). Only for a tenant with <c>configurableTypes</c>, so every other tenant approves as
    /// before; null when the document can be approved, or is in no state to be.
    /// </summary>
    private static async Task<ApprovalBlock?> ApprovalBlockAsync(Document document, IEffectiveConfigResolver configs, ITenantFeatures features, CancellationToken ct)
    {
        if (!DocumentStateMachine.CanTransition(document.Status, DocumentStatus.Approved)
            || !await features.IsEnabledAsync(document.TenantId, TenantFeature.ConfigurableTypes, ct))
        {
            return null;
        }

        if (document.Type.IsUnknown)
        {
            return new ApprovalBlock(ApprovalBlockCodes.TypeRequired, "The document has no type, so nothing defines what it holds; set its type and capture its values before approving it.");
        }

        var config = await configs.ResolveAsync(new EffectiveConfigRequest(document.TenantId, document.Type, document.SupplierId, document.DocumentTypeVersionId), ct);
        if (config.Type is null)
        {
            return new ApprovalBlock(ApprovalBlockCodes.NoDefinition, $"{document.Type} has no published definition this tenant can use; set another type, or publish this one, before approving.");
        }

        // No tree, or a tree that holds no value (what :set-type with prefill none writes: every field Missing, every
        // repeating group empty) is no data: approving it would publish an empty document.
        return HoldsData(document)
            ? null
            : new ApprovalBlock(ApprovalBlockCodes.NoData, "Nothing was captured for the document yet: none of its fields holds a value. Fill its fields (or set its type with prefill) before approving it.");
    }

    /// <summary>
    /// Whether the stored canonical tree holds a value (<see cref="DefinitionTree.HoldsValue"/>); the coded invoice
    /// record's <c>kind</c> is the record's own and does not count. A tree that does not parse counts as data: the gate
    /// does not decide on what it cannot read.
    /// </summary>
    private static bool HoldsData(Document document)
    {
        if (string.IsNullOrWhiteSpace(document.CanonicalJson))
        {
            return false;
        }

        try
        {
            var tree = JsonNode.Parse(document.CanonicalJson);
            return DefinitionTree.HoldsValue(tree, document.CanonicalSchemaVersion == DefinitionTree.SchemaVersion ? null : CodedRecordKeys);
        }
        catch (System.Text.Json.JsonException)
        {
            return true;
        }
    }

    /// <summary>The members of the coded records that are not read from the document.</summary>
    private static readonly string[] CodedRecordKeys = ["kind"];

    private static ProblemHttpResult ApprovalRefused(Document document, ApprovalBlock block) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Cannot approve",
            detail: $"Document {document.Id}: {block.Message}",
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = block.Code });

    /// <summary>A 409 unless a reviewer may act on the document now (NeedsReview, Validated or Failed).</summary>
    internal static ProblemHttpResult? NotInReview(Document document, string action) =>
        Correctable.Contains(document.Status)
            ? null
            : TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Document not in review",
                detail: DocumentStateMachine.IsSettled(document.Status) || document.Status == DocumentStatus.Cancelled
                    ? $"Document {document.Id} is {document.Status}; reprocess it before you {action}."
                    : $"Document {document.Id} is {document.Status}: the pipeline is still writing it. You can {action} once it is NeedsReview, Validated or Failed.");

    /// <summary>
    /// A reviewer changed the type a tenant fallback rule decided (docs/11 §6.6 <c>Overridden</c>): the rule counts it, and
    /// the document no longer pins the rule's version (a human's type is not the rule's).
    /// </summary>
    internal static async Task CountRuleOverrideAsync(DocProcDbContext db, Document document, DocumentTypeKey key, CancellationToken ct)
    {
        if (document.IdentificationRuleVersionId is not { } pinned)
        {
            return;
        }

        if (document.Type != key)
        {
            var ruleId = await db.TypeIdentificationRuleVersions.AsNoTracking().Where(v => v.Id == pinned).Select(v => (Guid?)v.RuleId).FirstOrDefaultAsync(ct);
            if (ruleId is { } overridden)
            {
                await db.TypeIdentificationRules.Where(r => r.Id == overridden).ExecuteUpdateAsync(u => u.SetProperty(r => r.Overridden, r => r.Overridden + 1), ct);
            }
        }

        document.IdentificationRuleVersionId = null;
    }

    internal static void ApplyType(Document document, DocumentTypeKey key, EffectiveType type, DateTimeOffset now)
    {
        document.Type = key;
        document.TypeSource = TypeSource.Human;
        document.TypeConfidence = 1.0;
        document.DocumentTypeVersionId = type.VersionId;
        document.UpdatedAt = now;
        // What the document is resolved with from now on (docs/11 §6.2): a tree of this type is kept by a later Extract.
        document.AppliedConfigJson = new JsonObject
        {
            ["typeVersionId"] = type.VersionId?.ToString(),
            ["typeKey"] = type.TypeKey,
            ["typeVersion"] = type.Version,
            ["template"] = $"{type.TemplateKey}@{type.TemplateVersion}",
            ["typeSource"] = nameof(TypeSource.Human),
            ["resolvedAt"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
        }.ToJsonString();
    }

    /// <summary>The data the document held for its old type, and the list columns from it.</summary>
    private static void ClearCaptured(Document document)
    {
        document.CanonicalJson = null;
        document.CanonicalSchemaVersion = null;
        document.ExtractionMethod = null;
        document.DocumentNumber = null;
        document.DocumentNumberNormalized = null;
        document.IssueDate = null;
        document.Currency = null;
        document.TotalGross = null;
        document.ValidFrom = null;
        document.ValidUntil = null;
        document.LineCount = null;
    }

    internal static void AddSetTypeCorrection(DocProcDbContext db, Document document, object previous, object next, string actor, DateTimeOffset now) =>
        db.Corrections.Add(new Correction
        {
            TenantId = document.TenantId,
            DocumentId = document.Id,
            FieldPath = "type",
            Op = CorrectionOps.SetType,
            OldValueJson = System.Text.Json.JsonSerializer.Serialize(previous, CorrectionJson),
            NewValueJson = System.Text.Json.JsonSerializer.Serialize(next, CorrectionJson),
            DocumentTypeVersionId = document.DocumentTypeVersionId,
            By = actor,
            At = now,
        });

    private static readonly System.Text.Json.JsonSerializerOptions CorrectionJson = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static void Assign(DocProcDbContext db, Document document, Supplier supplier, bool created, string actor, DateTimeOffset now, SupplierSourceRule? remembered = null)
    {
        var previous = document.SupplierId;
        document.SupplierId = supplier.Id;
        document.SupplierMatchConfidence = 1.0;
        document.SupplierMatchMethod = SupplierMatchMethod.Human;
        document.SupplierSourceRuleId = null;
        document.UpdatedAt = now;
        // The reviewer decided the supplier: what routing and Match said about it is answered (docs/11 §7.2, §7.4).
        ResolveOpenFlags(document, [FlagCodes.SupplierUnmatched, FlagCodes.SupplierConflict, FlagCodes.SupplierAmbiguous, FlagCodes.SupplierSourceUnverified], actor, now);
        DocumentQueries.AddEvent(db, document, DocumentEventTypes.SupplierAssigned, null, actor, new
        {
            from = previous,
            to = supplier.Id,
            supplierName = supplier.Name,
            created,
            rememberedRuleId = remembered?.Id,
            rememberedRuleEnabled = remembered?.Enabled,
        }, now);
    }

    /// <summary>Marks the document's open flags of <paramref name="codes"/> resolved by the reviewer whose action answered them.</summary>
    private static void ResolveOpenFlags(Document document, IReadOnlyCollection<string> codes, string actor, DateTimeOffset now)
    {
        foreach (var flag in document.Flags.Where(f => f.ResolvedAt is null && codes.Contains(f.Code)))
        {
            flag.ResolvedAt = now;
            flag.ResolvedBy = actor;
        }
    }

    private static void DropOpenPipelineFlags(DocProcDbContext db, Document document, IReadOnlyCollection<string> codes)
    {
        foreach (var stale in document.Flags.Where(f => f.CreatedBy == FlagOrigin.Pipeline && f.ResolvedAt is null && codes.Contains(f.Code)).ToList())
        {
            document.Flags.Remove(stale);
            db.DocumentFlags.Remove(stale);
        }
    }

    /// <summary>
    /// The checks of the validate stage over the document as it is now (docs/11 §12.8 inline validation), after a
    /// review action changed its type or supplier: the flags they own are replaced, so a duplicate the new supplier
    /// reveals, or the fields the new type requires, show at once. Nothing to check without a type or data.
    /// </summary>
    private static async Task RecheckAsync(DocProcDbContext db, Document document, IEffectiveConfigResolver configs, DocumentValidator validator, IPipelineConfigResolver pipelines, DateTimeOffset now, CancellationToken ct)
    {
        if (document.Type.IsUnknown || string.IsNullOrWhiteSpace(document.CanonicalJson))
        {
            return;
        }

        var config = await configs.ResolveAsync(new EffectiveConfigRequest(document.TenantId, document.Type, document.SupplierId, document.DocumentTypeVersionId), ct);
        if (config.Type is { } type)
        {
            await RecheckAsync(db, document, type, validator, pipelines, now, ct);
        }
    }

    private static async Task RecheckAsync(DocProcDbContext db, Document document, EffectiveType type, DocumentValidator validator, IPipelineConfigResolver pipelines, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(document.CanonicalJson))
        {
            return;
        }

        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == document.TenantId, ct);
        type = await DocumentChecks.WithPipelineAsync(pipelines, document, type, ct);
        var run = await DocumentChecks.RunAsync(db, document, type, tenant, DocumentChecks.TodayFor(tenant, now), validator, ct);
        DocumentChecks.ReplaceFlags(db, document, DocumentChecks.OwnedFlags, run.Flags, now);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Saves the tracked changes; false when another writer changed the document since it was read (its version token moved).</summary>
    private static async Task<bool> TrySaveAsync(DocProcDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    /// <summary>The detail as stored now, with the flag counts the action changed.</summary>
    internal static async Task<DocumentDetail> FreshDetailAsync(DocProcDbContext db, Guid id, IEffectiveConfigResolver configs, ITenantFeatures features, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var row = await DocumentQueries.FindForDetailAsync(db, id, track: false, ct) ?? throw new InvalidOperationException($"Document {id} vanished after a review action.");
        return await DetailAsync(row, configs, features, ct);
    }
}
