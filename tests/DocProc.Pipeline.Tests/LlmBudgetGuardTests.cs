using DocProc.Domain.Documents;
using DocProc.Infrastructure.Llm;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm;
using DocProc.Llm.Gateway;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Pipeline.Tests;

/// <summary>
/// <see cref="EfLlmBudgetGuard"/> (docs/11 §16, P5): the spend of a UTC day, a UTC month or a document counted from the
/// usage rows, a budget for one stage or every stage, and the estimate of the next call from the stage's recent calls.
/// </summary>
public sealed class LlmBudgetGuardTests
{
    private static readonly Guid TenantA = PipelineTestHost.TenantA;
    private static readonly Guid TenantB = DevSeed.TenantB;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_budget_allows_every_call()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var guard = Guard(host);

        (await guard.CheckAsync(TenantA, LlmStage.Extract, Guid.NewGuid(), Ct)).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task The_day_counts_from_utc_midnight_and_the_month_from_the_first()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var now = host.Clock.GetUtcNow();
        await UsageAsync(host, TenantA, LlmStage.Extract, 0.40m, now.AddHours(-1));
        await UsageAsync(host, TenantA, LlmStage.Extract, 0.50m, now.AddDays(-1));
        await UsageAsync(host, TenantB, LlmStage.Extract, 5m, now.AddHours(-1));
        await BudgetAsync(host, LlmBudgetPeriod.Day, 1m);
        var guard = Guard(host);

        (await EfLlmBudgetGuard.SpentAsync(await DbAsync(host), TenantA, new LlmBudget(LlmBudgetPeriod.Day, 1m), null, now, Ct)).ShouldBe(0.40m);
        (await EfLlmBudgetGuard.SpentAsync(await DbAsync(host), TenantA, new LlmBudget(LlmBudgetPeriod.Month, 1m), null, now, Ct)).ShouldBe(0.90m);

        // 0.40 spent today, the next Extract call is estimated at the average of the last calls (0.45): 0.85 <= 1.
        (await guard.CheckAsync(TenantA, LlmStage.Extract, null, Ct)).Allowed.ShouldBeTrue();

        await UsageAsync(host, TenantA, LlmStage.Extract, 0.40m, now.AddMinutes(-5));
        var refused = await guard.CheckAsync(TenantA, LlmStage.Extract, null, Ct);
        refused.Allowed.ShouldBeFalse("0.80 spent today plus an estimate of 0.43 is past 1.00");
        refused.Budget!.Period.ShouldBe(LlmBudgetPeriod.Day);
        refused.SpentUsd.ShouldBe(0.80m);
        refused.EstimateUsd.ShouldBeGreaterThan(0m);
        (await guard.CheckAsync(TenantB, LlmStage.Extract, null, Ct)).Allowed.ShouldBeTrue("tenant B has no budget");
    }

    [Fact]
    public async Task A_stage_budget_counts_and_stops_only_its_stage()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var now = host.Clock.GetUtcNow();
        await UsageAsync(host, TenantA, LlmStage.Classify, 2m, now.AddMinutes(-1));
        await BudgetAsync(host, LlmBudgetPeriod.Month, 1m, LlmStage.Extract);
        var guard = Guard(host);

        (await guard.CheckAsync(TenantA, LlmStage.Extract, null, Ct)).Allowed.ShouldBeTrue("the classifier's spend does not count against the Extract budget");
        (await guard.CheckAsync(TenantA, LlmStage.Classify, null, Ct)).Allowed.ShouldBeTrue("the Extract budget does not stop the classifier");
    }

    [Fact]
    public async Task A_document_budget_counts_that_document_s_calls_and_a_zero_budget_refuses_at_once()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var now = host.Clock.GetUtcNow();
        var expensive = Guid.NewGuid();
        await UsageAsync(host, TenantA, LlmStage.Extract, 0.30m, now.AddMinutes(-1), expensive);
        await BudgetAsync(host, LlmBudgetPeriod.Document, 0.25m);
        var guard = Guard(host);

        (await guard.CheckAsync(TenantA, LlmStage.Extract, expensive, Ct)).Allowed.ShouldBeFalse();
        (await guard.CheckAsync(TenantA, LlmStage.Extract, Guid.NewGuid(), Ct)).Allowed.ShouldBeFalse("a fresh document's call is estimated at the stage's average call, 0.30, past 0.25");
        (await guard.CheckAsync(TenantA, LlmStage.Classify, Guid.NewGuid(), Ct)).Allowed.ShouldBeTrue("no classifier call yet: estimated at 0");
        (await guard.CheckAsync(TenantA, LlmStage.Classify, null, Ct)).Allowed.ShouldBeTrue("a call for no document has no document budget");

        await BudgetAsync(host, LlmBudgetPeriod.Day, 0m, LlmStage.Split);
        (await guard.CheckAsync(TenantA, LlmStage.Split, null, Ct)).Allowed.ShouldBeFalse("a budget of 0 keeps the stage from calling at all");
    }

    [Fact]
    public async Task Parallel_calls_hold_their_estimates_so_they_cannot_pass_the_budget_together()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var now = host.Clock.GetUtcNow();
        await UsageAsync(host, TenantA, LlmStage.Extract, 0.40m, now.AddHours(-1));
        await UsageAsync(host, TenantA, LlmStage.Extract, 0.50m, now.AddDays(-1));
        await BudgetAsync(host, LlmBudgetPeriod.Day, 1m);
        var guard = Guard(host);

        // 0.40 spent today, estimate 0.45: one call fits, two at the same time (0.40 + 0.45 + 0.45) do not.
        var reservations = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => guard.ReserveAsync(TenantA, LlmStage.Extract, null, Ct)));
        reservations.Count(r => r.Verdict.Allowed).ShouldBe(1, "the first allowed call's estimate counts for the others");
        guard.HeldCount(TenantA).ShouldBe(1);
        var refused = reservations.First(r => !r.Verdict.Allowed).Verdict;
        refused.SpentUsd.ShouldBe(0.85m, "the spend counts the held estimate");

        foreach (var reservation in reservations)
        {
            reservation.Dispose();
        }

        guard.HeldCount(TenantA).ShouldBe(0, "released once the call is recorded");
        using var next = await guard.ReserveAsync(TenantA, LlmStage.Extract, null, Ct);
        next.Verdict.Allowed.ShouldBeTrue();
        (await guard.ReserveAsync(TenantB, LlmStage.Extract, null, Ct)).Verdict.Allowed.ShouldBeTrue("another tenant's holds do not count");
        guard.HeldCount(TenantB).ShouldBe(0, "a tenant without budgets holds nothing");
    }

    /// <summary>
    /// A model the price table has no price for (P5 fix 4): its calls are recorded without a cost and would count as 0,
    /// so with a budget that applies the call is refused before it is made; without one it goes ahead as before.
    /// </summary>
    [Fact]
    public async Task An_unpriced_model_is_refused_while_a_budget_applies_and_allowed_without_one()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var now = host.Clock.GetUtcNow();
        for (var i = 0; i < 50; i++)
        {
            await UsageAsync(host, TenantA, LlmStage.Extract, null, now.AddMinutes(-1 - i));
        }

        await BudgetAsync(host, LlmBudgetPeriod.Day, 0.01m);
        var guard = Guard(host);

        (await EfLlmBudgetGuard.SpentAsync(await DbAsync(host), TenantA, new LlmBudget(LlmBudgetPeriod.Day, 0.01m), null, now, Ct)).ShouldBe(0m, "the unpriced calls count as 0");
        using var refused = await guard.ReserveAsync(TenantA, LlmStage.Extract, null, "acme-unlisted-model", Ct);
        refused.Verdict.Allowed.ShouldBeFalse();
        refused.Verdict.UnpricedModel.ShouldBe("acme-unlisted-model");
        refused.Verdict.Budget!.Period.ShouldBe(LlmBudgetPeriod.Day);
        refused.Verdict.Describe().ShouldContain("has no price");
        guard.HeldCount(TenantA).ShouldBe(0, "a refused call holds nothing");

        using var priced = await guard.ReserveAsync(TenantA, LlmStage.Extract, null, null, Ct);
        priced.Verdict.Allowed.ShouldBeTrue("a priced call is checked as before");
        using var unbudgeted = await guard.ReserveAsync(TenantB, LlmStage.Extract, null, "acme-unlisted-model", Ct);
        unbudgeted.Verdict.Allowed.ShouldBeTrue("tenant B has no budget, so nothing changes for it");
    }

    private static EfLlmBudgetGuard Guard(PipelineTestHost host) => new(host.Services.GetRequiredService<ISystemDbContextFactory>(), host.Clock);

    private static Task<DocProcDbContext> DbAsync(PipelineTestHost host) => Task.FromResult(host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped());

    private static async Task UsageAsync(PipelineTestHost host, Guid tenantId, LlmStage stage, decimal? cost, DateTimeOffset at, Guid? documentId = null)
    {
        await using var db = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped();
        db.LlmUsage.Add(new LlmUsageEntry
        {
            TenantId = tenantId,
            DocumentId = documentId,
            Stage = stage,
            ConfigId = Guid.NewGuid(),
            KeyOwner = KeyOwner.Platform,
            Provider = LlmProviderKind.Replay,
            Model = "test-model",
            PromptVersion = "p@1",
            StopReason = "end_turn",
            RequestHash = new string('a', 64),
            EstimatedCostUsd = cost,
            At = at,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task BudgetAsync(PipelineTestHost host, LlmBudgetPeriod period, decimal limit, LlmStage? stage = null)
    {
        await using var db = host.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped();
        db.LlmBudgets.Add(new LlmBudgetRecord { Id = Guid.CreateVersion7(), TenantId = TenantA, Period = period, Stage = stage, LimitUsd = limit, CreatedAt = PipelineTestHost.Now, UpdatedAt = PipelineTestHost.Now });
        await db.SaveChangesAsync(Ct);
    }
}
