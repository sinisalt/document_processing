using System.Text.RegularExpressions;
using DocProc.Infrastructure.Persistence;
using DocProc.Pipeline.Tests.Demo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Pipeline.Tests;

/// <summary>
/// SQL Server 2025 refuses <c>LIKE</c> on a <c>json</c> column ("JSON text is not properly formatted. Unexpected
/// character '%'"), which the SQLite-backed pipeline tests cannot see: the P4 demo in compose failed every warm-up
/// document in Validate because of one such <c>Contains</c>. No query may call Contains, StartsWith or EndsWith on a
/// property the model maps to a json column; such a filter is applied to the rows read instead.
/// </summary>
public sealed partial class JsonColumnQueryTests
{
    [GeneratedRegex(@"\b(?:[a-z]\w*)\.(?<property>[A-Z]\w*Json)!?\.(?:Contains|StartsWith|EndsWith)\(", RegexOptions.CultureInvariant)]
    private static partial Regex TextMatchOnProperty();

    [Fact]
    public async Task No_query_matches_text_inside_a_json_column()
    {
        await using var host = await PipelineTestHost.StartAsync();
        using var scope = host.CreateTenantScope(PipelineTestHost.TenantA);
        var db = scope.ServiceProvider.GetRequiredService<DocProcDbContext>();
        var jsonProperties = db.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => string.Equals(p.GetColumnType(), "json", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        jsonProperties.ShouldContain("DetailsJson");
        jsonProperties.ShouldContain("SourceJson");

        var offending = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(DemoFixtureTests.RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var lines = await File.ReadAllLinesAsync(file, TestContext.Current.CancellationToken);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in TextMatchOnProperty().Matches(lines[i]))
                {
                    // In a LINQ query (the line or the two before it start a Where or an Any), not on a string in memory.
                    var context = string.Join(' ', lines[Math.Max(0, i - 2)..(i + 1)]);
                    if (jsonProperties.Contains(match.Groups["property"].Value) && (context.Contains(".Where(", StringComparison.Ordinal) || context.Contains("Async(", StringComparison.Ordinal)))
                    {
                        offending.Add($"{Path.GetRelativePath(DemoFixtureTests.RepositoryRoot(), file)}:{i + 1}: {lines[i].Trim()}");
                    }
                }
            }
        }

        offending.ShouldBeEmpty();
    }
}
