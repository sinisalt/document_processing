using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Llm.Gateway;
using DocProc.Llm.Prompts;
using DocProc.Pipeline.Demo;

namespace DocProc.Pipeline.Tests.Demo;

/// <summary>The pure parts of the demo data (docs/09 "Demo data"): the synthetic usage, the resume stages and the repository path lookup.</summary>
public sealed class DemoDataUnitTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Usage_covers_two_weeks_for_both_tenants_with_two_failures_whatever_the_weekday(int dayOffset)
    {
        var now = new DateTimeOffset(2026, 10, 26, 15, 30, 0, TimeSpan.Zero).AddDays(dayOffset);
        var rows = DemoData.Usage(PromptCatalogue.Load(), PriceTable.Default, now);

        rows.ShouldAllBe(r => r.At <= now && r.At > now.AddDays(-DemoData.UsageDays));
        rows.Select(r => r.TenantId).Distinct().ShouldBe([DevSeed.TenantA, DevSeed.TenantB], ignoreOrder: true);
        rows.Select(r => DateOnly.FromDateTime(r.At.UtcDateTime)).Distinct().Count().ShouldBe(DemoData.UsageDays);
        rows.Count(r => r.Error is not null).ShouldBe(2);
        rows.Where(r => r.Error is null).ShouldAllBe(r => r.EstimatedCostUsd > 0 && r.InputTokens > 0);
        rows.Select(r => r.ProviderRequestId).ShouldBeUnique();
        rows.ShouldAllBe(r => r.ProviderRequestId!.StartsWith(DemoData.UsageMarker) && r.RequestHash.Length == 64);
    }

    [Fact]
    public void Usage_rows_lie_on_the_utc_day_they_stand_for_shortly_after_midnight()
    {
        var now = new DateTimeOffset(2026, 10, 30, 0, 10, 0, TimeSpan.Zero);
        var rows = DemoData.Usage(PromptCatalogue.Load(), PriceTable.Default, now);

        rows.ShouldAllBe(r => r.At <= now);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var days = rows.Select(r => DateOnly.FromDateTime(r.At.UtcDateTime)).Distinct().Order().ToList();
        days.ShouldBe(Enumerable.Range(0, DemoData.UsageDays).Select(i => today.AddDays(i - DemoData.UsageDays + 1)));

        // The days before today start at 08:00 UTC; today's calls, still in the future at 00:10, are clamped to now.
        rows.Where(r => DateOnly.FromDateTime(r.At.UtcDateTime) < today).ShouldAllBe(r => r.At.UtcDateTime.TimeOfDay >= DemoData.UsageDayStart);
        rows.Where(r => DateOnly.FromDateTime(r.At.UtcDateTime) == today).ShouldNotBeEmpty();
        rows.Where(r => DateOnly.FromDateTime(r.At.UtcDateTime) == today).ShouldAllBe(r => r.At == now);
    }

    [Fact]
    public void Usage_ignores_the_offset_of_now_and_anchors_to_the_utc_date()
    {
        var utc = new DateTimeOffset(2026, 10, 29, 23, 50, 0, TimeSpan.Zero);
        var berlin = utc.ToOffset(TimeSpan.FromHours(1));

        DemoData.Usage(PromptCatalogue.Load(), PriceTable.Default, berlin).Select(r => (r.ProviderRequestId, r.At))
            .ShouldBe(DemoData.Usage(PromptCatalogue.Load(), PriceTable.Default, utc).Select(r => (r.ProviderRequestId, r.At)));
    }

    [Theory]
    [InlineData(DocumentStatus.Received, PipelineStage.Normalize)]
    [InlineData(DocumentStatus.Normalized, PipelineStage.Split)]
    [InlineData(DocumentStatus.Split, PipelineStage.Classify)]
    [InlineData(DocumentStatus.Classified, PipelineStage.Match)]
    [InlineData(DocumentStatus.Matched, PipelineStage.Extract)]
    [InlineData(DocumentStatus.Extracted, PipelineStage.Validate)]
    [InlineData(DocumentStatus.Validated, null)]
    [InlineData(DocumentStatus.NeedsReview, null)]
    [InlineData(DocumentStatus.AutoApproved, null)]
    [InlineData(DocumentStatus.Approved, null)]
    [InlineData(DocumentStatus.Rejected, null)]
    [InlineData(DocumentStatus.Failed, null)]
    public void An_interrupted_document_resumes_at_the_stage_whose_handler_accepts_its_status(DocumentStatus status, PipelineStage? stage) =>
        DemoData.ResumeStage(status).ShouldBe(stage);

    [Fact]
    public void Repository_paths_are_found_above_the_content_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "docproc-paths", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "src", "DocProc.Api");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(Path.Combine(root, "fixtures", "demo"));
        try
        {
            RepositoryPaths.ResolveUpwards("fixtures/demo/llm-replay", project).ShouldBe(Path.Combine(root, "fixtures", "demo", "llm-replay"));
            RepositoryPaths.ResolveUpwards("./fixtures/demo", project).ShouldBe(Path.Combine(root, "fixtures", "demo"));
            RepositoryPaths.ResolveUpwards("missing/folder", project).ShouldBe(Path.Combine(project, "missing", "folder"));
            RepositoryPaths.ResolveUpwards(Path.Combine(root, "elsewhere"), project).ShouldBe(Path.Combine(root, "elsewhere"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
