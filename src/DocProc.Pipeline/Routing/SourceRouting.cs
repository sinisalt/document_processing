using System.Text.Json;
using DocProc.Application.Routing;
using DocProc.Application.Suppliers;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Pipeline.Routing;

/// <summary>Event names of source routing (docs/11 §7.2, §7.4); timeline only, never published.</summary>
public static class RoutingEventTypes
{
    /// <summary>The supplier (and type, entity) came from the source at ingest.</summary>
    public const string SupplierIdentifiedBySource = "document.supplier_identified_by_source";

    /// <summary>Content evidence replaced a source supplier that was only a default (<c>DefaultWhenNoContentMatch</c>).</summary>
    public const string SupplierReplacedByContent = "document.supplier_replaced_by_content";
}

/// <summary>Flag codes of source routing (docs/11 §7.2, §7.4, §6.3), registered in the catalogue of <see cref="FlagCodes"/>.</summary>
public static class RoutingFlags
{
    /// <summary>The flags the ingest-time routing owns: written once, at ingest, and carried to split parts.</summary>
    public static readonly string[] IngestOwned = [FlagCodes.SupplierSourceUnverified, FlagCodes.SupplierAmbiguous];
}

/// <summary>
/// Source routing at ingest (docs/11 §7.2): loads the candidate rules of a source through the indexed lookups (never
/// the whole rule table), runs <see cref="SupplierSourceResolver"/>, and applies the result to a new document: the
/// supplier with <c>SupplierMatchMethod</c>, the rule, the confidence and the receiving entity; a type pinned by the
/// rule, the channel or the supplier's single allowed type (<c>TypeSource=Supplier</c>); the flags
/// <c>SUPPLIER_SOURCE_UNVERIFIED</c> and <c>SUPPLIER_AMBIGUOUS</c>; the timeline event
/// <c>supplier_identified_by_source</c>; and the item's <c>RouteJson</c>. Runs only for tenants with
/// <c>sourceRouting</c> on; for everyone else ingest is unchanged.
/// </summary>
internal sealed class SourceRouting(DocProcDbContext db, DocumentTimeline timeline, DocumentFlagWriter flags)
{
    private readonly Dictionary<Guid, IngestionChannel?> _channels = [];
    private readonly Dictionary<Guid, SupplierSettings> _settings = [];

    /// <summary>Routes one item and applies the result to its new document; returns the flags and events added (to drop them with the document after a lost race).</summary>
    public async Task<IReadOnlyList<object>> ApplyAsync(Ingestion ingestion, IngestionItem item, Document document, CancellationToken cancellationToken)
    {
        var source = SourceRuleCandidates.ContextOf(ingestion, item);
        var rules = await SourceRuleCandidates.LoadAsync(db, source, cancellationToken).ConfigureAwait(false);
        var channel = await ChannelAsync(ingestion.ChannelId, cancellationToken).ConfigureAwait(false);
        var result = SupplierSourceResolver.Resolve(source, rules, channel);
        var added = new List<object>();

        // A hint or default may name a supplier that was deleted since; it is then not applied.
        Guid? supplierId = null;
        SupplierSettings settings = SupplierSettings.Empty;
        if (result.SupplierId is { } candidate && await SettingsAsync(candidate, cancellationToken).ConfigureAwait(false) is { } found)
        {
            supplierId = candidate;
            settings = found;
        }

        var pin = result.PinTypeKey;
        var pinFrom = result.PinTypeFrom;
        if (pin is null && supplierId is not null && settings.PinnedType(sourceMatched: true) is { } allowed)
        {
            pin = allowed;
            pinFrom = "supplierSettings";
        }

        if (supplierId is { } routed)
        {
            document.SupplierId = routed;
            document.SupplierMatchMethod = result.Method;
            document.SupplierSourceRuleId = result.Rule?.Id;
            document.SupplierMatchConfidence = result.Confidence;
        }

        document.ReceivingEntityId = result.ReceivingEntityId;
        if (pin is not null && DocumentTypeKey.TryParse(pin, out var pinned))
        {
            document.Type = pinned;
            document.TypeSource = TypeSource.Supplier;
            document.TypeConfidence = 1.0;
        }
        else
        {
            pin = null;
            pinFrom = null;
        }

        var raised = new List<DocumentFlag>();
        if (result.UnverifiedWinners.Count > 0)
        {
            var rule = result.UnverifiedWinners[0].Rule;
            raised.Add(FlagCodes.Create(
                FlagCodes.SupplierSourceUnverified,
                $"A source rule for another supplier would have matched, but the sender {source.Sender ?? "(none)"} is not authenticated (DMARC); the rule was skipped.",
                parameters: FlagParams.Of(("ruleId", rule.Id.ToString()), ("supplierId", rule.SupplierId.ToString()), ("sender", source.Sender))));
        }

        if (result.Ambiguous)
        {
            raised.Add(FlagCodes.Create(
                FlagCodes.SupplierAmbiguous,
                "Source rules of several suppliers match this source equally; no supplier was assigned.",
                parameters: FlagParams.Of(("supplierIds", string.Join(",", result.AmbiguousSupplierIds)))));
        }

        if (raised.Count > 0)
        {
            flags.Replace(document, RoutingFlags.IngestOwned, raised);
            added.AddRange(document.Flags.Where(f => raised.Any(r => r.Code == f.Code)));
        }

        var route = new
        {
            supplierId,
            method = result.Method?.ToString(),
            ruleId = result.Rule?.Id,
            mode = result.Mode?.ToString(),
            confidence = supplierId is null ? null : result.Confidence,
            pinTypeKey = pin,
            pinTypeFrom = pinFrom,
            receivingEntityId = result.ReceivingEntityId,
            ambiguous = result.Ambiguous,
            ambiguousSupplierIds = result.AmbiguousSupplierIds,
            unverifiedRuleIds = result.UnverifiedWinners.Select(u => u.Rule.Id).ToList(),
            senderVerified = source.SenderAuth?.Verified,
            candidates = result.Evaluations.Count,
        };
        item.RouteJson = PipelineJson.Serialize(route);
        if (supplierId is not null || pin is not null || result.ReceivingEntityId is not null || raised.Count > 0)
        {
            added.Add(timeline.Record(document, RoutingEventTypes.SupplierIdentifiedBySource, null, null, route));
        }

        return added;
    }

    private async Task<IngestionChannel?> ChannelAsync(Guid channelId, CancellationToken cancellationToken)
    {
        if (!_channels.TryGetValue(channelId, out var channel))
        {
            channel = await db.IngestionChannels.AsNoTracking().SingleOrDefaultAsync(c => c.Id == channelId, cancellationToken).ConfigureAwait(false);
            _channels[channelId] = channel;
        }

        return channel;
    }

    /// <summary>The supplier's settings over its group's, or null when the supplier does not exist (in this tenant).</summary>
    private async Task<SupplierSettings?> SettingsAsync(Guid supplierId, CancellationToken cancellationToken)
    {
        if (_settings.TryGetValue(supplierId, out var cached))
        {
            return cached;
        }

        var row = await db.Suppliers.AsNoTracking().Where(s => s.Id == supplierId)
            .Select(s => new { s.SettingsJson, GroupSettings = s.Group != null ? s.Group.SettingsJson : null })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var settings = SupplierSettings.Parse(row.SettingsJson).Over(SupplierSettings.Parse(row.GroupSettings));
        _settings[supplierId] = settings;
        return settings;
    }

}

/// <summary>The candidate rules of a source (docs/11 §7.2): looked up by the indexed condition values, never the whole table.</summary>
public static class SourceRuleCandidates
{
    /// <summary>The most candidate rules one source loads; the indexed lookups keep the real number small.</summary>
    public const int MaxCandidates = 500;

    /// <summary>The source facts of an ingestion's item, as the resolver reads them.</summary>
    public static SourceContext ContextOf(Ingestion ingestion, IngestionItem? item) =>
        SourceContext.Create(
            ingestion.ChannelId,
            ingestion.SourceSender,
            ReadRecipients(ingestion.SourceRecipientsJson),
            ingestion.SourceMailbox,
            ingestion.SourceFolderPath,
            ingestion.SourceSubject,
            item?.FileName ?? ingestion.SourceFileName,
            SenderAuthentication.Parse(ingestion.SenderAuthJson),
            ingestion.SupplierIdHint,
            ingestion.DocumentTypeHint);

    private static List<string> ReadRecipients(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The enabled rules that can hold for <paramref name="source"/>: by sender, sender domain, recipient or channel (every rule has one of them).</summary>
    public static Task<List<SupplierSourceRule>> LoadAsync(DocProcDbContext db, SourceContext source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(source);
        var sender = source.Sender;
        var domain = source.SenderDomain;
        var addresses = source.Addresses.ToList();
        var channel = source.ChannelId;
        return db.SupplierSourceRules.AsNoTracking()
            .Where(r => r.Enabled && ((sender != null && r.Sender == sender)
                                      || (domain != null && r.SenderDomain == domain)
                                      || (r.Recipient != null && addresses.Contains(r.Recipient))
                                      || (channel != null && r.ChannelId == channel)))
            .OrderBy(r => r.Id)
            .Take(MaxCandidates)
            .ToListAsync(cancellationToken);
    }
}
