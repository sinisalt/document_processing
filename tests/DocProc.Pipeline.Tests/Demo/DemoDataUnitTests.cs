using DocProc.Application.Abstractions;
using DocProc.Infrastructure.Persistence;
using DocProc.Llm.Gateway;
using DocProc.Llm.Prompts;
using DocProc.Pipeline.Demo;

namespace DocProc.Pipeline.Tests.Demo;

/// <summary>The pure parts of the demo data (docs/09 "Demo data"): the synthetic usage and the repository path lookup.</summary>
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
