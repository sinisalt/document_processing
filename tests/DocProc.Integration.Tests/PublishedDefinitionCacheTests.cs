using DocProc.Definitions;
using DocProc.Definitions.Compilation;
using DocProc.Infrastructure.Catalogue;

namespace DocProc.Integration.Tests;

/// <summary>
/// The process-wide cache of published type versions: shared by every tenant, so it is bounded by the size of the JSON
/// its versions were read from as well as by their number (one version may hold several megabytes).
/// </summary>
public sealed class PublishedDefinitionCacheTests
{
    private static readonly TypeDefinition Definition = StandardTemplates.Find("std.price-list")!;

    [Fact]
    public void The_cache_is_cleared_before_it_would_pass_its_size_budget()
    {
        var cache = new PublishedDefinitionCache(maxCharacters: 1000);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        cache.Add(first, Definition, characters: 600);
        cache.Add(first, Definition, characters: 600).ShouldBeSameAs(Definition, "a version already cached is not counted twice");
        cache.Count.ShouldBe(1);
        cache.Characters.ShouldBe(600);

        cache.Add(second, Definition, characters: 600);

        cache.Count.ShouldBe(1, "the budget would have passed, so the cache started again");
        cache.TryGet(first, out _).ShouldBeFalse();
        cache.TryGet(second, out var kept).ShouldBeTrue();
        kept.ShouldBeSameAs(Definition);
        cache.Characters.ShouldBe(600);
    }

    [Fact]
    public void The_weight_of_a_version_is_its_stored_json()
    {
        var stored = new StoredArtifacts(new string('s', 10), new string('l', 20), "doc_x", "1", "hash", new string('f', 30), new string('d', 40), "c");

        PublishedDefinitionCache.SizeOf(new string('j', 5), stored).ShouldBe(105);
        PublishedDefinitionCache.SizeOf(new string('j', 5), null).ShouldBe(5);
        new PublishedDefinitionCache().Characters.ShouldBe(0);
        Should.Throw<ArgumentOutOfRangeException>(() => new PublishedDefinitionCache(0));
    }
}
