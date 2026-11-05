using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Pipeline.Routing;

/// <summary>A source rule in words, for the rule list, the routing test and the document timeline (docs/11 §7.2).</summary>
public static class SourceRuleText
{
    /// <summary>The rule in words: "From rechnungsversand@nordwind.example (DMARC required) → Nordwind, Exclusive, pins Invoice".</summary>
    public static string Describe(SupplierSourceRule rule, string? supplierName, IReadOnlyDictionary<Guid, string> channels)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(channels);
        var parts = new List<string>();
        if (rule.ChannelId is { } channel)
        {
            parts.Add($"channel {(channels.TryGetValue(channel, out var name) ? name : channel.ToString())}");
        }

        if (rule.FolderPrefix is not null)
        {
            parts.Add($"folder {rule.FolderPrefix}");
        }

        if (rule.Recipient is not null)
        {
            parts.Add($"to {rule.Recipient}");
        }

        if (rule.Sender is not null)
        {
            parts.Add($"from {rule.Sender}");
        }

        if (rule.SenderDomain is not null)
        {
            parts.Add($"from @{rule.SenderDomain}");
        }

        if (rule.SubjectRegex is not null)
        {
            parts.Add($"subject /{rule.SubjectRegex}/");
        }

        if (rule.FileNameRegex is not null)
        {
            parts.Add($"file /{rule.FileNameRegex}/");
        }

        var auth = rule.RequireSenderAuth && (rule.Sender is not null || rule.SenderDomain is not null) ? " (DMARC required)" : string.Empty;
        var pin = rule.PinTypeKey is null ? string.Empty : $", pins {rule.PinTypeKey}";
        var off = rule.Enabled ? string.Empty : " [disabled]";
        return $"{string.Join(", ", parts)}{auth} → {supplierName ?? rule.SupplierId.ToString()}, {rule.Mode}{pin}{off}";
    }
}
