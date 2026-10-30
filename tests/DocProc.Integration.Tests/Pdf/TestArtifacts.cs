namespace DocProc.Integration.Tests.Pdf;

/// <summary>
/// Optional dump of generated fixtures and rendered pages for eyeballing: set <c>DOCPROC_TEST_ARTIFACTS</c> to a
/// directory before running the tests. Without the variable nothing is written and the tests stay hermetic.
/// </summary>
internal static class TestArtifacts
{
    public const string EnvironmentVariable = "DOCPROC_TEST_ARTIFACTS";

    private static readonly string? Directory = Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } dir ? dir : null;

    public static void Save(string fileName, ReadOnlyMemory<byte> content)
    {
        if (Directory is null)
        {
            return;
        }

        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllBytes(Path.Combine(Directory, fileName), content.ToArray());
    }
}
