using System.Collections.Frozen;

namespace DocProc.Llm.Gateway;

/// <summary>USD per million tokens. Kept in configuration in production; these are the defaults from docs/06 (checked 2026-10-30).</summary>
public sealed record ModelPrice(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite)
{
    public static ModelPrice Standard(decimal input, decimal output) => new(input, output, input * 0.1m, input * 1.25m);
}

/// <summary>Estimates cost from usage. We cannot see a tenant's provider invoice, so estimates are what the dashboards show.</summary>
public sealed class PriceTable(IReadOnlyDictionary<string, ModelPrice> prices, ModelPrice? fallback = null)
{
    public static PriceTable Default { get; } = new(new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-opus-5-5"] = ModelPrice.Standard(4m, 20m),
        ["claude-sonnet-5-5"] = ModelPrice.Standard(2m, 10m),
        ["claude-haiku-5-5"] = ModelPrice.Standard(0.10m, 0.50m),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));

    public bool TryGet(string model, out ModelPrice price)
    {
        if (prices.TryGetValue(model, out var p))
        {
            price = p;
            return true;
        }

        // Azure deployment names and dated snapshots often embed the model id: "docproc-claude-opus-5-5-eu".
        foreach (var (key, value) in prices)
        {
            if (model.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                price = value;
                return true;
            }
        }

        if (fallback is not null)
        {
            price = fallback;
            return true;
        }

        price = default!;
        return false;
    }

    /// <summary>Estimated USD for a call, or null when the model is not priced (the row is still recorded).</summary>
    public decimal? Estimate(string model, LlmUsage usage, bool batch = false)
    {
        if (!TryGet(model, out var price))
        {
            return null;
        }

        const decimal million = 1_000_000m;
        var cost = (usage.InputTokens * price.Input
                    + (usage.OutputTokens + usage.ReasoningTokens) * price.Output
                    + usage.CacheReadTokens * price.CacheRead
                    + usage.CacheWriteTokens * price.CacheWrite) / million;
        return batch ? cost / 2 : cost;
    }
}
