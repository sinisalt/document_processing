using System.Text.RegularExpressions;

namespace DocProc.Api.Tests.Docs;

/// <summary>
/// Paths that the docs and code comments send a reader to must exist: a renamed file or a doc that was never written
/// turns into a dead end otherwise.
/// </summary>
public sealed class DocumentationLinkTests
{
    private static readonly string[] SkippedFolders = ["bin", "obj", "node_modules", "dist", ".git", "playwright-report", "test-results", "data", "worktrees"];
    private static readonly string[] TextExtensions = [".cs", ".csproj", ".md", ".ts", ".tsx", ".json", ".sh", ".yml", ".yaml", ".props"];

    private static readonly Regex DocReference = new(@"docs/\d{2}-[A-Za-z0-9-]+\.md", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex WebPackagePath = new(@"(?<![\w/@.-])(?:packages|apps)/[\w-]+/[\w./-]+\.(?:tsx?|json|css)\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex RepositoryPath = new(@"(?<![\w/@.-])(?:(?:src|tests)/DocProc\.[\w.]+|web|fixtures)/[\w./-]+\.(?:cs|csproj|json|tsx?|md|sh)\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    [Fact]
    public void Every_referenced_doc_exists()
    {
        var missing = References(TextFiles(RepositoryFiles.Root), DocReference)
            .Where(r => !File.Exists(RepositoryFiles.Path(r.Target.Split('/'))))
            .ToList();

        missing.ShouldBeEmpty("references to docs that do not exist");
    }

    [Fact]
    public void Web_package_paths_in_docs_and_web_sources_exist()
    {
        var files = TextFiles(RepositoryFiles.Path("docs")).Concat(TextFiles(RepositoryFiles.Path("web")));
        var missing = References(files, WebPackagePath)
            .Where(r => !File.Exists(RepositoryFiles.Path(["web", .. r.Target.Split('/')])))
            .ToList();

        missing.ShouldBeEmpty("web package paths that do not exist (relative to web/)");
    }

    [Fact]
    public void Repository_paths_in_the_docs_exist()
    {
        var files = TextFiles(RepositoryFiles.Path("docs")).Append(RepositoryFiles.Path("README.md"));
        var missing = References(files, RepositoryPath)
            .Where(r => !File.Exists(RepositoryFiles.Path(r.Target.Split('/'))) && !Directory.Exists(RepositoryFiles.Path(r.Target.Split('/'))))
            .ToList();

        missing.ShouldBeEmpty("repository paths in the docs that do not exist");
    }

    private static List<(string File, string Target)> References(IEnumerable<string> files, Regex pattern) =>
        [.. files.SelectMany(file => pattern.Matches(File.ReadAllText(file))
            .Select(m => (Path.GetRelativePath(RepositoryFiles.Root, file).Replace('\\', '/'), m.Value)))
            .Distinct()];

    private static IEnumerable<string> TextFiles(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) && !file.EndsWith("pnpm-lock.yaml", StringComparison.Ordinal))
            {
                yield return file;
            }
        }

        foreach (var child in Directory.EnumerateDirectories(folder))
        {
            if (SkippedFolders.Contains(Path.GetFileName(child), StringComparer.Ordinal) || (Path.GetFileName(child)?.StartsWith('.') ?? false))
            {
                continue;
            }

            foreach (var file in TextFiles(child))
            {
                yield return file;
            }
        }
    }
}
