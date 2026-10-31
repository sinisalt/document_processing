using System.Reflection;
using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Infrastructure.Blobs;
using DocProc.Infrastructure.Classification;
using DocProc.Infrastructure.Pdf;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Security;
using DocProc.Llm;
using DocProc.Llm.Extraction;
using DocProc.Pipeline;

namespace DocProc.Api.Tests.Docs;

/// <summary>
/// docs/09 "Settings reference" names every bound setting of the hosts. A new property on an options class, or a new
/// key in a host's appsettings file, fails here until the table documents it.
/// </summary>
public sealed class SettingsReferenceTests
{
    /// <summary>Every options class the API and the worker bind, with the configuration section it is bound from.</summary>
    public static TheoryData<string, Type> OptionsClasses => new()
    {
        { PersistenceSettings.SectionName, typeof(PersistenceSettings) },
        { DevAuthOptions.SectionName, typeof(DevAuthOptions) },
        { OidcOptions.SectionName, typeof(OidcOptions) },
        { BlobStoreOptions.SectionName, typeof(BlobStoreOptions) },
        { SecretStoreOptions.SectionName, typeof(SecretStoreOptions) },
        { PipelineOptions.SectionName, typeof(PipelineOptions) },
        { PdfProcessingOptions.SectionName, typeof(PdfProcessingOptions) },
        { KeywordClassifierOptions.SectionName, typeof(KeywordClassifierOptions) },
        { HeuristicSplitterOptions.SectionName, typeof(HeuristicSplitterOptions) },
        { $"{LlmServiceCollectionExtensions.SectionName}:Replay", typeof(LlmReplayOptions) },
        { $"{LlmServiceCollectionExtensions.SectionName}:Extraction", typeof(LlmExtractionOptions) },
    };

    /// <summary>Properties that are not read from their own section.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        // Bound from the connection string the AppHost injects, not from Blobs:ConnectionString.
        [$"{BlobStoreOptions.SectionName}:{nameof(BlobStoreOptions.ConnectionString)}"] = $"ConnectionStrings:{BlobStoreOptions.ConnectionStringName}",
    };

    /// <summary>Keys read one by one rather than through an options class.</summary>
    public static TheoryData<string> LooseKeys => new()
    {
        PipelineOptions.RunInProcessKey,
        PersistenceHostExtensions.MigrateOnStartupKey,
        $"ConnectionStrings:{PersistenceHostExtensions.ConnectionStringName}",
        "Llm:Secrets:<secretRef>",
    };

    [Theory]
    [MemberData(nameof(OptionsClasses))]
    public void Every_property_of_an_options_class_is_documented(string section, Type options)
    {
        var reference = Reference();
        var properties = options.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => $"{section}:{p.Name}")
            .Select(key => Aliases.GetValueOrDefault(key, key))
            .ToList();
        properties.ShouldNotBeEmpty();

        properties.Where(key => !reference.Contains($"`{key}`", StringComparison.Ordinal))
            .ShouldBeEmpty($"docs/09 \"Settings reference\" does not document these {options.Name} settings");
    }

    [Theory]
    [MemberData(nameof(LooseKeys))]
    public void Keys_read_outside_an_options_class_are_documented(string key) =>
        Reference().ShouldContain($"`{key}`", Case.Sensitive);

    [Theory]
    [InlineData("src/DocProc.Api/appsettings.json")]
    [InlineData("src/DocProc.Api/appsettings.Development.json")]
    [InlineData("src/DocProc.Worker/appsettings.json")]
    public void Every_key_of_a_host_settings_file_is_documented(string file)
    {
        var reference = Reference();
        using var json = JsonDocument.Parse(RepositoryFiles.Read(file.Split('/')));
        var keys = Leaves(json.RootElement, prefix: null)
            .Where(key => !key.StartsWith("Logging:", StringComparison.Ordinal) && key != "AllowedHosts")
            .Select(key => key.StartsWith("Llm:Secrets:", StringComparison.Ordinal) ? "Llm:Secrets:<secretRef>" : key)
            .Distinct()
            .ToList();

        keys.Where(key => !reference.Contains($"`{key}`", StringComparison.Ordinal))
            .ShouldBeEmpty($"docs/09 \"Settings reference\" does not document these keys of {file}");
    }

    private static string Reference()
    {
        var doc = RepositoryFiles.Read("docs", "09-local-development.md");
        var start = doc.IndexOf("\n## Settings reference\n", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(0, "docs/09 has a \"Settings reference\" section");
        var end = doc.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return end < 0 ? doc[start..] : doc[start..end];
    }

    private static IEnumerable<string> Leaves(JsonElement element, string? prefix)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            yield return prefix!;
            yield break;
        }

        foreach (var property in element.EnumerateObject())
        {
            foreach (var leaf in Leaves(property.Value, prefix is null ? property.Name : $"{prefix}:{property.Name}"))
            {
                yield return leaf;
            }
        }
    }
}
