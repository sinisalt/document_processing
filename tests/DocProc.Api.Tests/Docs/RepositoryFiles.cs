namespace DocProc.Api.Tests.Docs;

/// <summary>Reads files of the checkout the tests were built from, for the documentation checks.</summary>
internal static class RepositoryFiles
{
    public static string Root { get; } = FindRoot();

    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    public static string Read(params string[] parts) => File.ReadAllText(Path(parts)).ReplaceLineEndings("\n");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "DocProc.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root (DocProc.slnx).");
    }
}
