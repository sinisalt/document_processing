using System.Text.Json;
using DocProc.Llm.Schema;

namespace DocProc.Llm.Tests;

/// <summary>
/// The schemas sent to providers are committed under <c>schemas/</c> so a contract change is visible in review and
/// forces a version bump. Set <c>UPDATE_SCHEMAS=1</c> to rewrite the snapshots after an intentional change.
/// </summary>
public class SchemaSnapshotTests
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static TheoryData<string> Names => new(CanonicalSchemas.All().Select(s => s.Name));

    [Theory]
    [MemberData(nameof(Names))]
    public void Committed_schema_snapshot_matches_the_generated_schema(string name)
    {
        var schema = CanonicalSchemas.All().Single(s => s.Name == name);
        var path = Path.Combine(RepositoryRoot(), "schemas", $"{schema.Name}.v{schema.Version}.json");
        var generated = JsonSerializer.Serialize(schema.Schema, Pretty) + Environment.NewLine;

        if (Environment.GetEnvironmentVariable("UPDATE_SCHEMAS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, generated);
        }

        File.Exists(path).ShouldBeTrue($"Missing snapshot {path}. Run the tests once with UPDATE_SCHEMAS=1.");
        File.ReadAllText(path).ReplaceLineEndings().ShouldBe(generated.ReplaceLineEndings(),
            $"The generated schema differs from {path}. If the change is intended, bump the version in CanonicalSchemas and rerun with UPDATE_SCHEMAS=1.");
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocProc.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root (DocProc.slnx).");
    }
}
