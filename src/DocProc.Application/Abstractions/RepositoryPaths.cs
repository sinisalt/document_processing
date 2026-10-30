namespace DocProc.Application.Abstractions;

/// <summary>
/// Resolves development paths that name a folder of the repository rather than of the host project, such as
/// <c>fixtures/demo/llm-replay</c> (docs/09 "Demo data"): under <c>dotnet run</c> the content root is the project folder
/// (<c>src/DocProc.Api</c>), under the test runner it is the test project or its output folder, and the fixtures live at
/// the repository root.
/// </summary>
public static class RepositoryPaths
{
    /// <summary>
    /// An absolute <paramref name="path"/> is returned as it is (normalised). A relative one is anchored to the first
    /// folder at or above <paramref name="contentRootPath"/> that contains the path's first segment (<c>fixtures</c> for
    /// <c>fixtures/demo</c>), so the same setting works from every project folder. When no ancestor has that segment the
    /// path is anchored to the content root itself, which keeps the result predictable for a folder that does not exist yet.
    /// </summary>
    public static string ResolveUpwards(string path, string contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        if (Path.IsPathFullyQualified(path))
        {
            return Path.GetFullPath(path);
        }

        var root = Path.GetFullPath(contentRootPath);
        var firstSegment = FirstSegment(path);
        if (firstSegment is not null)
        {
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, firstSegment)))
                {
                    return Path.GetFullPath(path, directory.FullName);
                }
            }
        }

        return Path.GetFullPath(path, root);
    }

    /// <summary>The first real folder name of a relative path: <c>./fixtures/demo</c> gives <c>fixtures</c>; <c>../x</c> gives null (nothing to search for).</summary>
    private static string? FirstSegment(string path)
    {
        foreach (var segment in path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            return segment == ".." ? null : segment;
        }

        return null;
    }
}
