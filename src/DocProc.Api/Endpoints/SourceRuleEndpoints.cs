using System.Security.Claims;
using System.Text.RegularExpressions;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Application.Configuration;
using DocProc.Application.Routing;
using DocProc.Application.Suppliers;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using DocProc.Pipeline.Routing;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

/// <summary>A supplier source rule (docs/11 §6.5) with its conditions in their normalised form and a sentence describing it.</summary>
public sealed record SourceRuleResponse(
    Guid Id,
    Guid SupplierId,
    Guid? ChannelId,
    string? Recipient,
    string? Sender,
    string? SenderDomain,
    string? FolderPrefix,
    string? SubjectRegex,
    string? FileNameRegex,
    string Mode,
    bool RequireSenderAuth,
    int Priority,
    string? PinTypeKey,
    Guid? ReceivingEntityId,
    bool Enabled,
    Guid? CreatedFromDocumentId,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Describe);

/// <summary>
/// The body of <c>POST</c> and <c>PUT /suppliers/{id}/source-rules</c> (docs/11 §6.5): the conditions, all of which must
/// hold (at least one of <c>channelId</c>, <c>recipient</c>, <c>sender</c> and <c>senderDomain</c>; a
/// <c>folderPrefix</c> needs its <c>channelId</c>), and what the rule decides. <c>mode</c> is <c>Exclusive</c> (the
/// default) or <c>DefaultWhenNoContentMatch</c>; <c>requireSenderAuth</c> defaults to true for a sender rule (decision
/// 1 of docs/11 §24), or to the supplier's <c>settings.requireSenderAuth</c>.
/// </summary>
public sealed record SourceRuleRequest(
    Guid? ChannelId = null,
    string? Recipient = null,
    string? Sender = null,
    string? SenderDomain = null,
    string? FolderPrefix = null,
    string? SubjectRegex = null,
    string? FileNameRegex = null,
    string? Mode = null,
    bool? RequireSenderAuth = null,
    int? Priority = null,
    string? PinTypeKey = null,
    Guid? ReceivingEntityId = null,
    bool? Enabled = null);

/// <summary>The sender authentication a routing test assumes: the SPF, DKIM and DMARC results and the DKIM signing domains.</summary>
public sealed record RoutingTestSenderAuth(string? Spf = null, string? Dkim = null, string? Dmarc = null, IReadOnlyList<string>? DkimDomains = null);

/// <summary>The source facts of <c>POST /routing:test</c> (docs/11 §18): what an ingestion would carry.</summary>
public sealed record RoutingTestRequest(
    Guid? ChannelId = null,
    string? Sender = null,
    IReadOnlyList<string>? Recipients = null,
    string? Mailbox = null,
    string? FolderPath = null,
    string? Subject = null,
    string? FileName = null,
    RoutingTestSenderAuth? SenderAuth = null);

/// <summary>How one rule fared in a routing test.</summary>
public sealed record RoutingTestCandidate(Guid RuleId, Guid SupplierId, string? SupplierName, bool Matched, bool Unverified, int Specificity, int Priority, string Mode, IReadOnlyList<string> Held, IReadOnlyList<string> Failed, string Describe);

/// <summary>
/// The answer of <c>POST /routing:test</c>: the supplier and how it was found (<c>sourceRule</c>, <c>channelDefault</c>),
/// the winning rule, its mode and confidence, the type and entity the source decides, whether the top candidates tie
/// (<c>ambiguous</c>, no supplier), the rules skipped for an unauthenticated sender that would have won
/// (<c>SUPPLIER_SOURCE_UNVERIFIED</c>), every rule looked at with the conditions that held and failed, and a sentence.
/// </summary>
public sealed record RoutingTestResponse(
    Guid? SupplierId,
    string? SupplierName,
    string? Method,
    Guid? RuleId,
    string? Mode,
    double? Confidence,
    string? PinTypeKey,
    string? PinTypeFrom,
    Guid? ReceivingEntityId,
    bool Ambiguous,
    IReadOnlyList<Guid> AmbiguousSupplierIds,
    bool SenderVerified,
    IReadOnlyList<Guid> UnverifiedRuleIds,
    IReadOnlyList<string> Flags,
    IReadOnlyList<RoutingTestCandidate> Candidates,
    string Explanation);

/// <summary>
/// Supplier source rules (docs/11 §6.5) and the routing test (§18), behind the <c>sourceRouting</c> switch: reads need
/// <c>Read</c>, rule writes <c>ConfigAdmin</c>. Conditions are stored normalised; two rules of a tenant with the same
/// conditions (whichever suppliers they name) are refused (409).
/// </summary>
public static class SourceRuleEndpoints
{
    public const int MaxRulesPerSupplier = 100;
    private static readonly TimeSpan RegexCheckTimeout = TimeSpan.FromMilliseconds(100);

    public static RouteGroupBuilder MapSourceRules(this RouteGroupBuilder group)
    {
        var rules = group.MapGroup("/suppliers/{supplierId:guid}/source-rules").WithTags("Suppliers");
        rules.MapGet("/", ListAsync).RequireAuthorization(Policies.Read).WithName("ListSourceRules")
            .WithSummary("The supplier's source rules (docs/11 §6.5), by priority, then oldest first.");
        rules.MapPost("/", CreateAsync).RequireAuthorization(Policies.ConfigAdmin).WithName("CreateSourceRule")
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024))
            .WithSummary("Adds a source rule: conditions on the channel, recipient, sender, sender domain, folder, subject and file name that route a document to the supplier, with its mode, sender authentication, priority, pinned type and receiving entity.");
        rules.MapGet("/{ruleId:guid}", GetAsync).RequireAuthorization(Policies.Read).WithName("GetSourceRule")
            .WithSummary("One source rule of the supplier.");
        rules.MapPut("/{ruleId:guid}", ReplaceAsync).RequireAuthorization(Policies.ConfigAdmin).WithName("ReplaceSourceRule")
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024))
            .WithSummary("Replaces a source rule (the body of POST); it routes new ingestions from then on.");
        rules.MapDelete("/{ruleId:guid}", DeleteAsync).RequireAuthorization(Policies.ConfigAdmin).WithName("DeleteSourceRule")
            .WithSummary("Deletes a source rule; the documents it routed keep their supplier and name it in their timeline.");

        group.MapPost("/routing:test", TestAsync).RequireAuthorization(Policies.Read).WithTags("Suppliers").WithName("TestRouting")
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024))
            .WithSummary("Dry-runs the tenant's source rules against a source (channel, sender, recipients, mailbox, folder, subject, file name, sender authentication) and explains the result: winner, runners-up, conflicts and the type (docs/11 §18).");
        return group;
    }

    private static async Task<Results<Ok<IReadOnlyList<SourceRuleResponse>>, ProblemHttpResult>> ListAsync(Guid supplierId, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await SupplierConfigSupport.GuardAsync(tenant, features, "Source rules", ct) is { } problem)
        {
            return problem;
        }

        var supplier = await db.Suppliers.AsNoTracking().Where(s => s.Id == supplierId).Select(s => s.Name).FirstOrDefaultAsync(ct);
        if (supplier is null)
        {
            return SupplierGroupEndpoints.NotFound("Supplier", supplierId);
        }

        var rows = await db.SupplierSourceRules.AsNoTracking().Where(r => r.SupplierId == supplierId).ToListAsync(ct);
        var channels = await ChannelNamesAsync(db, rows, ct);
        return TypedResults.Ok<IReadOnlyList<SourceRuleResponse>>([.. rows
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.CreatedAt)
            .Select(r => Response(r, supplier, channels))]);
    }

    private static async Task<Results<Ok<SourceRuleResponse>, ProblemHttpResult>> GetAsync(Guid supplierId, Guid ruleId, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await SupplierConfigSupport.GuardAsync(tenant, features, "Source rules", ct) is { } problem)
        {
            return problem;
        }

        var row = await db.SupplierSourceRules.AsNoTracking().Include(r => r.Supplier).SingleOrDefaultAsync(r => r.Id == ruleId && r.SupplierId == supplierId, ct);
        return row is null ? SupplierGroupEndpoints.NotFound("Source rule", ruleId) : TypedResults.Ok(Response(row, row.Supplier?.Name, await ChannelNamesAsync(db, [row], ct)));
    }

    private static async Task<Results<Created<SourceRuleResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(Guid supplierId, SourceRuleRequest request, ITenantContext tenant, ClaimsPrincipal user, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, TimeProvider clock, CancellationToken ct)
    {
        if (await SupplierConfigSupport.GuardAsync(tenant, features, "Source rules", ct) is { } problem)
        {
            return problem;
        }

        var supplier = await db.Suppliers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == supplierId, ct);
        if (supplier is null)
        {
            return SupplierGroupEndpoints.NotFound("Supplier", supplierId);
        }

        if (await db.SupplierSourceRules.CountAsync(r => r.SupplierId == supplierId, ct) >= MaxRulesPerSupplier)
        {
            return Conflict("Too many rules", $"A supplier has at most {MaxRulesPerSupplier} source rules.");
        }

        var now = clock.GetUtcNow();
        var rule = new SupplierSourceRule
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.TenantId!.Value,
            SupplierId = supplierId,
            ConditionHash = string.Empty,
            CreatedBy = DocumentQueries.ActorName(user),
            CreatedAt = now,
            UpdatedAt = now,
        };
        var outcome = await ApplyAsync(db, rule, request, supplier, ct);
        if (outcome is not null)
        {
            return outcome.Invalid is { } invalid ? invalid : outcome.Problem!;
        }

        db.SupplierSourceRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/suppliers/{supplierId}/source-rules/{rule.Id}", Response(rule, supplier.Name, await ChannelNamesAsync(db, [rule], ct)));
    }

    private static async Task<Results<Ok<SourceRuleResponse>, ValidationProblem, ProblemHttpResult>> ReplaceAsync(Guid supplierId, Guid ruleId, SourceRuleRequest request, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, TimeProvider clock, CancellationToken ct)
    {
        if (await SupplierConfigSupport.GuardAsync(tenant, features, "Source rules", ct) is { } problem)
        {
            return problem;
        }

        var rule = await db.SupplierSourceRules.Include(r => r.Supplier).SingleOrDefaultAsync(r => r.Id == ruleId && r.SupplierId == supplierId, ct);
        if (rule?.Supplier is not { } supplier)
        {
            return SupplierGroupEndpoints.NotFound("Source rule", ruleId);
        }

        var outcome = await ApplyAsync(db, rule, request, supplier, ct);
        if (outcome is not null)
        {
            return outcome.Invalid is { } invalid ? invalid : outcome.Problem!;
        }

        rule.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(Response(rule, supplier.Name, await ChannelNamesAsync(db, [rule], ct)));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid supplierId, Guid ruleId, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await SupplierConfigSupport.GuardAsync(tenant, features, "Source rules", ct) is { } problem)
        {
            return problem;
        }

        var rule = await db.SupplierSourceRules.SingleOrDefaultAsync(r => r.Id == ruleId && r.SupplierId == supplierId, ct);
        if (rule is null)
        {
            return SupplierGroupEndpoints.NotFound("Source rule", ruleId);
        }

        db.SupplierSourceRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<RoutingTestResponse>, ValidationProblem, ProblemHttpResult>> TestAsync(RoutingTestRequest request, ITenantContext tenant, [FromServices] DocProcDbContext db, [FromServices] ITenantFeatures features, CancellationToken ct)
    {
        if (await SupplierConfigSupport.GuardAsync(tenant, features, "Routing tests", ct) is { } problem)
        {
            return problem;
        }

        var errors = new Errors()
            .Optional("sender", request.Sender, SupplierSourceRule.MaxAddressLength)
            .Optional("mailbox", request.Mailbox, SupplierSourceRule.MaxAddressLength)
            .Optional("folderPath", request.FolderPath, SupplierSourceRule.MaxFolderPrefixLength)
            .Optional("subject", request.Subject, 1000)
            .Optional("fileName", request.FileName, 260);
        if (request.Recipients is { Count: > 50 })
        {
            errors.Add("recipients", "At most 50 recipients.");
        }

        if (request.FolderPath is not null && SourceConditions.Folder(request.FolderPath) is null)
        {
            errors.Add("folderPath", "folderPath is relative to the channel root, without '.' or '..' segments.");
        }

        IngestionChannel? channel = null;
        if (request.ChannelId is { } channelId)
        {
            channel = await db.IngestionChannels.AsNoTracking().SingleOrDefaultAsync(c => c.Id == channelId, ct);
            if (channel is null)
            {
                errors.Add("channelId", $"No channel {channelId} in this tenant.");
            }
        }

        if (errors.ToProblem() is { } invalid)
        {
            return invalid;
        }

        var sender = SourceConditions.Address(request.Sender);
        var auth = request.SenderAuth is { } given
            ? new SenderAuthentication
            {
                Spf = given.Spf?.Trim().ToLowerInvariant(),
                Dkim = given.Dkim?.Trim().ToLowerInvariant(),
                Dmarc = given.Dmarc?.Trim().ToLowerInvariant(),
                DkimDomains = given.DkimDomains?.Select(SourceConditions.Domain).Where(d => d is not null).Select(d => d!).ToList(),
                FromDomain = SourceConditions.DomainOf(sender),
            }
            : SenderAuthentication.None(SourceConditions.DomainOf(sender));
        var source = SourceContext.Create(request.ChannelId, request.Sender, request.Recipients, request.Mailbox, request.FolderPath, request.Subject, request.FileName, auth);
        var candidates = await SourceRuleCandidates.LoadAsync(db, source, ct);
        var result = SupplierSourceResolver.Resolve(source, candidates, channel);

        var supplierIds = candidates.Select(c => c.SupplierId).Append(result.SupplierId ?? Guid.Empty).Concat(result.AmbiguousSupplierIds).Distinct().ToList();
        var names = await db.Suppliers.AsNoTracking().Where(s => supplierIds.Contains(s.Id)).Select(s => new { s.Id, s.Name, s.SettingsJson, Group = s.Group != null ? s.Group.SettingsJson : null }).ToListAsync(ct);
        string? NameOf(Guid? id) => names.FirstOrDefault(n => n.Id == id)?.Name;
        var channels = await ChannelNamesAsync(db, candidates, ct);

        var pin = result.PinTypeKey;
        var pinFrom = result.PinTypeFrom;
        if (pin is null && names.FirstOrDefault(n => n.Id == result.SupplierId) is { } routed
            && SupplierSettings.Parse(routed.SettingsJson).Over(SupplierSettings.Parse(routed.Group)).PinnedType(sourceMatched: true) is { } allowed)
        {
            pin = allowed;
            pinFrom = "supplierSettings";
        }

        var flags = new List<string>();
        if (result.UnverifiedWinners.Count > 0)
        {
            flags.Add(Domain.Flags.FlagCodes.SupplierSourceUnverified);
        }

        if (result.Ambiguous)
        {
            flags.Add(Domain.Flags.FlagCodes.SupplierAmbiguous);
        }

        var ordered = result.Evaluations
            .OrderByDescending(e => e.Matched)
            .ThenByDescending(e => e.Unverified)
            .ThenByDescending(e => e.Specificity)
            .ThenByDescending(e => e.Rule.Priority)
            .ThenBy(e => e.Rule.Id)
            .Select(e => new RoutingTestCandidate(e.Rule.Id, e.Rule.SupplierId, NameOf(e.Rule.SupplierId), e.Matched, e.Unverified, e.Specificity, e.Rule.Priority, e.Rule.Mode.ToString(), e.Held, e.Failed, Describe(e.Rule, NameOf(e.Rule.SupplierId), channels)))
            .ToList();
        var explanation = result switch
        {
            { Ambiguous: true } => $"Rules of {string.Join(" and ", result.AmbiguousSupplierIds.Select(id => NameOf(id) ?? id.ToString()))} match equally (same specificity and priority): no supplier is assigned and SUPPLIER_AMBIGUOUS is raised.",
            { Rule: { } rule } => $"{NameOf(rule.SupplierId)} by the rule \"{Describe(rule, NameOf(rule.SupplierId), channels)}\" ({rule.Mode}, confidence {result.Confidence:0.00}).",
            { Method: SupplierMatchMethod.ChannelDefault } => $"No rule matches; the channel's default supplier {NameOf(result.SupplierId)} applies as DefaultWhenNoContentMatch.",
            _ => "No rule matches and the channel has no default supplier: the Match stage looks for the supplier in the content.",
        };
        if (result.UnverifiedWinners.Count > 0)
        {
            explanation += $" A rule of {NameOf(result.UnverifiedWinners[0].Rule.SupplierId)} would have matched, but the sender is not authenticated (DMARC): SUPPLIER_SOURCE_UNVERIFIED.";
        }

        if (pin is not null)
        {
            explanation += $" The type is pinned to {pin} ({pinFrom}).";
        }

        return TypedResults.Ok(new RoutingTestResponse(
            result.SupplierId,
            NameOf(result.SupplierId),
            result.Method switch
            {
                SupplierMatchMethod.SourceRule => "sourceRule",
                SupplierMatchMethod.ChannelDefault => "channelDefault",
                SupplierMatchMethod.Hint => "hint",
                _ => null,
            },
            result.Rule?.Id,
            result.Mode?.ToString(),
            result.Confidence,
            pin,
            pinFrom,
            result.ReceivingEntityId,
            result.Ambiguous,
            result.AmbiguousSupplierIds,
            auth.Verified,
            [.. result.UnverifiedWinners.Select(u => u.Rule.Id)],
            flags,
            ordered,
            explanation));
    }

    /// <summary>Why <see cref="ApplyAsync"/> refused a rule: a validation problem, or a conflict.</summary>
    internal sealed record RuleRefusal(ValidationProblem? Invalid, ProblemHttpResult? Problem);

    /// <summary>
    /// Checks <paramref name="request"/> and writes it onto <paramref name="rule"/> (normalised, with its condition hash);
    /// null when it was applied, else why not. The channel and the receiving entity must be the tenant's, the type key a
    /// key, the regular expressions valid; a sender domain may not be a free-mail provider's, and no other rule of the
    /// tenant may have the same conditions.
    /// </summary>
    internal static async Task<RuleRefusal?> ApplyAsync(DocProcDbContext db, SupplierSourceRule rule, SourceRuleRequest request, Supplier supplier, CancellationToken ct)
    {
        var errors = new Errors()
            .Optional("recipient", request.Recipient, SupplierSourceRule.MaxAddressLength)
            .Optional("sender", request.Sender, SupplierSourceRule.MaxAddressLength)
            .Optional("senderDomain", request.SenderDomain, SupplierSourceRule.MaxDomainLength)
            .Optional("folderPrefix", request.FolderPrefix, SupplierSourceRule.MaxFolderPrefixLength)
            .Optional("subjectRegex", request.SubjectRegex, SupplierSourceRule.MaxRegexLength)
            .Optional("fileNameRegex", request.FileNameRegex, SupplierSourceRule.MaxRegexLength)
            .Optional("pinTypeKey", request.PinTypeKey, 64);
        var recipient = SourceConditions.Address(request.Recipient);
        var sender = SourceConditions.Address(request.Sender);
        var senderDomain = SourceConditions.Domain(request.SenderDomain);
        string? folder = null;
        if (request.FolderPrefix is not null)
        {
            folder = SourceConditions.Folder(request.FolderPrefix);
            if (folder is null or { Length: 0 })
            {
                errors.Add("folderPrefix", "folderPrefix is a folder relative to the channel root, without '.' or '..' segments (for example lieferanten/nordwind/).");
            }

            if (request.ChannelId is null)
            {
                errors.Add("folderPrefix", "A folderPrefix is relative to a channel's root: give its channelId.");
            }
        }

        if (recipient is not null && SourceConditions.DomainOf(recipient) is null)
        {
            errors.Add("recipient", "recipient is an e-mail address.");
        }

        if (sender is not null && SourceConditions.DomainOf(sender) is null)
        {
            errors.Add("sender", "sender is an e-mail address.");
        }

        if (senderDomain is not null)
        {
            if (!senderDomain.Contains('.', StringComparison.Ordinal) || senderDomain.Contains('@', StringComparison.Ordinal))
            {
                errors.Add("senderDomain", "senderDomain is a domain name such as nordwind.example.");
            }
            else if (SourceConditions.FreeMailDomains.Contains(senderDomain))
            {
                errors.Add("senderDomain", $"{senderDomain} is a free-mail provider: a domain rule would claim every private sender there. Use a sender rule with the exact address.");
            }
        }

        if (request.ChannelId is null && recipient is null && sender is null && senderDomain is null)
        {
            errors.Add("conditions", "A rule needs at least one of channelId, recipient, sender and senderDomain (subjectRegex and fileNameRegex only narrow them).");
        }

        CheckRegex(errors, "subjectRegex", request.SubjectRegex);
        CheckRegex(errors, "fileNameRegex", request.FileNameRegex);

        var mode = SourceRuleMode.Exclusive;
        if (request.Mode is not null && !TryParseEnum(request.Mode, out mode))
        {
            errors.Add("mode", "mode is Exclusive or DefaultWhenNoContentMatch.");
        }

        if (request.PinTypeKey is not null && !DocumentTypeKey.TryParse(request.PinTypeKey, out _))
        {
            errors.Add("pinTypeKey", $"'{request.PinTypeKey}' is not a type key.");
        }

        if (request.Priority is { } priority && priority is < -1000 or > 1000)
        {
            errors.Add("priority", "priority is between -1000 and 1000.");
        }

        if (request.ChannelId is { } channelId && !await db.IngestionChannels.AnyAsync(c => c.Id == channelId, ct))
        {
            errors.Add("channelId", $"No channel {channelId} in this tenant.");
        }

        if (request.ReceivingEntityId is { } entityId && !await db.ReceivingEntities.AnyAsync(e => e.Id == entityId, ct))
        {
            errors.Add("receivingEntityId", $"No receiving entity {entityId} in this tenant.");
        }

        if (errors.ToProblem() is { } invalid)
        {
            return new RuleRefusal(invalid, null);
        }

        var subject = string.IsNullOrWhiteSpace(request.SubjectRegex) ? null : request.SubjectRegex;
        var fileName = string.IsNullOrWhiteSpace(request.FileNameRegex) ? null : request.FileNameRegex;
        var hash = SourceConditions.Hash(request.ChannelId, recipient, sender, senderDomain, folder, subject, fileName);
        if (await db.SupplierSourceRules.Where(r => r.Id != rule.Id && r.ConditionHash == hash).Select(r => new { r.SupplierId }).FirstOrDefaultAsync(ct) is { } existing)
        {
            var owner = existing.SupplierId == supplier.Id ? "this supplier" : "another supplier";
            return new RuleRefusal(null, Conflict("Rule exists", $"A source rule with exactly these conditions already exists for {owner}; two rules of a tenant cannot claim the same conditions."));
        }

        var settings = SupplierSettings.Parse(supplier.SettingsJson);
        rule.ChannelId = request.ChannelId;
        rule.Recipient = recipient;
        rule.Sender = sender;
        rule.SenderDomain = senderDomain;
        rule.FolderPrefix = folder;
        rule.SubjectRegex = subject;
        rule.FileNameRegex = fileName;
        rule.Mode = mode;
        rule.RequireSenderAuth = request.RequireSenderAuth ?? settings.RequireSenderAuth ?? true;
        rule.Priority = request.Priority ?? 0;
        rule.PinTypeKey = request.PinTypeKey is null ? null : DocumentTypeKey.Parse(request.PinTypeKey).Value;
        rule.ReceivingEntityId = request.ReceivingEntityId;
        rule.Enabled = request.Enabled ?? true;
        rule.ConditionHash = hash;
        return null;
    }

    private static void CheckRegex(Errors errors, string field, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return;
        }

        try
        {
            _ = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, RegexCheckTimeout);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            errors.Add(field, $"{field} is not a regular expression the non-backtracking engine runs: {e.Message}");
        }
    }

    private static async Task<Dictionary<Guid, string>> ChannelNamesAsync(DocProcDbContext db, IEnumerable<SupplierSourceRule> rules, CancellationToken ct)
    {
        var ids = rules.Where(r => r.ChannelId is not null).Select(r => r.ChannelId!.Value).Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.IngestionChannels.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
    }

    internal static SourceRuleResponse Response(SupplierSourceRule r, string? supplierName, IReadOnlyDictionary<Guid, string> channels) =>
        new(r.Id, r.SupplierId, r.ChannelId, r.Recipient, r.Sender, r.SenderDomain, r.FolderPrefix, r.SubjectRegex, r.FileNameRegex, r.Mode.ToString(), r.RequireSenderAuth, r.Priority, r.PinTypeKey, r.ReceivingEntityId, r.Enabled, r.CreatedFromDocumentId, r.CreatedBy, r.CreatedAt, r.UpdatedAt, Describe(r, supplierName, channels));

    /// <summary>The rule in words (<see cref="DocProc.Pipeline.Routing.SourceRuleText.Describe"/>).</summary>
    internal static string Describe(SupplierSourceRule r, string? supplierName, IReadOnlyDictionary<Guid, string> channels) =>
        DocProc.Pipeline.Routing.SourceRuleText.Describe(r, supplierName, channels);
}
