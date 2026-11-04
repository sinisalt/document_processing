using System.Text.RegularExpressions;
using DocProc.Application.Routing;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Pipeline.Routing;

/// <summary>
/// The source facts one item is routed by (docs/11 §7.1): the ingestion's channel, sender, recipients, mailbox, folder
/// and subject, which all items of an ingestion share, plus the item's file name. Values are normalised by
/// <see cref="Create"/> (<see cref="SourceConditions"/>).
/// </summary>
public sealed record SourceContext(
    Guid? ChannelId,
    string? Sender,
    IReadOnlyList<string> Recipients,
    string? Mailbox,
    string? FolderPath,
    string? Subject,
    string? FileName,
    SenderAuthentication? SenderAuth,
    Guid? SupplierIdHint = null,
    string? TypeHint = null)
{
    public string? SenderDomain => SourceConditions.DomainOf(Sender);

    /// <summary>The recipients and the mailbox together: what a <c>recipient</c> condition is compared with.</summary>
    public IEnumerable<string> Addresses => Mailbox is null ? Recipients : Recipients.Append(Mailbox).Distinct(StringComparer.Ordinal);

    public static SourceContext Create(
        Guid? channelId,
        string? sender,
        IEnumerable<string>? recipients,
        string? mailbox,
        string? folderPath,
        string? subject,
        string? fileName,
        SenderAuthentication? senderAuth,
        Guid? supplierIdHint = null,
        string? typeHint = null) =>
        new(
            channelId,
            SourceConditions.Address(sender),
            [.. (recipients ?? []).Select(SourceConditions.Address).Where(a => a is not null).Select(a => a!).Distinct(StringComparer.Ordinal)],
            SourceConditions.Address(mailbox),
            folderPath is null ? null : SourceConditions.Folder(folderPath),
            subject,
            fileName,
            senderAuth,
            supplierIdHint,
            typeHint);
}

/// <summary>How one rule fared against a source (the explanation <c>routing:test</c> shows).</summary>
/// <param name="Rule">The rule.</param>
/// <param name="Matched">Every condition held (and the sender is authenticated where the rule requires it).</param>
/// <param name="Specificity">The weighted number of conditions that held (see <see cref="SupplierSourceResolver"/>).</param>
/// <param name="Held">The conditions that held (<c>sender</c>, <c>folderPrefix</c>, ...).</param>
/// <param name="Failed">The conditions that did not hold; empty for a matching rule.</param>
/// <param name="Unverified">Every condition held but the sender is not authenticated and the rule requires it: skipped.</param>
public sealed record RuleEvaluation(
    SupplierSourceRule Rule,
    bool Matched,
    bool Unverified,
    int Specificity,
    IReadOnlyList<string> Held,
    IReadOnlyList<string> Failed);

/// <summary>
/// The result of routing one item (docs/11 §7.2): the supplier and how it was found, the rule, the type and entity the
/// source decides, and what was skipped or tied. <see cref="Ambiguous"/> means the top two candidates named different
/// suppliers with equal standing, so no supplier was assigned.
/// </summary>
public sealed record RoutingResult(
    Guid? SupplierId,
    SupplierMatchMethod? Method,
    SupplierSourceRule? Rule,
    SourceRuleMode? Mode,
    double? Confidence,
    string? PinTypeKey,
    string? PinTypeFrom,
    Guid? ReceivingEntityId,
    bool Ambiguous,
    IReadOnlyList<Guid> AmbiguousSupplierIds,
    IReadOnlyList<RuleEvaluation> Evaluations)
{
    /// <summary>The rules that would have won but were skipped for an unauthenticated sender (<c>SUPPLIER_SOURCE_UNVERIFIED</c>).</summary>
    public IReadOnlyList<RuleEvaluation> UnverifiedWinners { get; init; } = [];

    /// <summary>The candidates that matched, best first.</summary>
    public IEnumerable<RuleEvaluation> Ranked => Evaluations.Where(e => e.Matched).OrderByDescending(e => e.Specificity).ThenByDescending(e => e.Rule.Priority).ThenBy(e => e.Rule.Id);
}

/// <summary>
/// <c>ISupplierSourceResolver</c> of docs/11 §7.2, a pure function over the source facts, the candidate rules (loaded
/// by the caller through the indexed lookups), the channel's defaults and an authorised hint:
/// <list type="number">
/// <item>An authorised hint wins (method <c>Hint</c>, confidence 0.99, authoritative like an <c>Exclusive</c> rule).</item>
/// <item>Rules whose conditions all hold become candidates. Sender conditions need an authenticated sender when the
/// rule says <c>requireSenderAuth</c> (decision 1 of §24: by default); otherwise the rule is skipped and, when it would
/// have won, reported for <c>SUPPLIER_SOURCE_UNVERIFIED</c>.</item>
/// <item>Specificity: sender 4, recipient 3, folder prefix 3 plus its depth in segments, file name 2, subject 2,
/// channel 1, sender domain 1; then <c>priority</c> (higher first).</item>
/// <item>No candidate: the channel's default supplier as <c>DefaultWhenNoContentMatch</c> (confidence 0.7).</item>
/// <item>The top two candidates name different suppliers with equal specificity and priority: nothing is assigned
/// (<c>SUPPLIER_AMBIGUOUS</c>).</item>
/// </list>
/// A rule's confidence is 0.99 for <c>Exclusive</c> and 0.7 for <c>DefaultWhenNoContentMatch</c>. The type is the
/// winning rule's <c>pinTypeKey</c>, else the channel's default type; the entity the rule's, else the channel's.
/// Regular expressions run non-backtracking with a 100 ms timeout; a timeout is a condition that does not hold.
/// </summary>
public static class SupplierSourceResolver
{
    public const double ExclusiveConfidence = 0.99;
    public const double DefaultConfidence = 0.7;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static RoutingResult Resolve(
        SourceContext source,
        IReadOnlyList<SupplierSourceRule> rules,
        IngestionChannel? channel,
        bool hintAuthorised = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);

        var evaluations = rules.Where(r => r.Enabled).OrderBy(r => r.Id).Select(r => Evaluate(r, source)).ToList();
        var channelType = channel?.DefaultDocumentTypeKey;
        var channelEntity = channel?.DefaultReceivingEntityId;

        if (hintAuthorised && source.SupplierIdHint is { } hinted)
        {
            return new RoutingResult(
                hinted, SupplierMatchMethod.Hint, null, SourceRuleMode.Exclusive, ExclusiveConfidence,
                source.TypeHint ?? channelType, source.TypeHint is not null ? "hint" : channelType is not null ? "channel" : null,
                channelEntity, false, [], evaluations);
        }

        var ranked = evaluations.Where(e => e.Matched)
            .OrderByDescending(e => e.Specificity)
            .ThenByDescending(e => e.Rule.Priority)
            .ThenBy(e => e.Rule.Id)
            .ToList();
        var best = ranked.FirstOrDefault();
        var unverified = evaluations.Where(e => e.Unverified && (best is null || Outranks(e, best) >= 0 && e.Rule.SupplierId != best.Rule.SupplierId)).ToList();

        if (best is not null)
        {
            var tied = ranked.Skip(1).Where(e => Outranks(e, best) == 0 && e.Rule.SupplierId != best.Rule.SupplierId).ToList();
            if (tied.Count > 0)
            {
                return new RoutingResult(
                    null, null, null, null, null, channelType, channelType is null ? null : "channel", channelEntity, true,
                    [.. tied.Select(t => t.Rule.SupplierId).Prepend(best.Rule.SupplierId).Distinct().Order()], evaluations)
                { UnverifiedWinners = unverified };
            }

            var rule = best.Rule;
            var pin = rule.PinTypeKey ?? channelType;
            return new RoutingResult(
                rule.SupplierId, SupplierMatchMethod.SourceRule, rule, rule.Mode,
                rule.Mode == SourceRuleMode.Exclusive ? ExclusiveConfidence : DefaultConfidence,
                pin, rule.PinTypeKey is not null ? "rule" : channelType is not null ? "channel" : null,
                rule.ReceivingEntityId ?? channelEntity, false, [], evaluations)
            { UnverifiedWinners = unverified };
        }

        if (channel?.DefaultSupplierId is { } fallback)
        {
            return new RoutingResult(
                fallback, SupplierMatchMethod.ChannelDefault, null, SourceRuleMode.DefaultWhenNoContentMatch, DefaultConfidence,
                channelType, channelType is null ? null : "channel", channelEntity, false, [], evaluations)
            { UnverifiedWinners = unverified };
        }

        return new RoutingResult(null, null, null, null, null, channelType, channelType is null ? null : "channel", channelEntity, false, [], evaluations)
        { UnverifiedWinners = unverified };
    }

    /// <summary>Positive when <paramref name="a"/> ranks above <paramref name="b"/>, zero for a tie (specificity, then priority).</summary>
    private static int Outranks(RuleEvaluation a, RuleEvaluation b)
    {
        var bySpecificity = a.Specificity.CompareTo(b.Specificity);
        return bySpecificity != 0 ? bySpecificity : a.Rule.Priority.CompareTo(b.Rule.Priority);
    }

    /// <summary>Evaluates one rule against the source: which conditions hold, its specificity, and whether only sender authentication is missing.</summary>
    public static RuleEvaluation Evaluate(SupplierSourceRule rule, SourceContext source)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(source);
        var held = new List<string>();
        var failed = new List<string>();
        var specificity = 0;

        void Check(string name, bool present, bool holds, int weight)
        {
            if (!present)
            {
                return;
            }

            if (holds)
            {
                held.Add(name);
                specificity += weight;
            }
            else
            {
                failed.Add(name);
            }
        }

        Check("channel", rule.ChannelId is not null, rule.ChannelId == source.ChannelId, 1);
        Check("sender", rule.Sender is not null, rule.Sender == source.Sender, 4);
        Check("senderDomain", rule.SenderDomain is not null, rule.SenderDomain == source.SenderDomain, 1);
        Check("recipient", rule.Recipient is not null, rule.Recipient is not null && source.Addresses.Contains(rule.Recipient, StringComparer.Ordinal), 3);
        Check(
            "folderPrefix",
            rule.FolderPrefix is not null,
            rule.FolderPrefix is not null && source.FolderPath is not null && SourceConditions.UnderPrefix(source.FolderPath, rule.FolderPrefix),
            3 + (rule.FolderPrefix is null ? 0 : SourceConditions.Depth(rule.FolderPrefix)));
        Check("subjectRegex", rule.SubjectRegex is not null, Matches(rule.SubjectRegex, source.Subject), 2);
        Check("fileNameRegex", rule.FileNameRegex is not null, Matches(rule.FileNameRegex, source.FileName), 2);

        var conditionsHold = failed.Count == 0 && held.Count > 0;
        var needsAuth = rule.RequireSenderAuth && (rule.Sender is not null || rule.SenderDomain is not null);
        var authenticated = source.SenderAuth?.Verified == true;
        var unverified = conditionsHold && needsAuth && !authenticated;
        if (unverified)
        {
            failed.Add("senderAuth");
        }

        return new RuleEvaluation(rule, conditionsHold && !unverified, unverified, specificity, held, failed);
    }

    private static bool Matches(string? pattern, string? input)
    {
        if (pattern is null || input is null)
        {
            return false;
        }

        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false; // A pattern stored outside the API that does not compile never holds.
        }
    }
}
