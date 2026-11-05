namespace DocProc.Llm.Gateway;

/// <summary>
/// Which model a tenant's stage resolves to when the price table has no price for it (docs/11 §16, Correction (P5)
/// fix 4). A cost limit counted from the recorded costs (the llm step's <c>maxCostPerDocument</c>) cannot hold for such a
/// model, whose calls are recorded without a cost, so the caller refuses the call instead of letting it through.
/// </summary>
public sealed class LlmPriceCheck(ILlmPolicyStore policies, PriceTable prices)
{
    /// <summary>The unpriced model the stage would call, or null when it is priced or no provider resolves (the gateway then fails as before).</summary>
    public async Task<string?> UnpricedModelAsync(Guid tenantId, LlmStage stage, CancellationToken cancellationToken)
    {
        var setup = await policies.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        ResolvedProvider resolved;
        try
        {
            resolved = LlmConfigResolver.Resolve(stage, setup.Policy, setup.TenantConfigs, setup.PlatformDefault);
        }
        catch (LlmException)
        {
            return null;
        }

        return prices.TryGet(resolved.Model, out _) ? null : resolved.Model;
    }
}
