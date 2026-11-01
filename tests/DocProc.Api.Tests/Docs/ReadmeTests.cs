using System.Text.RegularExpressions;
using DocProc.Api.Auth;

namespace DocProc.Api.Tests.Docs;

/// <summary>The README's statements that the code can check: the repository layout and the policies per role.</summary>
public sealed class ReadmeTests
{
    [Theory]
    [InlineData("src")]
    [InlineData("tests")]
    public void Repository_layout_lists_every_project(string folder)
    {
        var layout = Layout();
        var projects = Directory.GetDirectories(RepositoryFiles.Path(folder))
            .Select(Path.GetFileName)
            .Where(name => name!.StartsWith("DocProc.", StringComparison.Ordinal))
            .ToList();
        projects.ShouldNotBeEmpty();

        projects.Where(name => !layout.Contains($"  {name}/", StringComparison.Ordinal))
            .ShouldBeEmpty($"README \"Repository layout\" is missing these {folder}/ projects");
    }

    [Theory]
    [InlineData("web/")]
    [InlineData("fixtures/")]
    [InlineData("schemas/")]
    [InlineData("docs/")]
    public void Repository_layout_lists_the_top_level_folders(string folder)
    {
        Directory.Exists(RepositoryFiles.Path(folder.TrimEnd('/'))).ShouldBeTrue();
        Layout().ShouldContain($"\n{folder}", Case.Sensitive);
    }

    [Fact]
    public void Policy_table_matches_the_policies_each_role_satisfies()
    {
        var readme = RepositoryFiles.Read("README.md");
        foreach (var role in Roles.All)
        {
            var row = PolicyRow(role).Match(readme);
            row.Success.ShouldBeTrue($"README has a policy table row for {role}");
            var documented = row.Groups["policies"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var granted = Policies.All.Where(policy => Policies.RolesByPolicy[policy].Contains(role)).ToArray();

            documented.ShouldBe(granted, $"README policies of {role}");
        }
    }

    private static string Layout()
    {
        var readme = RepositoryFiles.Read("README.md");
        var start = readme.IndexOf("\n## Repository layout\n", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(0);
        var end = readme.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return end < 0 ? readme[start..] : readme[start..end];
    }

    private static Regex PolicyRow(string role) =>
        new($@"^\| {Regex.Escape(role)} \| [^|]+ \| (?<policies>[^|]+) \|$", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
