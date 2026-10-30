using System.Globalization;
using System.Runtime.InteropServices;
using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm;
using DocProc.Llm.Gateway;
using DocProc.Llm.Prompts;
using DocProc.Llm.Providers;
using DocProc.Llm.Schema;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Pipeline.Demo;

/// <summary>A tenant member seeded for a development user (docs/09 "Demo data"); the subject is the dev issuer's.</summary>
public sealed record DemoMember(Guid TenantId, string Username, string Email, string DisplayName, TenantRole Role)
{
    public string UserId => DevSeed.SubjectFor(Username);
}

/// <summary>
/// The rows the demo data adds on top of <see cref="DevSeed"/> (docs/09 "Demo data"): the platform Replay provider that
/// serves <c>fixtures/demo/llm-replay</c>, the tenant members of the development users and two weeks of LLM usage for the
/// usage charts. They are added once, by the first start-up that finds no synthetic usage row (<see cref="IsAppliedAsync"/>):
/// the usage rows double as the marker that the demo data was applied, so a member removed through the members screen
/// or a Replay configuration deleted in the admin console stays removed on later starts.
/// </summary>
public static class DemoData
{
    /// <summary>The platform Replay configuration; active, so it serves the demo PDFs while no other platform configuration is.</summary>
    public static readonly Guid PlatformReplayConfig = Guid.Parse("00000000-0003-4000-8000-000000000002");

    /// <summary>Prefix of <see cref="LlmUsageEntry.ProviderRequestId"/> on the synthetic usage rows; real calls never carry it.</summary>
    public const string UsageMarker = "demo-usage-";

    public const string CreatedBy = "demo-data";

    /// <summary>Days of synthetic usage, today included.</summary>
    public const int UsageDays = 14;

    /// <summary>UTC time of day the synthetic calls of a day start from; the calls follow at fixed offsets.</summary>
    public static readonly TimeSpan UsageDayStart = TimeSpan.FromHours(8);

    /// <summary>
    /// What a replay miss says in the development mode in place of "re-record the fixture" (<see cref="LlmReplayMissHint"/>):
    /// only the demo documents have recorded answers, so any other document needs a real provider.
    /// </summary>
    public const string ReplayMissHint =
        "This document has no demo fixture: the development mode answers LLM calls from the recorded demo responses " +
        "(Llm:Replay:Directory, fixtures/demo/llm-replay), which cover only the demo documents. To process other " +
        "documents, store a key for the platform Anthropic configuration and enable it in the admin console (LLM " +
        "providers), or disable the Replay configuration there; see docs/09-local-development.md, \"Demo data\".";

    /// <summary>
    /// The platform the committed classification fixture was captured on. Its request hash covers the first page as
    /// PDFium renders it, and PDFium builds rasterise differently elsewhere, so on other platforms the delivery note gets
    /// a replay miss (docs/09 "Demo data"); the extraction fixture hashes the PDF bytes and replays everywhere.
    /// </summary>
    public const string FixtureCapturePlatform = "linux-x64";

    /// <summary>True on <see cref="FixtureCapturePlatform"/>, where the rendered-page hash of the classification fixture matches.</summary>
    public static bool IsFixtureCapturePlatform =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.OSArchitecture == Architecture.X64;

    /// <summary>The customer users of <c>DevUsers</c> (and the Keycloak realm), as members of their tenants; <c>DemoDataTests.Demo_members_are_the_customer_dev_users</c> keeps the two lists equal.</summary>
    public static readonly IReadOnlyList<DemoMember> Members =
    [
        new(DevSeed.TenantA, "tenant-a-admin", "admin@tenant-a.local", "Anna Admin", TenantRole.TenantAdmin),
        new(DevSeed.TenantA, "tenant-a-reviewer", "reviewer@tenant-a.local", "Rea Reviewer", TenantRole.Reviewer),
        new(DevSeed.TenantB, "tenant-b-admin", "admin@tenant-b.local", "Bo Admin", TenantRole.TenantAdmin),
        new(DevSeed.TenantB, "tenant-b-viewer", "viewer@tenant-b.local", "Vic Viewer", TenantRole.Viewer),
    ];

    /// <summary>True once the demo rows were added to this database: a synthetic usage row exists.</summary>
    public static Task<bool> IsAppliedAsync(DocProcDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.LlmUsage.AnyAsync(u => u.ProviderRequestId != null && u.ProviderRequestId.StartsWith(UsageMarker), cancellationToken);
    }

    /// <summary>
    /// The stage that continues a document an interrupted run left in <paramref name="status"/>: the one after the
    /// stage that leaves this status behind, which is the status that stage's handler accepts (Received: Normalize, …,
    /// Extracted: Validate). Null for the states no stage continues: Failed, the review and settled states, and
    /// Validated (Validate routes the document in the same commit, so the pipeline never persists Validated).
    /// </summary>
    public static PipelineStage? ResumeStage(DocumentStatus status)
    {
        foreach (var stage in Enum.GetValues<PipelineStage>())
        {
            if (PipelineStages.ResultingStatus(stage) == status)
            {
                return PipelineStages.Next(stage);
            }
        }

        return null;
    }

    /// <summary>
    /// The platform configuration the gateway falls back to, chosen like <c>EfLlmPolicyStore</c> does: an active one
    /// first, then the newest. Null when the database has none.
    /// </summary>
    public static async Task<LlmProviderConfigRecord?> PlatformConfigInUseAsync(DocProcDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var configs = await db.LlmProviderConfigs.AsNoTracking()
            .Where(c => c.Scope == ConfigScope.Platform && c.TenantId == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return configs
            .OrderByDescending(c => c.Status == ProviderConfigStatus.Active)
            .ThenByDescending(c => c.UpdatedAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// Adds the replay provider configuration when it is missing and no other platform configuration is active: a
    /// database that already runs on a real provider keeps it, which the gateway would otherwise give up for the newer
    /// Replay row. Returns the number of rows added.
    /// </summary>
    public static async Task<int> EnsureReplayProviderAsync(DocProcDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var platformActive = await db.LlmProviderConfigs.AnyAsync(
                c => c.Id == PlatformReplayConfig || (c.Scope == ConfigScope.Platform && c.TenantId == null && c.Status == ProviderConfigStatus.Active),
                cancellationToken)
            .ConfigureAwait(false);
        if (platformActive)
        {
            return 0;
        }

        db.LlmProviderConfigs.Add(new LlmProviderConfigRecord
        {
            Id = PlatformReplayConfig,
            Scope = ConfigScope.Platform,
            TenantId = null,
            Kind = LlmProviderKind.Replay,
            AuthType = LlmAuthType.ApiKey,
            SecretRef = null,
            // The same models as the platform Anthropic configuration: the model is part of the request hash, so the
            // replay fixtures were captured with exactly these names.
            Models = new Dictionary<string, string>(DevSeed.PlatformModels, StringComparer.Ordinal),
            DataResidency = DataResidency.Eu,
            Status = ProviderConfigStatus.Active,
            CreatedBy = CreatedBy,
            CreatedAt = now,
            UpdatedAt = now,
        });
        return 1;
    }

    /// <summary>Adds every missing member row (key: tenant and subject). Returns the number of rows added.</summary>
    public static async Task<int> EnsureMembersAsync(DocProcDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var added = 0;
        foreach (var member in Members)
        {
            var userId = member.UserId;
            if (await db.TenantMembers.AnyAsync(m => m.TenantId == member.TenantId && m.UserId == userId, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            db.TenantMembers.Add(new TenantMember
            {
                TenantId = member.TenantId,
                UserId = userId,
                Email = member.Email,
                DisplayName = member.DisplayName,
                Role = member.Role,
                CreatedAt = now,
            });
            added++;
        }

        return added;
    }

    /// <summary>Adds the synthetic usage rows unless any of them exists already. Returns the number of rows added.</summary>
    public static async Task<int> EnsureUsageAsync(DocProcDbContext db, PromptCatalogue prompts, PriceTable prices, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (await IsAppliedAsync(db, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        var rows = Usage(prompts, prices, now);
        db.LlmUsage.AddRange(rows);
        return rows.Count;
    }

    /// <summary>
    /// Two weeks of calls as the gateway records them (docs/08 "Cost attribution"), through the platform Anthropic
    /// configuration: tenant A classifies and extracts every working day with a quiet weekend, tenant B extracts a
    /// little, and two calls failed (a rate limit and an unavailable provider) so the error column has something to show. The
    /// pattern is a pure function of the UTC date, and every row lies on the UTC date it stands for (<see cref="At"/>).
    /// The rows are written once, so the history stays where the first start-up put it.
    /// </summary>
    public static IReadOnlyList<LlmUsageEntry> Usage(PromptCatalogue prompts, PriceTable prices, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(prices);

        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var calls = new List<(Guid TenantId, string TenantKey, int DaysAgo, int Index, LlmStage Stage)>();
        for (var daysAgo = UsageDays - 1; daysAgo >= 0; daysAgo--)
        {
            var day = today.AddDays(-daysAgo);
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var tenantACalls = weekend ? 1 : 3 + (daysAgo % 3);
            for (var i = 0; i < tenantACalls; i++)
            {
                calls.Add((DevSeed.TenantA, "a", daysAgo, i, i % 3 == 2 ? LlmStage.Classify : LlmStage.Extract));
            }

            if (!weekend && daysAgo % 2 == 0)
            {
                calls.Add((DevSeed.TenantB, "b", daysAgo, 0, LlmStage.Extract));
            }
        }

        // Exactly two failures whatever the weekdays: an early call of tenant A hit the rate limit, the latest call of
        // tenant B found the provider unavailable.
        var rateLimited = calls.FindIndex(c => c.TenantKey == "a" && c.DaysAgo < UsageDays - 3);
        var unavailable = calls.FindLastIndex(c => c.TenantKey == "b");
        return calls
            .Select((c, i) => Row(prompts, prices, c.TenantId, c.TenantKey, c.DaysAgo, c.Index, c.Stage, today, now,
                i == rateLimited ? LlmErrorKind.RateLimited : i == unavailable ? LlmErrorKind.Unavailable : null))
            .ToList();
    }

    /// <summary>
    /// When a synthetic call happened: its UTC date at <see cref="UsageDayStart"/> plus a fixed offset per call, so the
    /// row lies on the day it stands for whatever the time of the start-up (00:10 UTC included). Today's calls that
    /// would lie in the future are clamped to <paramref name="now"/>.
    /// </summary>
    private static DateTimeOffset At(DateTimeOffset today, int daysAgo, int index, int seed, DateTimeOffset now)
    {
        var at = today.AddDays(-daysAgo) + UsageDayStart + TimeSpan.FromMinutes((index * 83) + (seed % 17));
        return at > now ? now : at;
    }

    private static LlmUsageEntry Row(PromptCatalogue prompts, PriceTable prices, Guid tenantId, string tenantKey, int daysAgo, int index, LlmStage stage, DateTimeOffset today, DateTimeOffset now, LlmErrorKind? error)
    {
        var requestId = string.Create(CultureInfo.InvariantCulture, $"{UsageMarker}{tenantKey}-{daysAgo:00}-{index}");
        var seed = (daysAgo * 7) + (index * 13) + (tenantKey == "a" ? 0 : 5);

        var extract = stage == LlmStage.Extract;
        var model = DevSeed.PlatformModels[stage.ToString()];
        var usage = error is not null
            ? LlmUsage.Zero
            : extract
                ? new LlmUsage(InputTokens: 4200 + (seed % 9 * 410), OutputTokens: 850 + (seed % 7 * 95), CacheReadTokens: index > 0 ? 2600 : 0, CacheWriteTokens: index == 0 ? 2600 : 0, ReasoningTokens: 0)
                : new LlmUsage(InputTokens: 1500 + (seed % 5 * 120), OutputTokens: 30 + (seed % 4 * 6), CacheReadTokens: 0, CacheWriteTokens: 0, ReasoningTokens: 0);
        var prompt = extract ? prompts.ExtractInvoice : prompts.ClassifyDocument;

        return new LlmUsageEntry
        {
            TenantId = tenantId,
            DocumentId = null,
            Stage = stage,
            ConfigId = DevSeed.PlatformAnthropicConfig,
            KeyOwner = KeyOwner.Platform,
            Provider = LlmProviderKind.Anthropic,
            Model = model,
            PromptVersion = prompt.CacheKey,
            SchemaVersion = (extract ? CanonicalSchemas.InvoiceExtraction : CanonicalSchemas.Classification).ToString(),
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CacheReadTokens = usage.CacheReadTokens,
            CacheWriteTokens = usage.CacheWriteTokens,
            ReasoningTokens = usage.ReasoningTokens,
            EstimatedCostUsd = error is null ? prices.Estimate(model, usage) : 0m,
            LatencyMs = error is not null ? 350 + (seed % 5 * 40) : extract ? 6200 + (seed % 11 * 430) : 700 + (seed % 6 * 90),
            StopReason = error is null ? StopReasons.EndTurn : "error",
            ProviderRequestId = requestId,
            RequestHash = ContentHash.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(requestId)),
            Error = error,
            At = At(today, daysAgo, index, seed, now),
        };
    }
}
