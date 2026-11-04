namespace DocProc.Llm.Gateway;

/// <summary>The periods an LLM budget covers (docs/11 §16 "LLM cost"): a UTC day, a UTC month, or one document.</summary>
public enum LlmBudgetPeriod
{
    Day,
    Month,
    Document,
}

/// <summary>
/// One of a tenant's LLM budgets: the most it spends on LLM calls in a period, for every stage or for one
/// (<paramref name="Stage"/>), in USD as the price table estimates the calls.
/// </summary>
public sealed record LlmBudget(LlmBudgetPeriod Period, decimal LimitUsd, LlmStage? Stage = null);

/// <summary>
/// What the guard decided for one call: allowed, or refused with the budget that would be exceeded, what was spent in
/// its period so far and the estimate of the call.
/// </summary>
public sealed record LlmBudgetVerdict(bool Allowed, LlmBudget? Budget = null, decimal SpentUsd = 0m, decimal EstimateUsd = 0m)
{
    public static LlmBudgetVerdict Allow { get; } = new(true);

    /// <summary>What a refusal says (for the flag and the timeline).</summary>
    public string Describe() => Budget is { } budget
        ? $"The {Name(budget.Period)} LLM budget of {budget.LimitUsd:0.00} USD{(budget.Stage is { } stage ? $" for {stage}" : string.Empty)} would be exceeded: {SpentUsd:0.0000} USD spent, the call is estimated at {EstimateUsd:0.0000} USD."
        : "The LLM budget would be exceeded.";

    private static string Name(LlmBudgetPeriod period) => period switch
    {
        LlmBudgetPeriod.Day => "daily",
        LlmBudgetPeriod.Month => "monthly",
        _ => "per-document",
    };
}

/// <summary>
/// The tenant's LLM budgets (docs/11 §16): asked by the gateway before every call. A call that would take the spend of
/// a period (or of the document) past its budget is not made: the gateway throws <see cref="LlmBudgetExceededException"/>
/// and the stage goes on without the LLM. Spend is counted from the usage rows (<c>LlmUsageEntry</c>).
/// </summary>
public interface ILlmBudgetGuard
{
    Task<LlmBudgetVerdict> CheckAsync(Guid tenantId, LlmStage stage, Guid? documentId, CancellationToken cancellationToken);
}

/// <summary>The guard without budgets: every call is allowed (hosts without a database, and the default).</summary>
public sealed class NoLlmBudgets : ILlmBudgetGuard
{
    public static NoLlmBudgets Instance { get; } = new();

    public Task<LlmBudgetVerdict> CheckAsync(Guid tenantId, LlmStage stage, Guid? documentId, CancellationToken cancellationToken) =>
        Task.FromResult(LlmBudgetVerdict.Allow);
}

/// <summary>A call refused by the tenant's LLM budget (<see cref="LlmErrorKind.BudgetExceeded"/>); nothing was sent.</summary>
public sealed class LlmBudgetExceededException(LlmBudgetVerdict verdict)
    : LlmException(LlmErrorKind.BudgetExceeded, verdict?.Describe() ?? "The LLM budget would be exceeded.")
{
    public LlmBudgetVerdict Verdict { get; } = verdict ?? new LlmBudgetVerdict(false);
}
