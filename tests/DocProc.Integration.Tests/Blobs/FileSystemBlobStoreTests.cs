using System.Security.Cryptography;
using DocProc.Application.Abstractions;
using DocProc.Infrastructure.Blobs;
using Microsoft.Extensions.Time.Testing;

namespace DocProc.Integration.Tests.Blobs;

/// <summary>The local content-addressed store (docs/03 "Idempotency": keys are <c>{tenant}/{sha256}</c>) against a temp directory.</summary>
public sealed class FileSystemBlobStoreTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherTenant = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "docproc-tests", Guid.NewGuid().ToString("N"));
    private readonly FileSystemBlobStore _store;

    public FileSystemBlobStoreTests()
    {
        _store = new FileSystemBlobStore(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Put_stores_under_tenant_and_hash_and_round_trips_bytes_and_content_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = "%PDF-1.7 hello"u8.ToArray();
        var expectedSha = ContentHash.Sha256Hex(bytes);

        var reference = await _store.PutAsync(Tenant, new MemoryStream(bytes), "application/pdf", ct);

        reference.Container.ShouldBe(BlobContainers.Raw);
        reference.Key.ShouldBe($"{Tenant:D}/{expectedSha}");
        reference.Sha256.ShouldBe(expectedSha);
        reference.SizeBytes.ShouldBe(bytes.Length);
        reference.ContentType.ShouldBe("application/pdf");

        var path = Path.Combine(_root, "raw", Tenant.ToString("D"), expectedSha);
        File.Exists(path).ShouldBeTrue();
        File.Exists(path + FileSystemBlobStore.MetadataSuffix).ShouldBeTrue();

        (await _store.ExistsAsync(reference, ct)).ShouldBeTrue();
        await using var read = await _store.OpenReadAsync(reference, ct);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy, ct);
        copy.ToArray().ShouldBe(bytes);

        var found = await _store.FindAsync(reference.Container, reference.Key, ct);
        found.ShouldBe(reference);
    }

    [Fact]
    public async Task Putting_identical_content_twice_is_a_no_op_and_keeps_the_first_content_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = new byte[1024];
        Random.Shared.NextBytes(bytes);

        var first = await _store.PutAsync(Tenant, new MemoryStream(bytes), "image/png", ct);
        var second = await _store.PutAsync(Tenant, new MemoryStream(bytes), "application/octet-stream", ct);

        second.ShouldBe(first);
        Directory.GetFiles(Path.Combine(_root, "raw", Tenant.ToString("D"))).Length.ShouldBe(2); // blob + sidecar
    }

    [Fact]
    public async Task Same_content_for_two_tenants_lives_under_two_prefixes()
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = "shared bytes"u8.ToArray();

        var a = await _store.PutAsync(Tenant, new MemoryStream(bytes), "text/plain", ct);
        var b = await _store.PutAsync(OtherTenant, new MemoryStream(bytes), "text/plain", ct);

        a.Sha256.ShouldBe(b.Sha256);
        a.Key.ShouldNotBe(b.Key);
        a.Key.ShouldStartWith(Tenant.ToString("D"));
        b.Key.ShouldStartWith(OtherTenant.ToString("D"));
    }

    [Fact]
    public async Task Container_overload_writes_to_that_container()
    {
        var ct = TestContext.Current.CancellationToken;

        var reference = await _store.PutAsync(BlobContainers.Derived, Tenant, new MemoryStream("page-1"u8.ToArray()), "image/png", ct);

        reference.Container.ShouldBe(BlobContainers.Derived);
        File.Exists(Path.Combine(_root, "derived", Tenant.ToString("D"), reference.Sha256)).ShouldBeTrue();
        (await _store.ExistsAsync(reference with { Container = BlobContainers.Raw }, ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Large_non_seekable_stream_is_hashed_correctly_without_buffering()
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = new byte[5 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);
        var expected = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var reference = await _store.PutAsync(Tenant, new NonSeekableStream(new MemoryStream(bytes)), "application/pdf", ct);

        reference.Sha256.ShouldBe(expected);
        reference.SizeBytes.ShouldBe(bytes.LongLength);
        new FileInfo(_store.PathFor(reference.Container, reference.Key)).Length.ShouldBe(bytes.LongLength);
        Directory.GetFiles(Path.Combine(_root, ".spool")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Concurrent_puts_of_the_same_content_do_not_corrupt_the_blob()
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = new byte[1024 * 1024];
        Random.Shared.NextBytes(bytes);
        var expected = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var puts = Enumerable.Range(0, 8)
            .Select(_ => _store.PutAsync(Tenant, new NonSeekableStream(new MemoryStream(bytes)), "application/pdf", ct));
        var references = await Task.WhenAll(puts);

        references.ShouldAllBe(r => r.Sha256 == expected && r.SizeBytes == bytes.Length && r.ContentType == "application/pdf");
        references.Distinct().Count().ShouldBe(1);

        var stored = await File.ReadAllBytesAsync(_store.PathFor(references[0].Container, references[0].Key), ct);
        stored.ShouldBe(bytes);
        Directory.GetFiles(Path.Combine(_root, ".spool")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failing_upload_leaves_no_spool_file_behind()
    {
        var ct = TestContext.Current.CancellationToken;

        await Should.ThrowAsync<IOException>(() => _store.PutAsync(Tenant, new FailingStream(), "application/pdf", ct));

        Directory.GetFiles(Path.Combine(_root, ".spool")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Unknown_reference_does_not_exist_and_opening_it_is_a_blob_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var reference = new BlobRef(BlobContainers.Raw, $"{Tenant:D}/{new string('0', 64)}", new string('0', 64), 0, "text/plain");

        (await _store.ExistsAsync(reference, ct)).ShouldBeFalse();
        (await _store.FindAsync(reference.Container, reference.Key, ct)).ShouldBeNull();
        var error = await Should.ThrowAsync<BlobNotFoundException>(() => _store.OpenReadAsync(reference, ct));
        error.Reference.ShouldBe(reference);
        error.Message.ShouldContain(reference.Key);
    }

    [Fact]
    public async Task Read_urls_are_not_supported_so_the_api_must_stream_the_bytes()
    {
        // A file: URI would be a permanent link leaking server paths (the contract asks for a short-lived URL).
        var ct = TestContext.Current.CancellationToken;
        var reference = await _store.PutAsync(Tenant, new MemoryStream("x"u8.ToArray()), "text/plain", ct);

        await Should.ThrowAsync<NotSupportedException>(() => _store.GetReadUrlAsync(reference, TimeSpan.FromMinutes(5), ct));
    }

    [Fact]
    public async Task Stale_spool_files_are_swept_when_a_store_is_constructed_and_fresh_ones_are_kept()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        Directory.CreateDirectory(_store.SpoolPath);
        var stale = Path.Combine(_store.SpoolPath, "crashed-upload.part");
        var fresh = Path.Combine(_store.SpoolPath, "in-progress.part");
        var unrelated = Path.Combine(_store.SpoolPath, "notes.txt");
        await File.WriteAllBytesAsync(stale, new byte[16], ct);
        await File.WriteAllBytesAsync(fresh, new byte[16], ct);
        await File.WriteAllBytesAsync(unrelated, new byte[16], ct);
        File.SetLastWriteTimeUtc(stale, clock.GetUtcNow().AddHours(-2).UtcDateTime);
        File.SetLastWriteTimeUtc(fresh, clock.GetUtcNow().AddMinutes(-5).UtcDateTime);
        File.SetLastWriteTimeUtc(unrelated, clock.GetUtcNow().AddDays(-3).UtcDateTime);

        _ = new FileSystemBlobStore(_root, clock);

        File.Exists(stale).ShouldBeFalse();
        File.Exists(fresh).ShouldBeTrue();
        File.Exists(unrelated).ShouldBeTrue();
    }

    [Theory]
    [InlineData("../etc", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("raw/../..", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("Raw", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("raw", "../../passwd")]
    [InlineData("raw", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/../../passwd")]
    [InlineData("raw", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/0000000000000000000000000000000000000000000000000000000000000000/../x")]
    [InlineData("raw", "..\\..\\passwd")]
    [InlineData("raw", "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA/0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task Hostile_container_or_key_is_rejected_before_touching_the_file_system(string container, string key)
    {
        var ct = TestContext.Current.CancellationToken;
        var reference = new BlobRef(container, key, new string('0', 64), 0, "text/plain");

        await Should.ThrowAsync<ArgumentException>(() => _store.ExistsAsync(reference, ct));
        await Should.ThrowAsync<ArgumentException>(() => _store.OpenReadAsync(reference, ct));
        await Should.ThrowAsync<ArgumentException>(() => _store.GetReadUrlAsync(reference, TimeSpan.FromMinutes(1), ct));
        await Should.ThrowAsync<ArgumentException>(() => _store.FindAsync(container, key, ct));
        if (container != BlobContainers.Raw)
        {
            await Should.ThrowAsync<ArgumentException>(() => _store.PutAsync(container, Tenant, new MemoryStream(), "text/plain", ct));
        }
    }

    [Fact]
    public void Blob_keys_follow_the_documented_scheme()
    {
        var sha = new string('a', 64);

        BlobKeys.ForContent(Tenant, sha).ShouldBe($"{Tenant:D}/{sha}");
        BlobKeys.Sha256Of($"{Tenant:D}/{sha}").ShouldBe(sha);
        Should.Throw<ArgumentException>(() => BlobKeys.ForContent(Tenant, sha.ToUpperInvariant()));
        Should.Throw<ArgumentException>(() => BlobKeys.ForContent(Tenant, "abc"));
        Should.NotThrow(() => BlobKeys.EnsureValidContainer("raw-documents"));
        Should.Throw<ArgumentException>(() => BlobKeys.EnsureValidContainer("raw--documents"));
        Should.Throw<ArgumentException>(() => BlobKeys.EnsureValidContainer("ab"));
    }

    /// <summary>Hides seeking so the store has to spool, as it would for an HTTP request body.</summary>
    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
