using DocProc.Infrastructure.Security;

namespace DocProc.Api.Tests;

public sealed class FileSecretStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "docproc-tests", Guid.NewGuid().ToString("N"), "secrets");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        var parent = Path.GetDirectoryName(_root)!;
        if (Directory.Exists(parent))
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task Secrets_round_trip_overwrite_and_are_owner_only_files_named_by_hash()
    {
        var store = new FileSecretStore(_root);
        const string reference = "tenant/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/config-1";

        (await store.GetAsync(reference, Ct)).ShouldBeNull();
        await store.SetAsync(reference, "first", Ct);
        (await store.GetAsync(reference, Ct)).ShouldBe("first");
        await store.SetAsync(reference, "second", Ct);
        (await store.GetAsync(reference, Ct)).ShouldBe("second");

        var path = store.PathFor(reference);
        Path.GetDirectoryName(path).ShouldBe(store.RootPath, "the reference's slashes never become directories");
        Path.GetFileName(path).ShouldNotContain("tenant");
        File.Exists(path).ShouldBeTrue();
        Directory.GetFiles(store.RootPath, "*.tmp").ShouldBeEmpty();

        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(path).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(store.RootPath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // A second store over the same directory reads what the first wrote; different references never collide.
        var again = new FileSecretStore(_root);
        (await again.GetAsync(reference, Ct)).ShouldBe("second");
        (await again.GetAsync(reference + "x", Ct)).ShouldBeNull();
        await Should.ThrowAsync<ArgumentException>(() => again.SetAsync(" ", "x", Ct));
    }

    [Fact]
    public async Task Delete_removes_the_file_and_tolerates_a_reference_that_was_never_stored()
    {
        var store = new FileSecretStore(_root);
        const string reference = "tenant/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/channel/config-2";
        await store.SetAsync(reference, "to be deleted", Ct);
        File.Exists(store.PathFor(reference)).ShouldBeTrue();

        await store.DeleteAsync(reference, Ct);

        File.Exists(store.PathFor(reference)).ShouldBeFalse();
        (await store.GetAsync(reference, Ct)).ShouldBeNull();
        await store.DeleteAsync(reference, Ct);
        await store.DeleteAsync("never/stored", Ct);
        await Should.ThrowAsync<ArgumentException>(() => store.DeleteAsync(" ", Ct));
    }

    [Fact]
    public async Task In_memory_store_deletes_and_tolerates_unknown_references()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync("a", "1", Ct);
        await store.SetAsync("b", "2", Ct);

        await store.DeleteAsync("a", Ct);
        await store.DeleteAsync("never", Ct);

        store.Count.ShouldBe(1);
        (await store.GetAsync("a", Ct)).ShouldBeNull();
        (await store.GetAsync("b", Ct)).ShouldBe("2");
    }
}
